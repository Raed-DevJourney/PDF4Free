using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PDF4Free.Core.Editing;

namespace PDF4Free.Tests;

/// <summary>Builds the PDFs the checks run against.</summary>
public static class Samples
{
    public static readonly string[] BodyLines =
    [
        "PDF4Free is a free desktop editor for PDF documents.",
        "Contact: jane.doe@example.com or call 555-123-4567 today.",
        "The quick brown fox jumps over the lazy dog.",
        "Confidential account number 4111 1111 1111 1111.",
    ];

    public static byte[] Basic(int pages = 3, string tag = "")
    {
        var doc = new PdfDocument();
        doc.Info.Title = "PDF4Free sample";
        doc.Info.Author = "Test Author";
        for (var i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            using var gfx = XGraphics.FromPdfPage(page);
            DrawBody(gfx, $"Page {i + 1} heading {tag}".Trim());
        }
        return PdfIO.Save(doc);
    }

    /// <summary>Four pages: upright, rotated 90, 180 and 270 degrees; the last one also has an offset media box and a crop box.</summary>
    public static byte[] Rotated()
    {
        var doc = new PdfDocument();
        for (var i = 0; i < 4; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                DrawBody(gfx, $"Rotation {i * 90}");
            }
            if (i > 0) page.Elements.SetInteger("/Rotate", i * 90);
        }
        var last = doc.Pages[3];
        last.Elements["/CropBox"] = Box(doc, 50, 60, 500, 700);
        return PdfIO.Save(doc);
    }

    public static PdfArray Box(PdfDocument doc, double x1, double y1, double x2, double y2) =>
        new(doc, new PdfReal(x1), new PdfReal(y1), new PdfReal(x2), new PdfReal(y2));

    private static void DrawBody(XGraphics gfx, string heading)
    {
        var headingFont = new XFont("Arial", 22, XFontStyleEx.Bold);
        var bodyFont = new XFont("Times New Roman", 12, XFontStyleEx.Regular);
        gfx.DrawString(heading, headingFont, XBrushes.DarkBlue, new XPoint(72, 100));
        var y = 140.0;
        foreach (var line in BodyLines)
        {
            gfx.DrawString(line, bodyFont, XBrushes.Black, new XPoint(72, y));
            y += 20;
        }
        gfx.DrawRectangle(new XPen(XColors.Red, 2), XBrushes.LightYellow, new XRect(72, 300, 200, 100));
        gfx.DrawEllipse(new XPen(XColors.Green, 1.5), new XRect(320, 300, 160, 100));
    }
}
