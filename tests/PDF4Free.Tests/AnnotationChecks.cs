using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Tests;

public sealed partial class TestRunner
{
    private static byte[] AnnotateEverything(byte[] bytes, int page)
    {
        using var doc = PdfiumDocument.Load(bytes);
        var text = doc.GetText(page);
        var geometry = doc.GetGeometry(page);
        List<PdfRect> Range(string word)
        {
            var i = text.Text.IndexOf(word, StringComparison.Ordinal);
            return text.GetRangeRects(i, word.Length);
        }
        PdfRect V(double x, double y, double w, double h) => geometry.ToPage(new System.Windows.Rect(x, y, w, h));
        PdfPoint P(double x, double y) => geometry.ToPage(new System.Windows.Point(x, y));

        bytes = AnnotationService.AddTextMarkup(bytes, page, TextMarkupKind.Highlight, Range("quick brown fox"), new AnnotStyle(0xFFE000), "Check this");
        bytes = AnnotationService.AddTextMarkup(bytes, page, TextMarkupKind.Underline, Range("free desktop editor"), new AnnotStyle(0x1E88E5));
        bytes = AnnotationService.AddTextMarkup(bytes, page, TextMarkupKind.StrikeOut, Range("555-123-4567"), new AnnotStyle(0xE53935));
        bytes = AnnotationService.AddTextMarkup(bytes, page, TextMarkupKind.Squiggly, Range("lazy dog"), new AnnotStyle(0x43A047));
        bytes = AnnotationService.AddNote(bytes, page, P(300, 150), "A sticky note", new AnnotStyle(0xFFD400));
        bytes = AnnotationService.AddFreeText(bytes, page, V(320, 180, 180, 60), "Text box with wrapped words inside it, written by PDF4Free.", new AnnotStyle(0xC62828, Fill: 0xFFF8E1, Width: 1, FontSize: 11));
        bytes = AnnotationService.AddInk(bytes, page, [[P(80, 450), P(110, 430), P(140, 470), P(170, 430), P(200, 460)]], new AnnotStyle(0x6A1B9A, Width: 3));
        bytes = AnnotationService.AddShape(bytes, page, ShapeKind.Rectangle, V(80, 500, 120, 60), new AnnotStyle(0x1565C0, Width: 2, Fill: 0xBBDEFB, Opacity: 0.7));
        bytes = AnnotationService.AddShape(bytes, page, ShapeKind.Ellipse, V(230, 500, 120, 60), new AnnotStyle(0x2E7D32, Width: 2));
        bytes = AnnotationService.AddLine(bytes, page, P(380, 560), P(520, 500), true, new AnnotStyle(0xD84315, Width: 2.5));
        bytes = AnnotationService.AddStamp(bytes, page, V(80, 600, 190, 60), "APPROVED", "By Tester, 2026-11-25", new AnnotStyle(0x2E7D32));
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 0, 0 }, 8);
        bytes = AnnotationService.AddImageStamp(bytes, page, V(300, 600, 60, 60), image, new AnnotStyle());
        bytes = AnnotationService.AddRedactionMarks(bytes, page, Range("4111 1111 1111 1111"));
        return bytes;
    }

    [Check]
    public void Annotations_are_written_and_rendered()
    {
        var bytes = AnnotateEverything(Samples.Basic(1), 0);
        File.WriteAllBytes(PathFor("annotated.pdf"), bytes);
        using var doc = PdfiumDocument.Load(bytes);
        var annots = doc.GetAnnotations(0);
        AssertEqual(13, annots.Count, "annotation count");
        string[] expected = ["Highlight", "Underline", "StrikeOut", "Squiggly", "Text", "FreeText", "Ink", "Square", "Circle", "Line", "Stamp", "Stamp", "Redact"];
        for (var i = 0; i < expected.Length; i++) AssertEqual(expected[i], annots[i].Subtype, $"subtype {i}");
        AssertEqual("Check this", annots[0].Contents, "highlight contents");
        SavePng(doc.RenderBitmap(0, 110), "annotated.png");
        var plain = doc.RenderBitmap(0, 110, new PageRenderOptions(Annotations: false));
        SavePng(plain, "annotated-off.png");
    }

    [Check]
    public void Annotations_stay_upright_on_rotated_pages()
    {
        var bytes = Samples.Rotated();
        for (var p = 1; p < 4; p++) bytes = AnnotateEverything(bytes, p);
        File.WriteAllBytes(PathFor("annotated-rotated.pdf"), bytes);
        using var doc = PdfiumDocument.Load(bytes);
        for (var p = 1; p < 4; p++) SavePng(doc.RenderBitmap(p, 80), $"annotated-rot{p * 90}.png");
    }

    [Check]
    public void Annotations_can_be_moved_edited_and_deleted()
    {
        var bytes = AnnotateEverything(Samples.Basic(1), 0);
        using (var before = PdfiumDocument.Load(bytes))
        {
            var note = before.GetAnnotations(0)[4];
            bytes = AnnotationService.Move(bytes, 0, 4, 50, -30);
            using var moved = PdfiumDocument.Load(bytes);
            var after = moved.GetAnnotations(0)[4];
            Assert(Math.Abs(after.Rect.Left - note.Rect.Left - 50) < 0.01 && Math.Abs(after.Rect.Bottom - note.Rect.Bottom + 30) < 0.01, "note moved");
        }
        bytes = AnnotationService.SetContents(bytes, 0, 5, "Changed text");
        bytes = AnnotationService.Delete(bytes, 0, 0);
        using var doc = PdfiumDocument.Load(bytes);
        var annots = doc.GetAnnotations(0);
        AssertEqual(12, annots.Count, "count after delete");
        AssertEqual("Changed text", annots[4].Contents, "free text contents");
        bytes = AnnotationService.DeleteAll(bytes);
        using var cleared = PdfiumDocument.Load(bytes);
        AssertEqual(0, cleared.GetAnnotations(0).Count, "count after delete all");
    }
}
