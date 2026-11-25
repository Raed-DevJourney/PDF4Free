using System.Globalization;
using System.Windows;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PDF4Free.Core.Editing;

public enum WatermarkLayer { InFront, Behind }

public sealed class WatermarkOptions
{
    public string Text { get; init; } = "CONFIDENTIAL";
    public byte[]? Image { get; init; }
    public TextStyle Style { get; init; } = new("Arial", 60, true, false, 0xC62828);
    public double Opacity { get; init; } = 0.25;
    public double RotationDegrees { get; init; } = 45;
    public double ImageScale { get; init; } = 0.5;
    public bool Tile { get; init; }
    public WatermarkLayer Layer { get; init; } = WatermarkLayer.InFront;
    public IReadOnlyList<int>? Pages { get; init; }
}

public sealed class HeaderFooterOptions
{
    public string HeaderLeft { get; init; } = "";
    public string HeaderCenter { get; init; } = "";
    public string HeaderRight { get; init; } = "";
    public string FooterLeft { get; init; } = "";
    public string FooterCenter { get; init; } = "";
    public string FooterRight { get; init; } = "";
    public TextStyle Style { get; init; } = new("Arial", 10);
    public double MarginTop { get; init; } = 36;
    public double MarginBottom { get; init; } = 36;
    public double MarginSide { get; init; } = 54;
    public int StartPageNumber { get; init; } = 1;
    public string FileName { get; init; } = "";
    public string BatesPrefix { get; init; } = "";
    public string BatesSuffix { get; init; } = "";
    public int BatesDigits { get; init; } = 6;
    public int BatesStart { get; init; } = 1;
    public IReadOnlyList<int>? Pages { get; init; }

    /// <summary>The supported placeholders, for display in the UI.</summary>
    public const string Placeholders = "{page}  {pages}  {date}  {time}  {file}  {bates}";
}

/// <summary>Draws new content onto pages. Positions are view points (top-left origin of the displayed page).</summary>
public static class ContentService
{
    // ------------------------------------------------------------------ text and images

    /// <summary>Adds text. <paramref name="origin"/> is the top-left corner of the first line, or its baseline start when <paramref name="originIsBaseline"/>.</summary>
    public static byte[] AddText(byte[] source, int pageIndex, Point origin, string text, TextStyle style, bool originIsBaseline = false, double maxWidth = 0) =>
        PdfIO.Modify(source, doc =>
        {
            using var canvas = PageCanvas.Open(doc.Pages[pageIndex]);
            var font = PageCanvas.Font(style);
            var ascent = style.Size * font.CellAscent / Math.Max(1, font.FontFamily.GetEmHeight(font.Style));
            var leading = font.GetHeight();
            var baseline = originIsBaseline ? origin.Y : origin.Y + ascent;
            foreach (var line in WrapLines(canvas, text, style, maxWidth))
            {
                canvas.DrawLine(line, style, origin.X, baseline);
                baseline += leading;
            }
        });

    private static IEnumerable<string> WrapLines(PageCanvas canvas, string text, TextStyle style, double maxWidth)
    {
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (maxWidth <= 0)
            {
                yield return paragraph;
                continue;
            }
            var current = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && canvas.Measure(candidate, style) > maxWidth)
                {
                    yield return current;
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            yield return current;
        }
    }

    /// <summary>Places an image (PNG, JPEG, BMP, GIF or TIFF bytes) inside the given view rectangle.</summary>
    public static byte[] AddImage(byte[] source, int pageIndex, Rect viewRect, byte[] imageData, double opacity = 1) =>
        PdfIO.Modify(source, doc =>
        {
            using var canvas = PageCanvas.Open(doc.Pages[pageIndex]);
            using var image = PageCanvas.ToXImage(imageData);
            canvas.Gfx.DrawImage(image, new XRect(viewRect.X, viewRect.Y, viewRect.Width, viewRect.Height));
        });

    /// <summary>Covers an area with a solid colour (used to hide content before typing over it).</summary>
    public static byte[] FillRectangle(byte[] source, int pageIndex, Rect viewRect, uint color) =>
        PdfIO.Modify(source, doc =>
        {
            using var canvas = PageCanvas.Open(doc.Pages[pageIndex]);
            canvas.Gfx.DrawRectangle(new XSolidBrush(PageCanvas.Color(color)), new XRect(viewRect.X, viewRect.Y, viewRect.Width, viewRect.Height));
        });

    /// <summary>Draws a shape outline or filled shape into the page content (not an annotation).</summary>
    public static byte[] DrawShape(byte[] source, int pageIndex, ShapeKind kind, Rect viewRect, uint stroke, double width, uint? fill) =>
        PdfIO.Modify(source, doc =>
        {
            using var canvas = PageCanvas.Open(doc.Pages[pageIndex]);
            var rect = new XRect(viewRect.X, viewRect.Y, viewRect.Width, viewRect.Height);
            var pen = new XPen(PageCanvas.Color(stroke), width);
            XBrush? brush = fill is { } f ? new XSolidBrush(PageCanvas.Color(f)) : null;
            if (kind == ShapeKind.Rectangle)
            {
                if (brush is null) canvas.Gfx.DrawRectangle(pen, rect);
                else canvas.Gfx.DrawRectangle(pen, brush, rect);
            }
            else
            {
                if (brush is null) canvas.Gfx.DrawEllipse(pen, rect);
                else canvas.Gfx.DrawEllipse(pen, brush, rect);
            }
        });

    // ------------------------------------------------------------------ watermark and background

    public static byte[] AddWatermark(byte[] source, WatermarkOptions options) =>
        PdfIO.Modify(source, doc =>
        {
            using var image = options.Image is null ? null : PageCanvas.ToXImage(options.Image);
            foreach (var index in PagesOrAll(doc, options.Pages))
            {
                using var canvas = PageCanvas.Open(doc.Pages[index], options.Layer == WatermarkLayer.Behind);
                foreach (var centre in WatermarkCentres(canvas, options))
                {
                    DrawWatermark(canvas, options, image, centre);
                }
            }
        });

    private static IEnumerable<Point> WatermarkCentres(PageCanvas canvas, WatermarkOptions options)
    {
        if (!options.Tile)
        {
            yield return new Point(canvas.Width / 2, canvas.Height / 2);
            yield break;
        }
        var stepX = Math.Max(120, canvas.Measure(options.Text, options.Style) + 60);
        var stepY = Math.Max(120, options.Style.Size * 5);
        for (var y = stepY / 2; y < canvas.Height + stepY / 2; y += stepY)
        {
            for (var x = stepX / 2; x < canvas.Width + stepX / 2; x += stepX) yield return new Point(x, y);
        }
    }

    private static void DrawWatermark(PageCanvas canvas, WatermarkOptions options, XImage? image, Point centre)
    {
        var gfx = canvas.Gfx;
        var state = gfx.Save();
        gfx.TranslateTransform(centre.X, centre.Y);
        gfx.RotateTransform(-options.RotationDegrees);
        if (image is not null)
        {
            var width = canvas.Width * options.ImageScale;
            var height = width * image.PixelHeight / Math.Max(1, image.PixelWidth);
            DrawImageWithOpacity(gfx, image, new XRect(-width / 2, -height / 2, width, height), options.Opacity);
        }
        else
        {
            var width = canvas.Measure(options.Text, options.Style);
            canvas.DrawLine(options.Text, options.Style, -width / 2, options.Style.Size * 0.35, options.Opacity);
        }
        gfx.Restore(state);
    }

    private static void DrawImageWithOpacity(XGraphics gfx, XImage image, XRect rect, double opacity)
    {
        gfx.DrawImage(image, rect);
        if (opacity >= 0.999) return;
        // PDFsharp has no image alpha: veil the image with translucent white to fade it.
        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb((int)Math.Round((1 - opacity) * 255), 255, 255, 255)), rect);
    }

    /// <summary>Paints a solid colour behind the content of the chosen pages.</summary>
    public static byte[] AddBackground(byte[] source, uint color, IReadOnlyList<int>? pages = null) =>
        PdfIO.Modify(source, doc =>
        {
            foreach (var index in PagesOrAll(doc, pages))
            {
                using var canvas = PageCanvas.Open(doc.Pages[index], behind: true);
                canvas.Gfx.DrawRectangle(new XSolidBrush(PageCanvas.Color(color)), new XRect(0, 0, canvas.Width, canvas.Height));
            }
        });

    // ------------------------------------------------------------------ header, footer, Bates numbers

    public static byte[] AddHeaderFooter(byte[] source, HeaderFooterOptions options) =>
        PdfIO.Modify(source, doc =>
        {
            var pages = PagesOrAll(doc, options.Pages).ToList();
            for (var ordinal = 0; ordinal < pages.Count; ordinal++)
            {
                using var canvas = PageCanvas.Open(doc.Pages[pages[ordinal]]);
                string Resolve(string template) => ResolvePlaceholders(template, options, ordinal, doc.PageCount);
                var top = options.MarginTop + options.Style.Size * 0.8;
                var bottom = canvas.Height - options.MarginBottom;
                DrawAligned(canvas, options, Resolve(options.HeaderLeft), Resolve(options.HeaderCenter), Resolve(options.HeaderRight), top);
                DrawAligned(canvas, options, Resolve(options.FooterLeft), Resolve(options.FooterCenter), Resolve(options.FooterRight), bottom);
            }
        });

    private static void DrawAligned(PageCanvas canvas, HeaderFooterOptions options, string left, string centre, string right, double baseline)
    {
        var style = options.Style;
        canvas.DrawLine(left, style, options.MarginSide, baseline);
        if (centre.Length > 0) canvas.DrawLine(centre, style, (canvas.Width - canvas.Measure(centre, style)) / 2, baseline);
        if (right.Length > 0) canvas.DrawLine(right, style, canvas.Width - options.MarginSide - canvas.Measure(right, style), baseline);
    }

    private static string ResolvePlaceholders(string template, HeaderFooterOptions options, int ordinal, int pageCount)
    {
        if (template.Length == 0) return template;
        var bates = options.BatesPrefix
                    + (options.BatesStart + ordinal).ToString("D" + Math.Clamp(options.BatesDigits, 1, 15), CultureInfo.InvariantCulture)
                    + options.BatesSuffix;
        return template
            .Replace("{page}", (options.StartPageNumber + ordinal).ToString(CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{pages}", (options.StartPageNumber + pageCount - 1).ToString(CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", DateTime.Now.ToShortDateString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", DateTime.Now.ToShortTimeString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{file}", options.FileName, StringComparison.OrdinalIgnoreCase)
            .Replace("{bates}", bates, StringComparison.OrdinalIgnoreCase);
    }

    internal static IEnumerable<int> PagesOrAll(PdfDocument doc, IReadOnlyList<int>? pages) =>
        (pages ?? Enumerable.Range(0, doc.PageCount).ToList()).Where(i => i >= 0 && i < doc.PageCount).Distinct().OrderBy(i => i);
}
