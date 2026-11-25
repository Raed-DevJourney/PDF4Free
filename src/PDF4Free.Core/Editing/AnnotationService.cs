using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PDF4Free.Core.Model;

namespace PDF4Free.Core.Editing;

public enum TextMarkupKind { Highlight, Underline, StrikeOut, Squiggly }

public enum ShapeKind { Rectangle, Ellipse }

/// <summary>How a new comment looks. Colours are 0xRRGGBB.</summary>
public sealed record AnnotStyle(
    uint Color = 0xFFD400,
    double Opacity = 1.0,
    double Width = 1.5,
    uint? Fill = null,
    double FontSize = 12,
    string Author = "");

/// <summary>Adds, moves, edits and removes annotations. Every method returns a new PDF.</summary>
public static partial class AnnotationService
{
    // ------------------------------------------------------------------ text markup

    public static byte[] AddTextMarkup(byte[] source, int pageIndex, TextMarkupKind kind, IReadOnlyList<PdfRect> lineRects, AnnotStyle style, string contents = "") =>
        PdfIO.Modify(source, doc =>
        {
            if (lineRects.Count == 0) return;
            var page = doc.Pages[pageIndex];
            var bounds = lineRects.Aggregate((a, b) => a.Union(b)).Inflate(1);
            var annot = NewAnnot(doc, page, kind.ToString(), bounds, style, contents);
            annot.Elements["/QuadPoints"] = QuadPoints(doc, lineRects);
            BuildMarkupAppearance(doc, annot, kind, lineRects, bounds, style);
        });

    private static PdfArray QuadPoints(PdfDocument doc, IReadOnlyList<PdfRect> rects)
    {
        var values = new List<double>(rects.Count * 8);
        foreach (var r in rects)
        {
            // Acrobat's de-facto order: top-left, top-right, bottom-left, bottom-right.
            values.AddRange([r.Left, r.Top, r.Right, r.Top, r.Left, r.Bottom, r.Right, r.Bottom]);
        }
        return PdfX.Reals(doc, [.. values]);
    }

    private static void BuildMarkupAppearance(PdfDocument doc, PdfDictionary annot, TextMarkupKind kind, IReadOnlyList<PdfRect> rects, PdfRect bounds, AnnotStyle style)
    {
        var content = new ContentBuilder().Raw("/GS0 gs");
        foreach (var r in rects)
        {
            var thickness = Math.Max(0.6, r.Height * 0.06);
            switch (kind)
            {
                case TextMarkupKind.Highlight:
                    content.FillColor(style.Color).Rect(r).Raw("f");
                    break;
                case TextMarkupKind.Underline:
                    content.StrokeColor(style.Color).Op("w", thickness).Op("m", r.Left, r.Bottom + thickness).Op("l", r.Right, r.Bottom + thickness).Raw("S");
                    break;
                case TextMarkupKind.StrikeOut:
                    content.StrokeColor(style.Color).Op("w", thickness).Op("m", r.Left, r.Bottom + r.Height * 0.42).Op("l", r.Right, r.Bottom + r.Height * 0.42).Raw("S");
                    break;
                default:
                    AppendSquiggle(content, r, style.Color, thickness);
                    break;
            }
        }
        var multiply = kind == TextMarkupKind.Highlight;
        SetAppearance(doc, annot, bounds, content, GraphicsState(doc, style.Opacity, multiply));
    }

    private static void AppendSquiggle(ContentBuilder content, PdfRect r, uint color, double thickness)
    {
        var step = Math.Max(1.5, r.Height * 0.12);
        var low = r.Bottom + thickness;
        var high = low + step * 0.8;
        content.StrokeColor(color).Op("w", thickness * 0.8).Op("m", r.Left, low);
        var up = true;
        for (var x = r.Left + step; x < r.Right + step; x += step)
        {
            content.Op("l", Math.Min(x, r.Right), up ? high : low);
            up = !up;
        }
        content.Raw("S");
    }

    // ------------------------------------------------------------------ notes and text boxes

    public const double NoteSize = 20;

    public static byte[] AddNote(byte[] source, int pageIndex, PdfPoint at, string contents, AnnotStyle style) =>
        PdfIO.Modify(source, doc =>
        {
            var page = doc.Pages[pageIndex];
            var rect = new PdfRect(at.X - NoteSize / 2, at.Y - NoteSize / 2, at.X + NoteSize / 2, at.Y + NoteSize / 2);
            var annot = NewAnnot(doc, page, "Text", rect, style, contents);
            annot.Elements.SetName("/Name", "/Comment");
            annot.Elements.SetBoolean("/Open", false);
            var icon = new ContentBuilder()
                .FillColor(style.Color).StrokeColor(0x444444).Op("w", 0.8)
                .RoundedRect(new PdfRect(1, 4, 19, 19), 2.5).Raw("B")
                .Op("m", 5, 4.2).Op("l", 5, 0.8).Op("l", 9.5, 4.2).Raw("B")
                .Op("w", 0.7).Op("m", 4.5, 15).Op("l", 15.5, 15).Raw("S")
                .Op("m", 4.5, 12).Op("l", 15.5, 12).Raw("S")
                .Op("m", 4.5, 9).Op("l", 11.5, 9).Raw("S");
            SetAppearance(doc, annot, new PdfRect(0, 0, NoteSize, NoteSize), icon, null, UprightMatrix(PdfX.Geometry(page).Rotation));
        });

    public static byte[] AddFreeText(byte[] source, int pageIndex, PdfRect rect, string contents, AnnotStyle style) =>
        PdfIO.Modify(source, doc =>
        {
            var page = doc.Pages[pageIndex];
            var annot = NewAnnot(doc, page, "FreeText", rect, style, contents);
            annot.Elements.Remove("/C");
            if (style.Fill is { } fill) annot.Elements["/C"] = PdfX.Color(doc, fill);
            annot.Elements.SetString("/DA", string.Create(CultureInfo.InvariantCulture,
                $"{ColorOperands(style.Color)} rg /Helv {style.FontSize:0.##} Tf"));
            var border = new PdfDictionary(doc);
            border.Elements.SetReal("/W", style.Width);
            annot.Elements["/BS"] = border;
            BuildFreeTextAppearance(doc, page, annot);
        });

    private static void BuildFreeTextAppearance(PdfDocument doc, PdfPage page, PdfDictionary annot)
    {
        var rect = annot.Rect("/Rect") ?? default;
        var rotation = PdfX.Geometry(page).Rotation;
        var (w, h) = rotation is 1 or 3 ? (rect.Height, rect.Width) : (rect.Width, rect.Height);
        var (textColor, size) = ParseDefaultAppearance(annot.Text("/DA"));
        var borderWidth = annot.Dict("/BS")?.Number("/W", 1) ?? 1;
        var content = new ContentBuilder();
        var box = new PdfRect(borderWidth / 2, borderWidth / 2, w - borderWidth / 2, h - borderWidth / 2);
        if (annot.Array("/C") is { Elements.Count: 3 } fill)
        {
            var c = fill.Numbers();
            content.Op("rg", c[0], c[1], c[2]).Rect(box).Raw("f");
        }
        if (borderWidth > 0) content.StrokeColor(textColor).Op("w", borderWidth).Rect(box).Raw("S");

        const double pad = 3;
        var leading = size * 1.2;
        var y = h - pad - borderWidth - size * TextMetrics.Ascent;
        content.Raw("q").Rect(new PdfRect(pad, pad, w - pad, h - pad)).Raw("W n").FillColor(textColor);
        foreach (var line in TextMetrics.Wrap(annot.Text("/Contents"), size, Math.Max(10, w - 2 * pad - 2 * borderWidth)))
        {
            if (y < -size) break;
            content.TextLine("Helv", size, pad + borderWidth, y, line);
            y -= leading;
        }
        content.Raw("Q");
        var resources = new PdfDictionary(doc);
        AddHelvetica(doc, resources, "Helv", bold: false);
        SetAppearance(doc, annot, new PdfRect(0, 0, w, h), content, resources, UprightMatrix(rotation));
    }

    [GeneratedRegex(@"([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+rg")]
    private static partial Regex DaColor();

    [GeneratedRegex(@"/\S+\s+([\d.]+)\s+Tf")]
    private static partial Regex DaFont();

    private static (uint Color, double Size) ParseDefaultAppearance(string da)
    {
        uint color = 0;
        var size = 12.0;
        var c = DaColor().Match(da);
        if (c.Success)
        {
            static uint Channel(string s) => (uint)Math.Clamp(Math.Round(double.Parse(s, CultureInfo.InvariantCulture) * 255), 0, 255);
            color = (Channel(c.Groups[1].Value) << 16) | (Channel(c.Groups[2].Value) << 8) | Channel(c.Groups[3].Value);
        }
        var f = DaFont().Match(da);
        if (f.Success && double.TryParse(f.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0) size = parsed;
        return (color, size);
    }

    // ------------------------------------------------------------------ drawing

    public static byte[] AddInk(byte[] source, int pageIndex, IReadOnlyList<IReadOnlyList<PdfPoint>> strokes, AnnotStyle style) =>
        PdfIO.Modify(source, doc =>
        {
            var usable = strokes.Where(s => s.Count > 0).ToList();
            if (usable.Count == 0) return;
            var page = doc.Pages[pageIndex];
            var all = usable.SelectMany(s => s).ToList();
            var bounds = new PdfRect(all.Min(p => p.X), all.Min(p => p.Y), all.Max(p => p.X), all.Max(p => p.Y)).Inflate(style.Width + 1);
            var annot = NewAnnot(doc, page, "Ink", bounds, style, "");
            var inkList = new PdfArray(doc);
            var content = new ContentBuilder().Raw("/GS0 gs").StrokeColor(style.Color).Op("w", style.Width).Raw("1 J 1 j");
            foreach (var stroke in usable)
            {
                inkList.Elements.Add(PdfX.Reals(doc, [.. stroke.SelectMany(p => new[] { p.X, p.Y })]));
                AppendSmoothStroke(content, stroke);
            }
            annot.Elements["/InkList"] = inkList;
            SetBorderStyle(doc, annot, style.Width);
            SetAppearance(doc, annot, bounds, content, GraphicsState(doc, style.Opacity, false));
        });

    private static void AppendSmoothStroke(ContentBuilder content, IReadOnlyList<PdfPoint> points)
    {
        content.Op("m", points[0].X, points[0].Y);
        if (points.Count == 1)
        {
            content.Op("l", points[0].X + 0.01, points[0].Y).Raw("S");
            return;
        }
        var from = points[0];
        for (var i = 1; i < points.Count - 1; i++)
        {
            var control = points[i];
            var to = new PdfPoint((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2);
            content.Op("c",
                from.X + 2.0 / 3 * (control.X - from.X), from.Y + 2.0 / 3 * (control.Y - from.Y),
                to.X + 2.0 / 3 * (control.X - to.X), to.Y + 2.0 / 3 * (control.Y - to.Y),
                to.X, to.Y);
            from = to;
        }
        content.Op("l", points[^1].X, points[^1].Y).Raw("S");
    }

    public static byte[] AddShape(byte[] source, int pageIndex, ShapeKind kind, PdfRect rect, AnnotStyle style) =>
        PdfIO.Modify(source, doc =>
        {
            var page = doc.Pages[pageIndex];
            var bounds = rect.Inflate(style.Width / 2 + 0.5);
            var annot = NewAnnot(doc, page, kind == ShapeKind.Rectangle ? "Square" : "Circle", bounds, style, "");
            SetBorderStyle(doc, annot, style.Width);
            var content = new ContentBuilder().Raw("/GS0 gs").StrokeColor(style.Color).Op("w", style.Width);
            if (style.Fill is { } fill)
            {
                annot.Elements["/IC"] = PdfX.Color(doc, fill);
                content.FillColor(fill);
            }
            if (kind == ShapeKind.Rectangle) content.Rect(rect);
            else content.Ellipse(rect);
            content.Raw(style.Fill is null ? "S" : "B");
            SetAppearance(doc, annot, bounds, content, GraphicsState(doc, style.Opacity, false));
        });

    public static byte[] AddLine(byte[] source, int pageIndex, PdfPoint from, PdfPoint to, bool arrow, AnnotStyle style) =>
        PdfIO.Modify(source, doc =>
        {
            var page = doc.Pages[pageIndex];
            var head = Math.Max(8, style.Width * 4.5);
            var bounds = PdfRect.FromPoints(from, to).Inflate(head + style.Width);
            var annot = NewAnnot(doc, page, "Line", bounds, style, "");
            annot.Elements["/L"] = PdfX.Reals(doc, from.X, from.Y, to.X, to.Y);
            SetBorderStyle(doc, annot, style.Width);
            var content = new ContentBuilder().Raw("/GS0 gs").StrokeColor(style.Color).FillColor(style.Color).Op("w", style.Width).Raw("1 J");
            var lineEnd = to;
            if (arrow)
            {
                var ends = new PdfArray(doc);
                ends.Elements.Add(new PdfName("/None"));
                ends.Elements.Add(new PdfName("/ClosedArrow"));
                annot.Elements["/LE"] = ends;
                annot.Elements["/IC"] = PdfX.Color(doc, style.Color);
                lineEnd = AppendArrowHead(content, from, to, head);
            }
            content.Op("m", from.X, from.Y).Op("l", lineEnd.X, lineEnd.Y).Raw("S");
            SetAppearance(doc, annot, bounds, content, GraphicsState(doc, style.Opacity, false));
        });

    /// <summary>Draws a filled arrow head at <paramref name="to"/> and returns where the shaft should stop.</summary>
    private static PdfPoint AppendArrowHead(ContentBuilder content, PdfPoint from, PdfPoint to, double size)
    {
        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        const double spread = 0.42;
        var a = new PdfPoint(to.X - size * Math.Cos(angle - spread), to.Y - size * Math.Sin(angle - spread));
        var b = new PdfPoint(to.X - size * Math.Cos(angle + spread), to.Y - size * Math.Sin(angle + spread));
        content.Op("m", to.X, to.Y).Op("l", a.X, a.Y).Op("l", b.X, b.Y).Raw("h f");
        return new PdfPoint(to.X - size * 0.8 * Math.Cos(angle), to.Y - size * 0.8 * Math.Sin(angle));
    }

    // ------------------------------------------------------------------ stamps

    public static byte[] AddStamp(byte[] source, int pageIndex, PdfRect rect, string text, string subtitle, AnnotStyle style) =>
        PdfIO.Modify(source, doc =>
        {
            var page = doc.Pages[pageIndex];
            var annot = NewAnnot(doc, page, "Stamp", rect, style, text);
            annot.Elements.SetName("/Name", "/" + Regex.Replace(text, @"[^A-Za-z0-9]", ""));
            var rotation = PdfX.Geometry(page).Rotation;
            var (w, h) = rotation is 1 or 3 ? (rect.Height, rect.Width) : (rect.Width, rect.Height);
            var content = new ContentBuilder().Raw("/GS0 gs").StrokeColor(style.Color).FillColor(style.Color)
                .Op("w", 2.2).RoundedRect(new PdfRect(2, 2, w - 2, h - 2), 6).Raw("S")
                .Op("w", 0.7).RoundedRect(new PdfRect(5, 5, w - 5, h - 5), 4).Raw("S");
            var hasSubtitle = !string.IsNullOrWhiteSpace(subtitle);
            var area = hasSubtitle ? h * 0.62 : h;
            var size = Math.Min(area * 0.5, (w - 20) / Math.Max(0.1, TextMetrics.Width(text, 1, bold: true)));
            var baseline = (hasSubtitle ? h - area : 0) + (area - size * TextMetrics.CapHeight) / 2;
            content.TextLine("HelvB", size, (w - TextMetrics.Width(text, size, true)) / 2, baseline, text);
            if (hasSubtitle)
            {
                var small = Math.Min(h * 0.16, (w - 20) / Math.Max(0.1, TextMetrics.Width(subtitle, 1)));
                content.TextLine("Helv", small, (w - TextMetrics.Width(subtitle, small)) / 2, h * 0.16, subtitle);
            }
            var resources = GraphicsState(doc, style.Opacity, false);
            AddHelvetica(doc, resources, "HelvB", bold: true);
            AddHelvetica(doc, resources, "Helv", bold: false);
            SetAppearance(doc, annot, new PdfRect(0, 0, w, h), content, resources, UprightMatrix(rotation));
        });

    public static byte[] AddImageStamp(byte[] source, int pageIndex, PdfRect rect, BitmapSource image, AnnotStyle style) =>
        PdfIO.Modify(source, doc =>
        {
            var page = doc.Pages[pageIndex];
            var annot = NewAnnot(doc, page, "Stamp", rect, style, "");
            annot.Elements.SetName("/Name", "/Image");
            var rotation = PdfX.Geometry(page).Rotation;
            var (w, h) = rotation is 1 or 3 ? (rect.Height, rect.Width) : (rect.Width, rect.Height);
            var xObjects = new PdfDictionary(doc);
            xObjects.Elements["/Im0"] = PdfX.Ref(CreateImageXObject(doc, image));
            var resources = GraphicsState(doc, style.Opacity, false);
            resources.Elements["/XObject"] = xObjects;
            var content = new ContentBuilder().Raw("/GS0 gs").Raw("q").Op("cm", w, 0, 0, h, 0, 0).Raw("/Im0 Do").Raw("Q");
            SetAppearance(doc, annot, new PdfRect(0, 0, w, h), content, resources, UprightMatrix(rotation));
        });

    /// <summary>Embeds a bitmap as an RGB image XObject with a soft mask when it has transparency.</summary>
    internal static PdfDictionary CreateImageXObject(PdfDocument doc, BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];
        var hasAlpha = false;
        for (var i = 0; i < alpha.Length; i++)
        {
            rgb[i * 3] = pixels[i * 4 + 2];
            rgb[i * 3 + 1] = pixels[i * 4 + 1];
            rgb[i * 3 + 2] = pixels[i * 4];
            alpha[i] = pixels[i * 4 + 3];
            hasAlpha |= alpha[i] != 255;
        }
        var image = NewImage(doc, Deflate(rgb), width, height, "/DeviceRGB");
        if (hasAlpha) image.Elements["/SMask"] = PdfX.Ref(NewImage(doc, Deflate(alpha), width, height, "/DeviceGray"));
        return image;
    }

    private static PdfDictionary NewImage(PdfDocument doc, byte[] deflated, int width, int height, string colorSpace)
    {
        var image = PdfX.NewStream(doc, deflated);
        image.Elements.SetName("/Type", "/XObject");
        image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetInteger("/Width", width);
        image.Elements.SetInteger("/Height", height);
        image.Elements.SetName("/ColorSpace", colorSpace);
        image.Elements.SetInteger("/BitsPerComponent", 8);
        image.Elements.SetName("/Filter", "/FlateDecode");
        return image;
    }

    internal static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    // ------------------------------------------------------------------ links

    /// <summary>Adds a clickable area that opens a web address or jumps to a page of this document.</summary>
    public static byte[] AddLink(byte[] source, int pageIndex, PdfRect rect, string? url, int targetPage = -1) =>
        PdfIO.Modify(source, doc =>
        {
            var page = doc.Pages[pageIndex];
            var link = PdfX.NewObject(doc);
            link.Elements.SetName("/Type", "/Annot");
            link.Elements.SetName("/Subtype", "/Link");
            link.Elements["/Rect"] = rect.ToArray(doc);
            link.Elements["/Border"] = PdfX.Reals(doc, 0, 0, 0);
            link.Elements.SetInteger("/F", 4);
            if (!string.IsNullOrWhiteSpace(url))
            {
                var action = new PdfDictionary(doc);
                action.Elements.SetName("/S", "/URI");
                action.Elements.SetString("/URI", url.Trim());
                link.Elements["/A"] = action;
            }
            else
            {
                var target = doc.Pages[Math.Clamp(targetPage, 0, doc.PageCount - 1)];
                var destination = new PdfArray(doc);
                destination.Elements.Add(target.Reference!);
                destination.Elements.Add(new PdfName("/Fit"));
                link.Elements["/Dest"] = destination;
            }
            PdfX.Annots(page, create: true).Elements.Add(PdfX.Ref(link));
        });

    // ------------------------------------------------------------------ redaction marks

    /// <summary>Marks areas for redaction. Nothing is removed until the marks are applied.</summary>
    public static byte[] AddRedactionMarks(byte[] source, int pageIndex, IReadOnlyList<PdfRect> rects, string author = "") =>
        PdfIO.Modify(source, doc => AddRedactionMarks(doc, pageIndex, rects, author));

    internal static void AddRedactionMarks(PdfDocument doc, int pageIndex, IReadOnlyList<PdfRect> rects, string author)
    {
        if (rects.Count == 0) return;
        var page = doc.Pages[pageIndex];
        var bounds = rects.Aggregate((a, b) => a.Union(b)).Inflate(1);
        var annot = NewAnnot(doc, page, "Redact", bounds, new AnnotStyle(Color: 0xE53935, Author: author), "");
        annot.Elements["/QuadPoints"] = QuadPoints(doc, rects);
        annot.Elements["/IC"] = PdfX.Color(doc, 0x000000);
        var content = new ContentBuilder().Raw("/GS0 gs").StrokeColor(0xE53935).FillColor(0xE53935).Op("w", 1);
        foreach (var r in rects) content.Rect(r).Raw("B");
        var resources = GraphicsState(doc, 1, false);
        var gs = resources.Dict("/ExtGState")!.Dict("/GS0")!;
        gs.Elements.SetReal("/ca", 0.25);
        SetAppearance(doc, annot, bounds, content, resources);
    }

    // ------------------------------------------------------------------ editing existing annotations

    public static byte[] Delete(byte[] source, int pageIndex, int annotIndex) =>
        PdfIO.Modify(source, doc => DeleteAt(doc.Pages[pageIndex], annotIndex));

    /// <summary>Removes every annotation that matches the filter (subtype name without the slash). Links and form widgets are kept unless asked for.</summary>
    public static byte[] DeleteAll(byte[] source, Func<string, bool>? filter = null) =>
        PdfIO.Modify(source, doc =>
        {
            filter ??= subtype => subtype is not ("Link" or "Widget");
            foreach (var page in doc.Pages)
            {
                var annots = PdfX.Annots(page, false);
                for (var i = annots.Elements.Count - 1; i >= 0; i--)
                {
                    if (PdfX.Resolve(annots.Elements[i]) is PdfDictionary a && filter(a.Name("/Subtype").TrimStart('/'))) annots.Elements.RemoveAt(i);
                }
            }
        });

    internal static void DeleteAt(PdfPage page, int annotIndex)
    {
        var annots = PdfX.Annots(page, false);
        if (annotIndex < 0 || annotIndex >= annots.Elements.Count) return;
        var target = PdfX.Resolve(annots.Elements[annotIndex]) as PdfDictionary;
        var popup = target?.Elements["/Popup"] as PdfReference;
        annots.Elements.RemoveAt(annotIndex);
        if (popup is null) return;
        for (var i = annots.Elements.Count - 1; i >= 0; i--)
        {
            if (annots.Elements[i] is PdfReference r && r.ObjectID == popup.ObjectID) annots.Elements.RemoveAt(i);
        }
    }

    public static byte[] Move(byte[] source, int pageIndex, int annotIndex, double dx, double dy) =>
        PdfIO.Modify(source, doc =>
        {
            if (Find(doc, pageIndex, annotIndex) is not { } annot) return;
            foreach (var key in new[] { "/Rect", "/QuadPoints", "/L", "/Vertices", "/CL" })
            {
                if (annot.Array(key) is { } array) Translate(array, dx, dy);
            }
            if (annot.Array("/InkList") is { } ink)
            {
                foreach (var item in ink.Elements)
                {
                    if (PdfX.Resolve(item) is PdfArray stroke) Translate(stroke, dx, dy);
                }
            }
            Touch(annot);
        });

    private static void Translate(PdfArray array, double dx, double dy)
    {
        for (var i = 0; i < array.Elements.Count; i++)
        {
            array.Elements[i] = new PdfReal(Math.Round(PdfX.Number(array.Elements[i]) + (i % 2 == 0 ? dx : dy), 4));
        }
    }

    public static byte[] SetContents(byte[] source, int pageIndex, int annotIndex, string contents) =>
        PdfIO.Modify(source, doc =>
        {
            if (Find(doc, pageIndex, annotIndex) is not { } annot) return;
            annot.Elements["/Contents"] = PdfX.UnicodeString(contents);
            if (annot.Name("/Subtype") == "/FreeText") BuildFreeTextAppearance(doc, doc.Pages[pageIndex], annot);
            Touch(annot);
        });

    private static PdfDictionary? Find(PdfDocument doc, int pageIndex, int annotIndex)
    {
        var annots = PdfX.Annots(doc.Pages[pageIndex], false);
        return annotIndex >= 0 && annotIndex < annots.Elements.Count ? PdfX.Resolve(annots.Elements[annotIndex]) as PdfDictionary : null;
    }

    private static void Touch(PdfDictionary annot) => annot.Elements.SetString("/M", PdfX.DateString(DateTime.Now));

    // ------------------------------------------------------------------ shared building blocks

    private static PdfDictionary NewAnnot(PdfDocument doc, PdfPage page, string subtype, PdfRect rect, AnnotStyle style, string contents)
    {
        var annot = PdfX.NewObject(doc);
        var now = PdfX.DateString(DateTime.Now);
        annot.Elements.SetName("/Type", "/Annot");
        annot.Elements.SetName("/Subtype", "/" + subtype);
        annot.Elements["/Rect"] = rect.ToArray(doc);
        annot.Elements["/C"] = PdfX.Color(doc, style.Color);
        annot.Elements.SetInteger("/F", 4);
        annot.Elements["/T"] = PdfX.UnicodeString(string.IsNullOrEmpty(style.Author) ? Environment.UserName : style.Author);
        annot.Elements["/Contents"] = PdfX.UnicodeString(contents);
        annot.Elements.SetString("/M", now);
        annot.Elements.SetString("/CreationDate", now);
        annot.Elements.SetString("/NM", Guid.NewGuid().ToString());
        if (page.Reference is { } pageRef) annot.Elements["/P"] = pageRef;
        if (style.Opacity < 1) annot.Elements.SetReal("/CA", style.Opacity);
        PdfX.Annots(page, create: true).Elements.Add(PdfX.Ref(annot));
        return annot;
    }

    private static void SetBorderStyle(PdfDocument doc, PdfDictionary annot, double width)
    {
        var border = new PdfDictionary(doc);
        border.Elements.SetReal("/W", width);
        annot.Elements["/BS"] = border;
    }

    private static void SetAppearance(PdfDocument doc, PdfDictionary annot, PdfRect bbox, ContentBuilder content, PdfDictionary? resources, double[]? matrix = null)
    {
        var form = PdfX.NewStream(doc, content.ToBytes());
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements.SetInteger("/FormType", 1);
        form.Elements["/BBox"] = bbox.ToArray(doc);
        if (matrix is not null) form.Elements["/Matrix"] = PdfX.Reals(doc, matrix);
        form.Elements["/Resources"] = resources ?? GraphicsState(doc, 1, false);
        var appearance = new PdfDictionary(doc);
        appearance.Elements["/N"] = PdfX.Ref(form);
        annot.Elements["/AP"] = appearance;
    }

    private static PdfDictionary GraphicsState(PdfDocument doc, double opacity, bool multiply)
    {
        var state = new PdfDictionary(doc);
        state.Elements.SetName("/Type", "/ExtGState");
        state.Elements.SetReal("/ca", opacity);
        state.Elements.SetReal("/CA", opacity);
        if (multiply) state.Elements.SetName("/BM", "/Multiply");
        var states = new PdfDictionary(doc);
        states.Elements["/GS0"] = state;
        var resources = new PdfDictionary(doc);
        resources.Elements["/ExtGState"] = states;
        return resources;
    }

    internal static void AddHelvetica(PdfDocument doc, PdfDictionary resources, string key, bool bold)
    {
        var font = new PdfDictionary(doc);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", bold ? "/Helvetica-Bold" : "/Helvetica");
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        var fonts = resources.Dict("/Font") ?? new PdfDictionary(doc);
        fonts.Elements["/" + key] = font;
        resources.Elements["/Font"] = fonts;
    }

    /// <summary>Appearance matrix that keeps text upright on a page displayed with the given rotation.</summary>
    private static double[]? UprightMatrix(int rotation) => rotation switch
    {
        1 => [0, 1, -1, 0, 0, 0],
        2 => [-1, 0, 0, -1, 0, 0],
        3 => [0, -1, 1, 0, 0, 0],
        _ => null,
    };

    private static string ColorOperands(uint rgb) => string.Create(CultureInfo.InvariantCulture,
        $"{((rgb >> 16) & 0xFF) / 255.0:0.###} {((rgb >> 8) & 0xFF) / 255.0:0.###} {(rgb & 0xFF) / 255.0:0.###}");
}
