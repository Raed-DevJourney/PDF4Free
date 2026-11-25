using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Core.Editing;

/// <summary>
/// Permanent redaction. Marks are ordinary /Redact annotations; applying them replaces each affected page with
/// an image of itself with the marked areas blacked out, so no text, vector data or hidden layer can survive beneath.
/// </summary>
public static class RedactionService
{
    /// <summary>The marked areas of every page, read from the /Redact annotations.</summary>
    public static Dictionary<int, List<PdfRect>> ReadMarks(byte[] source)
    {
        using var doc = PdfIO.Open(source, PdfDocumentOpenMode.Import);
        var marks = new Dictionary<int, List<PdfRect>>();
        for (var p = 0; p < doc.PageCount; p++)
        {
            foreach (var item in PdfX.Annots(doc.Pages[p], false).Elements)
            {
                if (PdfX.Resolve(item) is not PdfDictionary annot || annot.Name("/Subtype") != "/Redact") continue;
                if (!marks.TryGetValue(p, out var list)) marks[p] = list = [];
                list.AddRange(AreasOf(annot));
            }
        }
        return marks;
    }

    private static IEnumerable<PdfRect> AreasOf(PdfDictionary annot)
    {
        if (annot.Array("/QuadPoints") is { } quads && quads.Elements.Count >= 8)
        {
            var n = quads.Numbers();
            for (var i = 0; i + 7 < n.Length; i += 8)
            {
                var xs = new[] { n[i], n[i + 2], n[i + 4], n[i + 6] };
                var ys = new[] { n[i + 1], n[i + 3], n[i + 5], n[i + 7] };
                yield return new PdfRect(xs.Min(), ys.Min(), xs.Max(), ys.Max());
            }
            yield break;
        }
        if (annot.Rect("/Rect") is { } rect) yield return rect;
    }

    /// <summary>Marks every search hit for redaction and returns how many areas were marked.</summary>
    public static byte[] MarkHits(byte[] source, IEnumerable<SearchHit> hits, string author, out int marked)
    {
        var byPage = hits.GroupBy(h => h.PageIndex).ToDictionary(g => g.Key, g => g.SelectMany(h => h.Rects).ToList());
        marked = byPage.Values.Sum(v => v.Count);
        if (marked == 0) return source;
        return PdfIO.Modify(source, doc =>
        {
            foreach (var (page, rects) in byPage) AnnotationService.AddRedactionMarks(doc, page, rects, author);
        });
    }

    /// <summary>Applies all redaction marks. <paramref name="affectedPages"/> lists the pages that became images.</summary>
    public static byte[] Apply(byte[] source, out List<int> affectedPages, double dpi = 200, int jpegQuality = 90)
    {
        var marks = ReadMarks(source);
        affectedPages = [.. marks.Keys.OrderBy(i => i)];
        if (marks.Count == 0) return source;

        var unmarked = AnnotationService.DeleteAll(source, subtype => subtype == "Redact");
        var images = new Dictionary<int, (byte[] Jpeg, double Width, double Height)>();
        using (var rendered = PdfiumDocument.Load(unmarked))
        {
            foreach (var (page, rects) in marks) images[page] = RenderRedacted(rendered, page, rects, dpi, jpegQuality);
        }

        return PdfIO.Modify(unmarked, doc =>
        {
            foreach (var (pageIndex, image) in images) ReplaceWithImage(doc, doc.Pages[pageIndex], image.Jpeg, image.Width, image.Height);
        });
    }

    private static (byte[] Jpeg, double Width, double Height) RenderRedacted(PdfiumDocument doc, int page, List<PdfRect> rects, double dpi, int quality)
    {
        var geometry = doc.GetGeometry(page);
        var scale = dpi / 72.0;
        var width = Math.Max(1, (int)Math.Round(geometry.ViewWidth * scale));
        var height = Math.Max(1, (int)Math.Round(geometry.ViewHeight * scale));
        var pixels = doc.Render(page, width, height);
        foreach (var rect in rects)
        {
            var view = geometry.ToView(rect);
            FillBlack(pixels, width, height,
                (int)Math.Floor(view.Left * scale) - 1, (int)Math.Floor(view.Top * scale) - 1,
                (int)Math.Ceiling(view.Right * scale) + 1, (int)Math.Ceiling(view.Bottom * scale) + 1);
        }
        var bitmap = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgr32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return (output.ToArray(), geometry.ViewWidth, geometry.ViewHeight);
    }

    private static void FillBlack(byte[] pixels, int width, int height, int left, int top, int right, int bottom)
    {
        left = Math.Clamp(left, 0, width);
        right = Math.Clamp(right, 0, width);
        top = Math.Clamp(top, 0, height);
        bottom = Math.Clamp(bottom, 0, height);
        for (var y = top; y < bottom; y++)
        {
            var row = y * width * 4;
            for (var x = left; x < right; x++)
            {
                var i = row + x * 4;
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                pixels[i + 3] = 255;
            }
        }
    }

    /// <summary>Empties a page and paints a full-page image on it. The page object itself is kept so bookmarks still point at it.</summary>
    internal static void ReplaceWithImage(PdfDocument doc, PdfPage page, byte[] jpeg, double width, double height)
    {
        foreach (var item in PdfX.Annots(page, false).Elements)
        {
            if (PdfX.Resolve(item) is PdfDictionary annot && annot.Name("/Subtype") == "/Widget") FormService.DetachWidget(doc, annot);
        }
        foreach (var key in new[]
                 {
                     "/Contents", "/Resources", "/Annots", "/CropBox", "/BleedBox", "/TrimBox", "/ArtBox", "/Rotate", "/Thumb",
                     "/Group", "/StructParents", "/PieceInfo", "/Metadata", "/AA", "/B", "/Tabs",
                 })
        {
            page.Elements.Remove(key);
        }
        page.Elements["/MediaBox"] = PdfX.Reals(doc, 0, 0, width, height);
        using var image = XImage.FromStream(new MemoryStream(jpeg));
        using var canvas = PageCanvas.Open(page);
        canvas.Gfx.DrawImage(image, new XRect(0, 0, width, height));
    }
}
