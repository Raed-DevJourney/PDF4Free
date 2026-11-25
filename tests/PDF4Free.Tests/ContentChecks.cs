using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Tests;

public sealed partial class TestRunner
{
    private static Rect FindTextInView(PdfiumDocument doc, int page, string needle)
    {
        var text = doc.GetText(page);
        var index = text.Text.IndexOf(needle, StringComparison.Ordinal);
        Assert(index >= 0, $"'{needle}' present on page {page + 1}");
        var rect = text.GetRangeRects(index, needle.Length).Aggregate((a, b) => a.Union(b));
        return doc.GetGeometry(page).ToView(rect);
    }

    [Check]
    public void Added_text_lands_where_asked_on_any_page()
    {
        var bytes = Samples.Rotated();
        var style = new TextStyle("Arial", 14, Bold: true, Color: 0xAA00AA);
        for (var p = 0; p < 4; p++) bytes = ContentService.AddText(bytes, p, new Point(72, 400), $"MARKER{p}", style);
        File.WriteAllBytes(PathFor("content-rotated.pdf"), bytes);
        using var doc = PdfiumDocument.Load(bytes);
        for (var p = 0; p < 4; p++)
        {
            var found = FindTextInView(doc, p, $"MARKER{p}");
            Assert(Math.Abs(found.Left - 72) < 2.5, $"page {p + 1}: left {found.Left:0.0} should be 72");
            Assert(found.Top > 398 && found.Top < 406, $"page {p + 1}: top {found.Top:0.0} should be just below 400");
            Assert(found.Height > 8 && found.Height < 16 && found.Width > 50, $"page {p + 1}: upright size {found.Width:0.0}x{found.Height:0.0}");
            SavePng(doc.RenderBitmap(p, 60), $"content-rot{p * 90}.png");
        }
    }

    [Check]
    public void Added_text_survives_offset_media_box_and_dirty_graphics_state()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Elements["/MediaBox"] = Samples.Box(doc, 100, 200, 712, 992);
        // Existing content that scales the coordinate system and never restores it.
        var content = page.Contents.CreateSingleContent();
        content.Stream.Value = Encoding.ASCII.GetBytes("0.5 0 0 0.5 30 30 cm 0 0 1 rg 300 500 200 100 re f");
        var bytes = PdfIO.Save(doc);
        bytes = ContentService.AddText(bytes, 0, new Point(50, 80), "OFFSETMARK", new TextStyle("Arial", 12));
        using var pdfium = PdfiumDocument.Load(bytes);
        var found = FindTextInView(pdfium, 0, "OFFSETMARK");
        Assert(Math.Abs(found.Left - 50) < 2.5 && found.Top > 78 && found.Top < 86, $"found at {found.Left:0.0},{found.Top:0.0}");
        Assert(found.Height > 7 && found.Height < 14, $"not scaled by the old content (height {found.Height:0.0})");
    }

    [Check]
    public void Images_watermarks_headers_and_bates_numbers_are_drawn()
    {
        var bytes = Samples.Basic(3);
        var png = PageCanvas.ToPng(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 0, 128 }, 8));
        bytes = ContentService.AddImage(bytes, 0, new Rect(400, 420, 80, 80), png);
        bytes = ContentService.AddWatermark(bytes, new WatermarkOptions { Text = "DRAFT", Opacity = 0.2 });
        bytes = ContentService.AddHeaderFooter(bytes, new HeaderFooterOptions
        {
            HeaderRight = "{file}",
            FooterCenter = "Page {page} of {pages}",
            FooterRight = "{bates}",
            FileName = "sample.pdf",
            BatesPrefix = "ABC-",
            BatesDigits = 5,
            BatesStart = 41,
        });
        bytes = ContentService.AddBackground(bytes, 0xF3F8FF, [2]);
        File.WriteAllBytes(PathFor("content.pdf"), bytes);
        using var doc = PdfiumDocument.Load(bytes);
        Assert(doc.GetText(1).Text.Contains("Page 2 of 3"), "footer page number");
        Assert(doc.GetText(2).Text.Contains("ABC-00043"), "bates number on page 3");
        Assert(doc.GetText(0).Text.Contains("sample.pdf"), "file name header");
        Assert(doc.GetText(0).Text.Contains("DRAFT"), "watermark text");
        var image = doc.GetPageObjects(0).FirstOrDefault(o => o.Kind == PageObjectKind.Image);
        Assert(image is not null, "image object present");
        var view = doc.GetGeometry(0).ToView(image!.Bounds);
        Assert(Math.Abs(view.Left - 400) < 1 && Math.Abs(view.Top - 420) < 1 && Math.Abs(view.Width - 80) < 1, $"image at {view}");
        SavePng(doc.RenderBitmap(0, 80), "content-p1.png");
        SavePng(doc.RenderBitmap(2, 80), "content-p3.png");
    }

    [Check]
    public void Arabic_text_is_drawn_as_shaped_outlines()
    {
        var bytes = Samples.Basic(1);
        using var before = PdfiumDocument.Load(bytes);
        var pathsBefore = before.GetPageObjects(0).Count(o => o.Kind == PageObjectKind.Path);
        bytes = ContentService.AddText(bytes, 0, new Point(72, 440), "مرحبا بالعالم", new TextStyle("Arial", 24, Color: 0x004D40));
        using var after = PdfiumDocument.Load(bytes);
        Assert(after.GetPageObjects(0).Count(o => o.Kind == PageObjectKind.Path) > pathsBefore, "outline path added");
        SavePng(after.RenderBitmap(0, 80), "content-arabic.png");
    }

    [Check]
    public void Mixed_latin_and_arabic_text_keeps_the_latin_part_as_real_text()
    {
        // On the page: the Latin words must stay extractable; only the Arabic run becomes outlines.
        var bytes = ContentService.AddText(Samples.Basic(1), 0, new Point(72, 440), "Invoice total شكرا جزيلا paid", new TextStyle("Arial", 14));
        using (var page = PdfiumDocument.Load(bytes))
        {
            var text = page.GetText(0).Text;
            Assert(text.Contains("Invoice total") && text.Contains("paid"), "Latin words around an Arabic phrase are still text");
            var first = FindTextInView(page, 0, "Invoice total");
            var last = FindTextInView(page, 0, "paid");
            Assert(last.Left > first.Right + 40, $"the Arabic phrase sits between the Latin parts (gap {last.Left - first.Right:0})");
            SavePng(page.RenderBitmap(0, 80), "content-mixed.png");
        }

        // In a form field: same rule for the field's appearance.
        var form = FormService.FillByName(FormSample(), new Dictionary<string, string> { ["FullName"] = "Thanks شكرا", ["Notes"] = "Line one\nCall on arrival. شكرا" }, out _);
        using var flat = PdfiumDocument.Load(PdfiumOps.Flatten(form));
        var shown = flat.GetText(0).Text;
        Assert(shown.Contains("Thanks") && shown.Contains("Call on arrival."), "Latin text of a mixed field value is drawn as text");
    }

    [Check]
    public void Pages_can_be_organised()
    {
        var bytes = Samples.Basic(5);
        string Order(byte[] b)
        {
            using var d = PdfiumDocument.Load(b);
            return string.Concat(Enumerable.Range(0, d.PageCount).Select(i =>
            {
                var t = d.GetText(i).Text;
                var at = t.IndexOf("Page ", StringComparison.Ordinal);
                return at < 0 ? "_" : t.Substring(at + 5, 1);
            }));
        }

        AssertEqual("12345", Order(bytes), "initial order");
        AssertEqual("1245", Order(PageService.Delete(bytes, [2])), "delete");
        AssertEqual("54321", Order(PageService.Reorder(bytes, [4, 3, 2, 1, 0])), "reverse");
        AssertEqual("31245", Order(PageService.Move(bytes, [2], 0, 5)), "move page 3 to front");
        AssertEqual("14523", Order(PageService.Move(bytes, [1, 2], 5, 5)), "move pages 2-3 to end");
        AssertEqual("12_345", Order(PageService.InsertBlank(bytes, 2, 612, 792)), "insert blank");
        AssertEqual("1212345", Order(PageService.InsertFrom(bytes, 2, Samples.Basic(2))), "insert from other");
        AssertEqual("1232345", Order(PageService.Duplicate(bytes, [1, 2])), "duplicates follow the selection");
        AssertEqual("42", Order(PageService.Extract(bytes, [3, 1])), "extract");
        AssertEqual("12|34|5", string.Join("|", PageService.SplitEvery(bytes, 2, 5).Select(Order)), "split every 2");
        AssertEqual("1234512", Order(PageService.Merge([bytes, Samples.Basic(2)], ["A", "B"])), "merge");
        AssertEqual("11345", Order(PageService.Replace(bytes, 1, Samples.Basic(1), 0)), "replace page 2 with another document's page 1");

        var rotated = PageService.Rotate(bytes, [0, 1], 90);
        rotated = PageService.Rotate(rotated, [1], 180);
        using (var d = PdfiumDocument.Load(rotated))
        {
            AssertEqual(1, d.GetGeometry(0).Rotation, "page 1 rotated 90");
            AssertEqual(3, d.GetGeometry(1).Rotation, "page 2 rotated 270");
            AssertEqual(0, d.GetGeometry(2).Rotation, "page 3 untouched");
        }

        var cropped = PageService.Crop(bytes, [0], new Rect(50, 60, 300, 400));
        using (var d = PdfiumDocument.Load(cropped))
        {
            var g = d.GetGeometry(0);
            Assert(Math.Abs(g.ViewWidth - 300) < 0.01 && Math.Abs(g.ViewHeight - 400) < 0.01, "crop size");
            Assert(Math.Abs(g.Box.Left - 50) < 0.01 && Math.Abs(g.Box.Top - 732) < 0.01, "crop position");
        }
        using (var d = PdfiumDocument.Load(PageService.RemoveCrop(cropped, [0]))) Assert(Math.Abs(d.GetGeometry(0).ViewWidth - 612) < 0.01, "crop removed");

        using (var merged = PdfiumDocument.Load(PageService.Merge([bytes, Samples.Basic(2)], ["First", "Second"])))
        {
            var bookmarks = merged.GetBookmarks();
            AssertEqual(2, bookmarks.Count, "merge bookmarks");
            AssertEqual("Second", bookmarks[1].Title, "bookmark title");
            AssertEqual(5, bookmarks[1].PageIndex, "bookmark target");
        }
    }

    [Check]
    public void Page_ranges_are_parsed()
    {
        AssertEqual("0,1,2,4,7,8,9", string.Join(",", PageService.ParseRange("1-3, 5, 8-", 10)!), "mixed range");
        AssertEqual("2,1,0", string.Join(",", PageService.ParseRange("3-1", 10)!), "descending range");
        Assert(PageService.ParseRange("0", 10) is null, "zero rejected");
        Assert(PageService.ParseRange("4-12", 10) is null, "out of range rejected");
        Assert(PageService.ParseRange("abc", 10) is null, "garbage rejected");
    }
}
