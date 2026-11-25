using System.IO;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Tests;

public sealed partial class TestRunner
{
    [Check]
    public void Render_basic_document()
    {
        var bytes = Samples.Basic();
        File.WriteAllBytes(PathFor("basic.pdf"), bytes);
        using var doc = PdfiumDocument.Load(bytes);
        AssertEqual(3, doc.PageCount, "page count");
        var bitmap = doc.RenderBitmap(0, 96);
        AssertEqual(816, bitmap.PixelWidth, "pixel width at 96 dpi");
        SavePng(bitmap, "basic-p1.png");
    }

    [Check]
    public void Text_extraction_has_boxes()
    {
        using var doc = PdfiumDocument.Load(Samples.Basic());
        var text = doc.GetText(0);
        Assert(text.Text.Contains("quick brown fox"), "body text extracted");
        AssertEqual(text.Text.Length, text.Boxes.Length, "one box per char");
        var index = text.Text.IndexOf("quick", StringComparison.Ordinal);
        var rects = text.GetRangeRects(index, 5);
        AssertEqual(1, rects.Count, "one line rect for a word");
        Assert(rects[0].Width > 15 && rects[0].Width < 60, $"word width plausible ({rects[0].Width:0.0})");
    }

    [Check]
    public void Geometry_matches_pdfium_on_rotated_and_cropped_pages()
    {
        var bytes = Samples.Rotated();
        File.WriteAllBytes(PathFor("rotated.pdf"), bytes);
        using var doc = PdfiumDocument.Load(bytes);
        for (var p = 0; p < doc.PageCount; p++)
        {
            var geometry = doc.GetGeometry(p);
            AssertEqual(p, geometry.Rotation, $"rotation of page {p + 1}");
            const double scale = 2.0;
            var width = (int)Math.Round(geometry.ViewWidth * scale);
            var height = (int)Math.Round(geometry.ViewHeight * scale);
            PdfPoint[] probes = [new(100, 100), new(300, 500), new(72, 700), new(480, 90)];
            foreach (var probe in probes)
            {
                var mine = geometry.ToView(probe);
                int dx, dy;
                lock (PdfiumLibrary.Sync)
                {
                    (dx, dy) = doc.WithPage(p, page =>
                    {
                        Native.FPDF_PageToDevice(page, 0, 0, width, height, 0, probe.X, probe.Y, out var x, out var y);
                        return (x, y);
                    });
                }
                Assert(Math.Abs(mine.X * scale - dx) <= 2 && Math.Abs(mine.Y * scale - dy) <= 2,
                    $"page {p + 1} probe {probe}: mine=({mine.X * scale:0.0},{mine.Y * scale:0.0}) pdfium=({dx},{dy})");
                var back = geometry.ToPage(mine);
                Assert(Math.Abs(back.X - probe.X) < 0.001 && Math.Abs(back.Y - probe.Y) < 0.001, "round trip");
            }
            SavePng(doc.RenderBitmap(p, 60), $"rotated-p{p + 1}.png");
        }
    }

    [Check]
    public void Page_objects_are_listed_and_removable()
    {
        var bytes = Samples.Basic(1);
        using var doc = PdfiumDocument.Load(bytes);
        var objects = doc.GetPageObjects(0);
        var heading = objects.FirstOrDefault(o => o.Kind == PageObjectKind.Text && o.Text.Contains("heading"));
        Assert(heading is not null, "heading text object found");
        Assert(Math.Abs(heading!.FontSize - 22) < 0.5, $"heading font size ({heading.FontSize:0.0})");
        Assert(heading.Bold, "heading is bold");
        var edited = PdfiumOps.RemoveObjects(bytes, 0, [heading.Index]);
        using var after = PdfiumDocument.Load(edited);
        Assert(!after.GetText(0).Text.Contains("heading"), "heading removed from text");
        Assert(after.GetText(0).Text.Contains("quick brown fox"), "other text kept");
    }
}
