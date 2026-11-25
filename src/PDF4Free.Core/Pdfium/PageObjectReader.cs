using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF4Free.Core.Model;

namespace PDF4Free.Core.Pdfium;

/// <summary>Reads the top-level content objects (text runs, images, vector paths) of a page.</summary>
internal static class PageObjectReader
{
    public static List<PageObjectInfo> Read(IntPtr page)
    {
        var result = new List<PageObjectInfo>();
        var textPage = Native.FPDFText_LoadPage(page);
        try
        {
            var origins = ReadTextOrigins(textPage);
            var count = Native.FPDFPage_CountObjects(page);
            for (var i = 0; i < count; i++)
            {
                var obj = Native.FPDFPage_GetObject(page, i);
                if (obj == IntPtr.Zero) continue;
                if (Native.FPDFPageObj_GetBounds(obj, out var left, out var bottom, out var right, out var top) == 0) continue;
                var bounds = new PdfRect(Math.Min(left, right), Math.Min(bottom, top), Math.Max(left, right), Math.Max(bottom, top));
                var kind = ToKind(Native.FPDFPageObj_GetType(obj));
                result.Add(kind == PageObjectKind.Text
                    ? ReadText(i, obj, textPage, bounds, origins)
                    : new PageObjectInfo(i, kind, bounds, "", 0, "", false, false, 0, new PdfPoint(bounds.Left, bounds.Bottom)));
            }
        }
        finally
        {
            if (textPage != IntPtr.Zero) Native.FPDFText_ClosePage(textPage);
        }
        return result;
    }

    private static Dictionary<IntPtr, PdfPoint> ReadTextOrigins(IntPtr textPage)
    {
        var origins = new Dictionary<IntPtr, PdfPoint>();
        if (textPage == IntPtr.Zero) return origins;
        var count = Native.FPDFText_CountChars(textPage);
        for (var i = 0; i < count; i++)
        {
            var owner = Native.FPDFText_GetTextObject(textPage, i);
            if (owner == IntPtr.Zero || origins.ContainsKey(owner)) continue;
            if (Native.FPDFText_GetCharOrigin(textPage, i, out var x, out var y) != 0) origins[owner] = new PdfPoint(x, y);
        }
        return origins;
    }

    private static PageObjectInfo ReadText(int index, IntPtr obj, IntPtr textPage, PdfRect bounds, Dictionary<IntPtr, PdfPoint> origins)
    {
        var text = textPage == IntPtr.Zero
            ? ""
            : PdfiumDocument.ReadUtf16((buffer, length) => Native.FPDFTextObj_GetText(obj, textPage, buffer, length));
        Native.FPDFTextObj_GetFontSize(obj, out var size);
        double effective = size;
        if (Native.FPDFPageObj_GetMatrix(obj, out var m) != 0)
        {
            var scale = Math.Sqrt(m.C * m.C + m.D * m.D);
            if (scale > 0.0001) effective = size * scale;
        }
        if (effective < 1 || effective > 600) effective = Math.Clamp(bounds.Height, 4, 600);

        var font = Native.FPDFTextObj_GetFont(obj);
        var name = "";
        var bold = false;
        var italic = false;
        if (font != IntPtr.Zero)
        {
            name = ReadFontName(font);
            bold = Native.FPDFFont_GetWeight(font) >= 600 || name.Contains("Bold", StringComparison.OrdinalIgnoreCase);
            italic = (Native.FPDFFont_GetItalicAngle(font, out var angle) != 0 && angle != 0)
                     || name.Contains("Italic", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        }
        uint color = 0;
        if (Native.FPDFPageObj_GetFillColor(obj, out var r, out var g, out var b, out _) != 0) color = (r << 16) | (g << 8) | b;
        var origin = origins.TryGetValue(obj, out var o) ? o : new PdfPoint(bounds.Left, bounds.Bottom);
        return new PageObjectInfo(index, PageObjectKind.Text, bounds, text, effective, name, bold, italic, color, origin);
    }

    private static string ReadFontName(IntPtr font)
    {
        var family = ReadFontString((buffer, length) => Native.FPDFFont_GetFamilyName(font, buffer, length));
        if (family.Length > 0) return family;
        var baseName = ReadFontString((buffer, length) => Native.FPDFFont_GetBaseFontName(font, buffer, length));
        var plus = baseName.IndexOf('+');
        return plus >= 0 ? baseName[(plus + 1)..] : baseName;
    }

    private static string ReadFontString(Func<IntPtr, nuint, nuint> read) =>
        PdfiumDocument.ReadAnsi((buffer, length) => (uint)read(buffer, length));

    private static PageObjectKind ToKind(int type) => type switch
    {
        Native.PAGEOBJ_TEXT => PageObjectKind.Text,
        Native.PAGEOBJ_PATH => PageObjectKind.Path,
        Native.PAGEOBJ_IMAGE => PageObjectKind.Image,
        Native.PAGEOBJ_SHADING => PageObjectKind.Shading,
        Native.PAGEOBJ_FORM => PageObjectKind.Form,
        _ => PageObjectKind.Unknown,
    };

    /// <summary>Copies a PDFium bitmap into a frozen WPF bitmap and destroys the native one.</summary>
    public static BitmapSource? CopyBitmap(IntPtr bitmap)
    {
        try
        {
            var width = Native.FPDFBitmap_GetWidth(bitmap);
            var height = Native.FPDFBitmap_GetHeight(bitmap);
            var stride = Native.FPDFBitmap_GetStride(bitmap);
            var buffer = Native.FPDFBitmap_GetBuffer(bitmap);
            if (width <= 0 || height <= 0 || buffer == IntPtr.Zero) return null;
            var format = Native.FPDFBitmap_GetFormat(bitmap) switch
            {
                1 => PixelFormats.Gray8,
                2 => PixelFormats.Bgr24,
                3 => PixelFormats.Bgr32,
                _ => PixelFormats.Bgra32,
            };
            var copy = BitmapSource.Create(width, height, 96, 96, format, null, buffer, stride * height, stride);
            copy.Freeze();
            return copy;
        }
        finally
        {
            Native.FPDFBitmap_Destroy(bitmap);
        }
    }
}
