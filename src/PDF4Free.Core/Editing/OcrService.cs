using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Windows;
using PdfSharp.Drawing;
using PDF4Free.Core.Pdfium;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PDF4Free.Core.Editing;

public sealed record OcrWord(string Text, Rect PixelRect);

public sealed record OcrLine(IReadOnlyList<OcrWord> Words)
{
    public string Text => string.Join(' ', Words.Select(w => w.Text));
}

public sealed record OcrPage(int PixelWidth, int PixelHeight, IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text));
}

public sealed record OcrLanguage(string Tag, string DisplayName);

/// <summary>Text recognition using the OCR engine built into Windows; adds an invisible, searchable text layer to pages.</summary>
public static class OcrService
{
    public static IReadOnlyList<OcrLanguage> Languages() =>
        [.. OcrEngine.AvailableRecognizerLanguages.Select(l => new OcrLanguage(l.LanguageTag, l.DisplayName))];

    private static OcrEngine CreateEngine(string? languageTag)
    {
        OcrEngine? engine = null;
        if (!string.IsNullOrEmpty(languageTag))
        {
            var language = new Language(languageTag);
            if (OcrEngine.IsLanguageSupported(language)) engine = OcrEngine.TryCreateFromLanguage(language);
        }
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null && OcrEngine.AvailableRecognizerLanguages.Count > 0)
        {
            engine = OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);
        }
        return engine ?? throw new InvalidOperationException(
            "Windows has no OCR language installed. Add one under Settings > Time & language > Language & region (Optional features: Optical character recognition).");
    }

    /// <summary>Recognises text in 32-bit BGR(A) pixels.</summary>
    public static async Task<OcrPage> RecognizeAsync(byte[] pixels, int width, int height, string? languageTag = null)
    {
        var engine = CreateEngine(languageTag);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap);
        var lines = result.Lines
            .Select(line => new OcrLine([.. line.Words.Select(w => new OcrWord(w.Text, new Rect(w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height)))]))
            .ToList();
        return new OcrPage(width, height, lines);
    }

    /// <summary>Renders a page and recognises its text. The resolution is lowered if the page would exceed the engine's limit.</summary>
    public static async Task<(OcrPage Page, double Scale)> RecognizePageAsync(PdfiumDocument doc, int pageIndex, string? languageTag, double dpi = 200)
    {
        var geometry = doc.GetGeometry(pageIndex);
        var scale = dpi / 72.0;
        var limit = Math.Max(1000, (int)OcrEngine.MaxImageDimension - 10);
        var largest = Math.Max(geometry.ViewWidth, geometry.ViewHeight) * scale;
        if (largest > limit) scale *= limit / largest;
        var width = Math.Max(1, (int)Math.Round(geometry.ViewWidth * scale));
        var height = Math.Max(1, (int)Math.Round(geometry.ViewHeight * scale));
        var pixels = doc.Render(pageIndex, width, height);
        return (await RecognizeAsync(pixels, width, height, languageTag), scale);
    }

    /// <summary>
    /// Adds a hidden text layer to the given pages so they can be searched, selected and copied.
    /// Pages that already carry real text are left alone unless <paramref name="force"/> is set.
    /// </summary>
    public static async Task<(byte[] Result, int PagesRecognised, int Words)> AddTextLayerAsync(
        byte[] source,
        IReadOnlyList<int> pages,
        string? languageTag = null,
        bool force = false,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancel = default)
    {
        var recognised = new Dictionary<int, (OcrPage Page, double Scale)>();
        using (var doc = PdfiumDocument.Load(source))
        {
            for (var i = 0; i < pages.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var index = pages[i];
                if (index >= 0 && index < doc.PageCount && (force || doc.GetText(index).Text.Count(char.IsLetterOrDigit) < 12))
                {
                    recognised[index] = await RecognizePageAsync(doc, index, languageTag);
                }
                progress?.Report((i + 1, pages.Count));
            }
        }
        var words = recognised.Values.Sum(r => r.Page.Lines.Sum(l => l.Words.Count));
        if (words == 0) return (source, 0, 0);
        var result = PdfIO.Modify(source, doc =>
        {
            foreach (var (index, (page, scale)) in recognised) DrawHiddenText(doc.Pages[index], page, scale);
        });
        return (result, recognised.Count(r => r.Value.Page.Lines.Count > 0), words);
    }

    private static void DrawHiddenText(PdfSharp.Pdf.PdfPage pdfPage, OcrPage page, double scale)
    {
        using (var canvas = PageCanvas.Open(pdfPage))
        {
            DrawWords(canvas.Gfx, page, scale);
        }
        HideLastContentStream(pdfPage);
    }

    /// <summary>
    /// Wraps the content stream that was just appended in "q 3 Tr ... Q": text rendering mode 3 means
    /// "neither fill nor stroke", the standard way to store recognised text that can be searched but not seen.
    /// </summary>
    private static void HideLastContentStream(PdfSharp.Pdf.PdfPage pdfPage)
    {
        var contents = pdfPage.Contents;
        if (contents.Elements.Count == 0) return;
        if (PdfX.Resolve(contents.Elements[contents.Elements.Count - 1]) is not PdfSharp.Pdf.PdfDictionary last || last.Stream is null) return;
        var body = last.Elements.ContainsKey("/Filter") ? last.Stream.UnfilteredValue : last.Stream.Value;
        last.Elements.Remove("/Filter");
        last.Elements.Remove("/DecodeParms");
        var head = Encoding.ASCII.GetBytes("q 3 Tr\n");
        var tail = Encoding.ASCII.GetBytes("\nQ\n");
        var wrapped = new byte[head.Length + body.Length + tail.Length];
        Buffer.BlockCopy(head, 0, wrapped, 0, head.Length);
        Buffer.BlockCopy(body, 0, wrapped, head.Length, body.Length);
        Buffer.BlockCopy(tail, 0, wrapped, head.Length + body.Length, tail.Length);
        last.Stream.Value = wrapped;
    }

    private static void DrawWords(XGraphics gfx, OcrPage page, double scale)
    {
        var invisible = XBrushes.Black; // Never painted: the whole stream is switched to invisible text afterwards.
        var fonts = new Dictionary<int, XFont>();
        foreach (var line in page.Lines)
        {
            if (line.Words.Count == 0) continue;
            var top = line.Words.Min(w => w.PixelRect.Top) / scale;
            var bottom = line.Words.Max(w => w.PixelRect.Bottom) / scale;
            var lineHeight = Math.Max(2, bottom - top);
            var sizeKey = Math.Max(4, (int)Math.Round(lineHeight * 0.85 * 2));
            if (!fonts.TryGetValue(sizeKey, out var font)) fonts[sizeKey] = font = new XFont("Arial", sizeKey / 2.0);
            var baseline = bottom - lineHeight * 0.2;
            foreach (var word in line.Words)
            {
                var measured = gfx.MeasureString(word.Text, font).Width;
                if (measured <= 0) continue;
                var state = gfx.Save();
                gfx.TranslateTransform(word.PixelRect.Left / scale, baseline);
                gfx.ScaleTransform(word.PixelRect.Width / scale / measured, 1);
                gfx.DrawString(word.Text, font, invisible, new XPoint(0, 0));
                gfx.Restore(state);
            }
        }
    }

    /// <summary>Recognises one page and returns its plain text (used by "copy text from image").</summary>
    public static async Task<string> ReadPageTextAsync(PdfiumDocument doc, int pageIndex, string? languageTag = null)
    {
        var (page, _) = await RecognizePageAsync(doc, pageIndex, languageTag);
        var builder = new StringBuilder();
        foreach (var line in page.Lines) builder.AppendLine(line.Text);
        return builder.ToString();
    }
}
