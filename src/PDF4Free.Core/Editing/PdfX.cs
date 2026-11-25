using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PDF4Free.Core.Model;

namespace PDF4Free.Core.Editing;

/// <summary>Small helpers for working with raw PDF objects through PDFsharp.</summary>
internal static class PdfX
{
    public static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    public static PdfDictionary? Dict(this PdfDictionary owner, string key) => Resolve(owner.Elements[key]) as PdfDictionary;

    public static PdfArray? Array(this PdfDictionary owner, string key) => Resolve(owner.Elements[key]) as PdfArray;

    public static string Name(this PdfDictionary owner, string key) => Resolve(owner.Elements[key]) switch
    {
        PdfName name => name.Value,
        PdfNameObject name => name.Value,
        _ => "",
    };

    public static string Text(this PdfDictionary owner, string key) => Resolve(owner.Elements[key]) switch
    {
        PdfString s => s.Value,
        PdfStringObject s => s.Value,
        PdfName n => n.Value.TrimStart('/'),
        _ => "",
    };

    public static double Number(PdfItem? item, double fallback = 0) => Resolve(item) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        PdfIntegerObject i => i.Value,
        PdfRealObject r => r.Value,
        PdfLongInteger l => l.Value,
        _ => fallback,
    };

    public static double Number(this PdfDictionary owner, string key, double fallback = 0) => Number(owner.Elements[key], fallback);

    public static int Int(this PdfDictionary owner, string key, int fallback = 0) => (int)Math.Round(Number(owner.Elements[key], fallback));

    public static double[] Numbers(this PdfArray array)
    {
        var values = new double[array.Elements.Count];
        for (var i = 0; i < values.Length; i++) values[i] = Number(array.Elements[i]);
        return values;
    }

    public static PdfRect? Rect(this PdfDictionary owner, string key)
    {
        // PDFsharp swaps the array for a PdfRectangle once one of its own properties has read the entry.
        if (Resolve(owner.Elements[key]) is PdfRectangle r)
        {
            return new PdfRect(Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2), Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2));
        }
        var array = owner.Array(key);
        if (array is null || array.Elements.Count < 4) return null;
        var n = array.Numbers();
        return new PdfRect(Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), Math.Max(n[0], n[2]), Math.Max(n[1], n[3]));
    }

    public static PdfArray ToArray(this PdfRect rect, PdfDocument doc) => Reals(doc, rect.Left, rect.Bottom, rect.Right, rect.Top);

    public static PdfArray Reals(PdfDocument doc, params double[] values)
    {
        var array = new PdfArray(doc);
        foreach (var value in values) array.Elements.Add(new PdfReal(Math.Round(value, 4)));
        return array;
    }

    public static PdfArray Color(PdfDocument doc, uint rgb) =>
        Reals(doc, ((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);

    public static PdfString UnicodeString(string value) => new(value, PdfStringEncoding.Unicode);

    public static string DateString(DateTime local)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        return string.Create(CultureInfo.InvariantCulture,
            $"D:{local:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'");
    }

    /// <summary>Creates an indirect dictionary object owned by the document.</summary>
    public static PdfDictionary NewObject(PdfDocument doc)
    {
        var dict = new PdfDictionary(doc);
        doc.Internals.AddObject(dict);
        return dict;
    }

    /// <summary>Creates an indirect stream object with uncompressed content.</summary>
    public static PdfDictionary NewStream(PdfDocument doc, byte[] content)
    {
        var dict = NewObject(doc);
        dict.CreateStream(content);
        return dict;
    }

    public static PdfReference Ref(PdfDictionary indirect) =>
        indirect.Reference ?? throw new InvalidOperationException("The PDF object is not indirect.");

    /// <summary>The page's annotation array, created on demand.</summary>
    public static PdfArray Annots(PdfPage page, bool create)
    {
        var existing = page.Array("/Annots");
        if (existing is not null) return existing;
        var created = new PdfArray(page.Owner);
        if (create) page.Elements["/Annots"] = created;
        return created;
    }

    /// <summary>The page box PDFium displays (crop box clipped to the media box) and the page rotation.</summary>
    public static PageGeometry Geometry(PdfPage page)
    {
        var media = page.Rect("/MediaBox") ?? new PdfRect(0, 0, 612, 792);
        var box = media;
        if (page.Rect("/CropBox") is { } crop)
        {
            var clipped = new PdfRect(Math.Max(crop.Left, media.Left), Math.Max(crop.Bottom, media.Bottom),
                Math.Min(crop.Right, media.Right), Math.Min(crop.Top, media.Top));
            if (!clipped.IsEmpty) box = clipped;
        }
        var rotate = page.Int("/Rotate");
        return new PageGeometry(box, ((rotate / 90 % 4) + 4) % 4);
    }

    public static readonly Encoding WinAnsi = CreateWinAnsi();

    private static Encoding CreateWinAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));
    }
}

/// <summary>Builds PDF content-stream text with culture-invariant number formatting.</summary>
internal sealed class ContentBuilder
{
    private readonly StringBuilder _text = new();

    public static string F(double value)
    {
        var rounded = Math.Round(value, 3);
        return rounded.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public ContentBuilder Op(string op, params double[] operands)
    {
        foreach (var operand in operands) _text.Append(F(operand)).Append(' ');
        _text.Append(op).Append('\n');
        return this;
    }

    public ContentBuilder Raw(string text)
    {
        _text.Append(text).Append('\n');
        return this;
    }

    public ContentBuilder FillColor(uint rgb) => Op("rg", ((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);

    public ContentBuilder StrokeColor(uint rgb) => Op("RG", ((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);

    public ContentBuilder Rect(PdfRect r) => Op("re", r.Left, r.Bottom, r.Width, r.Height);

    public ContentBuilder Ellipse(PdfRect r)
    {
        const double k = 0.5522847498;
        double cx = (r.Left + r.Right) / 2, cy = (r.Bottom + r.Top) / 2, rx = r.Width / 2, ry = r.Height / 2;
        Op("m", cx + rx, cy);
        Op("c", cx + rx, cy + k * ry, cx + k * rx, cy + ry, cx, cy + ry);
        Op("c", cx - k * rx, cy + ry, cx - rx, cy + k * ry, cx - rx, cy);
        Op("c", cx - rx, cy - k * ry, cx - k * rx, cy - ry, cx, cy - ry);
        Op("c", cx + k * rx, cy - ry, cx + rx, cy - k * ry, cx + rx, cy);
        return Raw("h");
    }

    public ContentBuilder RoundedRect(PdfRect r, double radius)
    {
        const double k = 0.5522847498;
        var rad = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var c = rad * (1 - k);
        Op("m", r.Left + rad, r.Bottom);
        Op("l", r.Right - rad, r.Bottom);
        Op("c", r.Right - c, r.Bottom, r.Right, r.Bottom + c, r.Right, r.Bottom + rad);
        Op("l", r.Right, r.Top - rad);
        Op("c", r.Right, r.Top - c, r.Right - c, r.Top, r.Right - rad, r.Top);
        Op("l", r.Left + rad, r.Top);
        Op("c", r.Left + c, r.Top, r.Left, r.Top - c, r.Left, r.Top - rad);
        Op("l", r.Left, r.Bottom + rad);
        Op("c", r.Left, r.Bottom + c, r.Left + c, r.Bottom, r.Left + rad, r.Bottom);
        return Raw("h");
    }

    /// <summary>Shows one line of WinAnsi text at the given baseline origin.</summary>
    public ContentBuilder TextLine(string fontResource, double size, double x, double y, string text)
    {
        Raw("BT");
        Raw($"/{fontResource} {F(size)} Tf");
        Op("Tm", 1, 0, 0, 1, x, y);
        Raw($"({Escape(text)}) Tj");
        return Raw("ET");
    }

    public static string Escape(string text)
    {
        var bytes = PdfX.WinAnsi.GetBytes(text);
        var builder = new StringBuilder(bytes.Length + 8);
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    builder.Append('\\').Append((char)b);
                    break;
                case < 32 or > 126:
                    builder.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
                default:
                    builder.Append((char)b);
                    break;
            }
        }
        return builder.ToString();
    }

    public byte[] ToBytes() => Encoding.ASCII.GetBytes(_text.ToString());

    public override string ToString() => _text.ToString();
}
