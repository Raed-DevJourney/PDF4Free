using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PDF4Free.Core.Model;

namespace PDF4Free.Core.Editing;

/// <summary>
/// A drawing surface over an existing page whose coordinates are "view points": measured from the top-left
/// corner of the page as displayed, y down, whatever the page's rotation, media box origin or crop box.
/// </summary>
internal sealed class PageCanvas : IDisposable
{
    private PageCanvas(XGraphics gfx, PageGeometry geometry)
    {
        Gfx = gfx;
        Geometry = geometry;
    }

    public XGraphics Gfx { get; }
    public PageGeometry Geometry { get; }
    public double Width => Geometry.ViewWidth;
    public double Height => Geometry.ViewHeight;

    public static PageCanvas Open(PdfPage page, bool behind = false)
    {
        var geometry = PdfX.Geometry(page);
        var gfx = XGraphics.FromPdfPage(page, behind ? XGraphicsPdfPageOptions.Prepend : XGraphicsPdfPageOptions.Append);
        // PDFsharp maps (x, y) to PDF (x, pageHeight - y) and ignores rotation and box origins, so compose:
        // view space -> PDF user space -> PDFsharp space.
        var (a, b, c, d, e, f) = geometry.ViewToPageMatrix();
        var height = page.Height.Point;
        gfx.MultiplyTransform(new XMatrix(a, -b, c, -d, e, height - f));
        return new PageCanvas(gfx, geometry);
    }

    public void Dispose() => Gfx.Dispose();

    public static XColor Color(uint rgb, double opacity = 1) =>
        XColor.FromArgb((int)Math.Round(Math.Clamp(opacity, 0, 1) * 255), (int)((rgb >> 16) & 0xFF), (int)((rgb >> 8) & 0xFF), (int)(rgb & 0xFF));

    public static XFont Font(TextStyle style)
    {
        var flags = XFontStyleEx.Regular;
        if (style.Bold) flags |= XFontStyleEx.Bold;
        if (style.Italic) flags |= XFontStyleEx.Italic;
        try
        {
            return new XFont(style.FontFamily, style.Size, flags);
        }
        catch (Exception)
        {
            return new XFont("Arial", style.Size, flags);
        }
    }

    /// <summary>
    /// Draws one line of text with its baseline starting at (x, baseline). Ordinary text stays real text; runs in a
    /// complex script are drawn as shaped outlines, and a line that starts right-to-left is outlined as a whole so
    /// its reading order is right.
    /// </summary>
    public void DrawLine(string text, TextStyle style, double x, double baseline, double opacity = 1)
    {
        if (text.Length == 0) return;
        if (!ComplexText.NeedsShaping(text))
        {
            DrawPlain(text, style, x, baseline, opacity);
            return;
        }
        if (ComplexText.StartsRightToLeft(text))
        {
            ComplexText.Draw(Gfx, text, style, x, baseline, opacity);
            return;
        }
        foreach (var (run, simple) in ComplexText.SplitRuns(text, c => !ComplexText.NeedsShaping(c)))
        {
            if (simple) DrawPlain(run, style, x, baseline, opacity);
            else ComplexText.Draw(Gfx, run, style, x, baseline, opacity);
            x += MeasureRun(run, style, simple);
        }
    }

    private void DrawPlain(string text, TextStyle style, double x, double baseline, double opacity) =>
        Gfx.DrawString(text, Font(style), new XSolidBrush(Color(style.Color, opacity)), new XPoint(x, baseline));

    private double MeasureRun(string run, TextStyle style, bool simple) =>
        simple ? Gfx.MeasureString(run, Font(style)).Width : ComplexText.Measure(run, style);

    public double Measure(string text, TextStyle style)
    {
        if (!ComplexText.NeedsShaping(text)) return Gfx.MeasureString(text, Font(style)).Width;
        if (ComplexText.StartsRightToLeft(text)) return ComplexText.Measure(text, style);
        return ComplexText.SplitRuns(text, c => !ComplexText.NeedsShaping(c)).Sum(r => MeasureRun(r.Text, style, r.Simple));
    }

    public static XImage ToXImage(byte[] imageData)
    {
        if (IsJpeg(imageData)) return XImage.FromStream(new MemoryStream(imageData));
        return XImage.FromStream(new MemoryStream(ToPng(Decode(imageData))));
    }

    public static bool IsJpeg(byte[] data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static BitmapSource Decode(byte[] imageData)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(imageData), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public static byte[] ToPng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}

/// <summary>Font and colour of text added to a page.</summary>
public sealed record TextStyle(string FontFamily = "Arial", double Size = 12, bool Bold = false, bool Italic = false, uint Color = 0x000000);

/// <summary>
/// Draws right-to-left and complex scripts (Arabic, Hebrew, Indic...) as vector outlines shaped by WPF,
/// because PDFsharp places glyphs one by one without shaping or bidirectional reordering.
/// </summary>
internal static class ComplexText
{
    public static bool NeedsShaping(string text)
    {
        foreach (var ch in text)
        {
            if (NeedsShaping(ch)) return true;
        }
        return false;
    }

    public static bool NeedsShaping(char ch) =>
        ch is >= '֐' and <= 'ࣿ' or >= 'ऀ' and <= '෿' or >= '฀' and <= '໿'
            or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    private static bool IsRightToLeft(char ch) =>
        ch is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    /// <summary>True when the first letter of the text belongs to a right-to-left script (Hebrew, Arabic...).</summary>
    public static bool StartsRightToLeft(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsLetter(ch)) return IsRightToLeft(ch);
        }
        return false;
    }

    /// <summary>
    /// Cuts a left-to-right line into runs of "simple" characters and runs that need special treatment.
    /// Spaces and punctuation between two special runs stay with them, so a phrase is shaped in one piece.
    /// </summary>
    public static List<(string Text, bool Simple)> SplitRuns(string text, Func<char, bool> isSimple)
    {
        var runs = new List<(string Text, bool Simple)>();
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i < text.Length && isSimple(text[i]) == isSimple(text[start])) continue;
            runs.Add((text[start..i], isSimple(text[start])));
            start = i;
        }
        for (var i = 1; i < runs.Count - 1; i++)
        {
            var neutral = runs[i].Simple && !runs[i - 1].Simple && !runs[i + 1].Simple && runs[i].Text.All(c => !char.IsLetterOrDigit(c));
            if (!neutral) continue;
            runs[i - 1] = (runs[i - 1].Text + runs[i].Text + runs[i + 1].Text, false);
            runs.RemoveRange(i, 2);
            i--;
        }
        return runs;
    }

    public static double Measure(string text, TextStyle style) => Format(text, style).WidthIncludingTrailingWhitespace;

    public static void Draw(XGraphics gfx, string text, TextStyle style, double x, double baseline, double opacity)
    {
        var formatted = Format(text, style);
        var geometry = formatted.BuildGeometry(new Point(x, baseline - formatted.Baseline));
        var flattened = geometry.GetFlattenedPathGeometry(0.02, ToleranceType.Absolute);
        var path = new XGraphicsPath { FillMode = XFillMode.Winding };
        foreach (var figure in flattened.Figures)
        {
            var points = new List<XPoint> { new(figure.StartPoint.X, figure.StartPoint.Y) };
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case PolyLineSegment poly:
                        points.AddRange(poly.Points.Select(p => new XPoint(p.X, p.Y)));
                        break;
                    case LineSegment line:
                        points.Add(new XPoint(line.Point.X, line.Point.Y));
                        break;
                }
            }
            if (points.Count < 3) continue;
            path.StartFigure();
            path.AddPolygon(points.ToArray());
            path.CloseFigure();
        }
        gfx.DrawPath(new XSolidBrush(PageCanvas.Color(style.Color, opacity)), path);
    }

    /// <summary>Writes the text as filled outline paths into a raw content stream whose y axis points up.</summary>
    public static void AppendOutline(ContentBuilder content, string text, TextStyle style, double x, double baseline)
    {
        var formatted = Format(text, style);
        var geometry = formatted.BuildGeometry(new Point(0, -formatted.Baseline));
        var flattened = geometry.GetFlattenedPathGeometry(0.02, ToleranceType.Absolute);
        var any = false;
        foreach (var figure in flattened.Figures)
        {
            content.Op("m", x + figure.StartPoint.X, baseline - figure.StartPoint.Y);
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case PolyLineSegment poly:
                        foreach (var p in poly.Points) content.Op("l", x + p.X, baseline - p.Y);
                        break;
                    case LineSegment line:
                        content.Op("l", x + line.Point.X, baseline - line.Point.Y);
                        break;
                }
            }
            content.Raw("h");
            any = true;
        }
        if (any) content.Raw("f");
    }

    private static FormattedText Format(string text, TextStyle style)
    {
        var typeface = new Typeface(
            new FontFamily(style.FontFamily),
            style.Italic ? FontStyles.Italic : FontStyles.Normal,
            style.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        return new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, style.Size, Brushes.Black, 1.0);
    }
}
