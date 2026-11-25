using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Tests;

public sealed partial class TestRunner
{
    /// <summary>Discovers where PDFsharp's XGraphics puts (10,20)-(40,60) on awkward pages. Prints, never fails.</summary>
    [Check]
    public void Probe_xgraphics_mapping()
    {
        foreach (var (name, mediaBox, rotate, resetRotate) in new[]
                 {
                     ("plain", (0.0, 0.0, 612.0, 792.0), 0, false),
                     ("offset-media", (100.0, 100.0, 712.0, 892.0), 0, false),
                     ("rotate90-native", (0.0, 0.0, 612.0, 792.0), 90, false),
                     ("rotate90-reset", (0.0, 0.0, 612.0, 792.0), 90, true),
                     ("rotate270-reset-offset", (100.0, 100.0, 712.0, 892.0), 270, true),
                 })
        {
            var source = MakeBlank(mediaBox, rotate);
            var edited = PdfIO.Modify(source, doc =>
            {
                var page = doc.Pages[0];
                var saved = page.Elements.GetInteger("/Rotate");
                if (resetRotate) page.Elements.Remove("/Rotate");
                using (var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
                {
                    gfx.DrawRectangle(XBrushes.Black, new XRect(10, 20, 30, 40));
                }
                if (resetRotate) page.Elements.SetInteger("/Rotate", saved);
            });
            using var pdfium = PdfiumDocument.Load(edited);
            var path = pdfium.GetPageObjects(0).Last(o => o.Kind == PageObjectKind.Path);
            var b = path.Bounds;
            Console.WriteLine($"      probe {name,-24} -> page rect L={b.Left:0.#} B={b.Bottom:0.#} R={b.Right:0.#} T={b.Top:0.#}");
        }
    }

    private static byte[] MakeBlank((double X1, double Y1, double X2, double Y2) box, int rotate)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Elements["/MediaBox"] = Samples.Box(doc, box.X1, box.Y1, box.X2, box.Y2);
        if (rotate != 0) page.Elements.SetInteger("/Rotate", rotate);
        var bytes = PdfIO.Save(doc);
        // Re-open so PDFsharp sees the page exactly as it would see a foreign file.
        using var reopened = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Modify);
        return PdfIO.Save(reopened);
    }
}
