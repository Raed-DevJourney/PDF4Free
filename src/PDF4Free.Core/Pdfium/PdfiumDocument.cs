using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF4Free.Core.Model;

namespace PDF4Free.Core.Pdfium;

/// <summary>Owns the one-time initialisation of PDFium and the lock that serialises every native call.</summary>
public static class PdfiumLibrary
{
    /// <summary>PDFium is not thread safe: hold this lock around every native call.</summary>
    public static readonly object Sync = new();

    private static bool _initialised;

    internal static void EnsureInitialised()
    {
        if (_initialised) return;
        lock (Sync)
        {
            if (_initialised) return;
            Native.FPDF_InitLibrary();
            _initialised = true;
        }
    }
}

/// <summary>A PDF loaded into PDFium from memory. Used for rendering and for reading document structure.</summary>
public sealed class PdfiumDocument : IDisposable
{
    private readonly GCHandle _pin;
    private readonly Dictionary<int, PageGeometry> _geometry = [];
    private readonly Dictionary<int, PageText> _text = [];
    private readonly Dictionary<int, IReadOnlyList<LinkInfo>> _links = [];
    private IntPtr _doc;
    private FormEnvironment? _forms;

    private PdfiumDocument(IntPtr doc, GCHandle pin, int length)
    {
        _doc = doc;
        _pin = pin;
        _forms = FormEnvironment.Create(doc);
        ByteLength = length;
        PageCount = Native.FPDF_GetPageCount(doc);
        var revision = Native.FPDF_GetSecurityHandlerRevision(doc);
        Security = new DocumentSecurity(revision >= 0, revision, Native.FPDF_GetDocPermissions(doc));
        Native.FPDF_GetFileVersion(doc, out var version);
        FileVersion = version;
    }

    public int PageCount { get; }
    public int ByteLength { get; }
    public DocumentSecurity Security { get; }

    /// <summary>PDF version times ten (17 = PDF 1.7).</summary>
    public int FileVersion { get; }

    internal IntPtr Handle => _doc;

    public static PdfiumDocument Load(byte[] data, string? password = null)
    {
        PdfiumLibrary.EnsureInitialised();
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        lock (PdfiumLibrary.Sync)
        {
            var doc = Native.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)data.Length, string.IsNullOrEmpty(password) ? null : password);
            if (doc != IntPtr.Zero) return new PdfiumDocument(doc, pin, data.Length);
            var error = Native.FPDF_GetLastError();
            pin.Free();
            if (error == Native.FPDF_ERR_PASSWORD) throw new PdfPasswordRequiredException(!string.IsNullOrEmpty(password));
            throw new PdfLoadException("The file is not a PDF document or is damaged.");
        }
    }

    public void Dispose()
    {
        lock (PdfiumLibrary.Sync)
        {
            if (_doc == IntPtr.Zero) return;
            _forms?.Dispose();
            _forms = null;
            Native.FPDF_CloseDocument(_doc);
            _doc = IntPtr.Zero;
            _pin.Free();
        }
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>The displayed size of a page in points, read without loading the page (fast enough to call for every page).</summary>
    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            if (_geometry.TryGetValue(pageIndex, out var known)) return (known.ViewWidth, known.ViewHeight);
            return Native.FPDF_GetPageSizeByIndexF(_doc, pageIndex, out var size) != 0 && size.Width > 0 && size.Height > 0
                ? (size.Width, size.Height)
                : (612, 792);
        }
    }

    public PageGeometry GetGeometry(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            if (_geometry.TryGetValue(pageIndex, out var cached)) return cached;
            return WithPage(pageIndex, page =>
            {
                var rotation = Native.FPDFPage_GetRotation(page);
                PdfRect box;
                if (Native.FPDF_GetPageBoundingBox(page, out var r) != 0 && r.Right > r.Left && r.Top > r.Bottom)
                {
                    box = new PdfRect(r.Left, r.Bottom, r.Right, r.Top);
                }
                else
                {
                    var w = Native.FPDF_GetPageWidthF(page);
                    var h = Native.FPDF_GetPageHeightF(page);
                    box = rotation is 1 or 3 ? new PdfRect(0, 0, h, w) : new PdfRect(0, 0, w, h);
                }
                var geometry = new PageGeometry(box, rotation);
                _geometry[pageIndex] = geometry;
                return geometry;
            });
        }
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>Renders a region of the page. The page is laid out at <paramref name="fullWidth"/> x <paramref name="fullHeight"/> pixels
    /// and the rectangle starting at (<paramref name="x"/>, <paramref name="y"/>) is returned as 32-bit BGR pixels.</summary>
    public byte[] RenderRegion(int pageIndex, int fullWidth, int fullHeight, int x, int y, int width, int height, PageRenderOptions options)
    {
        var buffer = new byte[width * height * 4];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            lock (PdfiumLibrary.Sync)
            {
                ThrowIfDisposed();
                var page = Native.FPDF_LoadPage(_doc, pageIndex);
                if (page == IntPtr.Zero) return buffer;
                var bitmap = Native.FPDFBitmap_CreateEx(width, height, Native.FPDFBitmap_BGRA, pin.AddrOfPinnedObject(), width * 4);
                var forms = options.Annotations ? _forms?.Handle ?? IntPtr.Zero : IntPtr.Zero;
                try
                {
                    if (forms != IntPtr.Zero) Native.FORM_OnAfterLoadPage(page, forms);
                    Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, options.Background);
                    var flags = 0;
                    if (options.Annotations) flags |= Native.FPDF_ANNOT;
                    if (options.LcdText) flags |= Native.FPDF_LCD_TEXT;
                    if (options.Grayscale) flags |= Native.FPDF_GRAYSCALE;
                    if (options.ForPrint) flags |= Native.FPDF_PRINTING;
                    Native.FPDF_RenderPageBitmap(bitmap, page, -x, -y, fullWidth, fullHeight, options.ExtraRotation, flags);
                    // Form fields are only drawn by the form-fill layer.
                    if (forms != IntPtr.Zero) Native.FPDF_FFLDraw(forms, bitmap, page, -x, -y, fullWidth, fullHeight, options.ExtraRotation, flags);
                }
                finally
                {
                    if (forms != IntPtr.Zero) Native.FORM_OnBeforeClosePage(page, forms);
                    Native.FPDFBitmap_Destroy(bitmap);
                    Native.FPDF_ClosePage(page);
                }
            }
        }
        finally
        {
            pin.Free();
        }
        return buffer;
    }

    public byte[] Render(int pageIndex, int width, int height, PageRenderOptions? options = null) =>
        RenderRegion(pageIndex, width, height, 0, 0, width, height, options ?? PageRenderOptions.Default);

    /// <summary>Renders the whole page at the given resolution and returns a frozen bitmap.</summary>
    public BitmapSource RenderBitmap(int pageIndex, double dpi, PageRenderOptions? options = null)
    {
        var geometry = GetGeometry(pageIndex);
        var width = Math.Max(1, (int)Math.Round(geometry.ViewWidth * dpi / 72.0));
        var height = Math.Max(1, (int)Math.Round(geometry.ViewHeight * dpi / 72.0));
        var pixels = Render(pageIndex, width, height, options);
        return ToBitmap(pixels, width, height, dpi);
    }

    public static BitmapSource ToBitmap(byte[] pixels, int width, int height, double dpi = 96)
    {
        var bitmap = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgr32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // ------------------------------------------------------------------ text

    public PageText GetText(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            if (_text.TryGetValue(pageIndex, out var cached)) return cached;
            var text = WithPage(pageIndex, ReadText);
            _text[pageIndex] = text;
            return text;
        }
    }

    private static PageText ReadText(IntPtr page)
    {
        var textPage = Native.FPDFText_LoadPage(page);
        if (textPage == IntPtr.Zero) return new PageText("", []);
        try
        {
            var count = Native.FPDFText_CountChars(textPage);
            var builder = new StringBuilder(count);
            var boxes = new List<PdfRect>(count);
            for (var i = 0; i < count; i++)
            {
                var code = Native.FPDFText_GetUnicode(textPage, i);
                var box = default(PdfRect);
                if (Native.FPDFText_GetCharBox(textPage, i, out var left, out var right, out var bottom, out var top) != 0)
                {
                    box = new PdfRect(Math.Min(left, right), Math.Min(bottom, top), Math.Max(left, right), Math.Max(bottom, top));
                }
                AppendChar(builder, boxes, code, box);
            }
            return new PageText(builder.ToString(), [.. boxes]);
        }
        finally
        {
            Native.FPDFText_ClosePage(textPage);
        }
    }

    private static void AppendChar(StringBuilder builder, List<PdfRect> boxes, uint code, PdfRect box)
    {
        if (code == 0 || code == 0xFFFE || code == 0xFFFF) code = ' ';
        if (code > 0xFFFF)
        {
            var s = char.ConvertFromUtf32((int)Math.Min(code, 0x10FFFF));
            builder.Append(s);
            boxes.Add(box);
            boxes.Add(default);
            return;
        }
        builder.Append((char)code);
        boxes.Add(box);
    }

    // ------------------------------------------------------------------ bookmarks and links

    public List<BookmarkNode> GetBookmarks()
    {
        var roots = new List<BookmarkNode>();
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            ReadBookmarks(IntPtr.Zero, roots, 0, new HashSet<IntPtr>());
        }
        return roots;
    }

    private void ReadBookmarks(IntPtr parent, List<BookmarkNode> target, int depth, HashSet<IntPtr> seen)
    {
        if (depth > 32) return;
        var item = Native.FPDFBookmark_GetFirstChild(_doc, parent);
        while (item != IntPtr.Zero && seen.Add(item) && seen.Count < 20000)
        {
            var (page, top, uri) = ResolveTarget(Native.FPDFBookmark_GetDest(_doc, item), Native.FPDFBookmark_GetAction(item));
            var node = new BookmarkNode
            {
                Title = ReadUtf16((buffer, length) => Native.FPDFBookmark_GetTitle(item, buffer, length)),
                PageIndex = page,
                Top = top,
                Uri = uri,
            };
            ReadBookmarks(item, node.Children, depth + 1, seen);
            target.Add(node);
            item = Native.FPDFBookmark_GetNextSibling(_doc, item);
        }
    }

    private (int Page, double? Top, string? Uri) ResolveTarget(IntPtr dest, IntPtr action)
    {
        string? uri = null;
        if (dest == IntPtr.Zero && action != IntPtr.Zero)
        {
            var type = Native.FPDFAction_GetType(action);
            if (type == Native.PDFACTION_GOTO) dest = Native.FPDFAction_GetDest(_doc, action);
            else if (type == Native.PDFACTION_URI) uri = ReadAnsi((buffer, length) => Native.FPDFAction_GetURIPath(_doc, action, buffer, length));
        }
        if (dest == IntPtr.Zero) return (-1, null, uri);
        var page = Native.FPDFDest_GetDestPageIndex(_doc, dest);
        double? top = null;
        if (Native.FPDFDest_GetLocationInPage(dest, out _, out var hasY, out _, out _, out var y, out _) != 0 && hasY != 0) top = y;
        return (page, top, uri);
    }

    public IReadOnlyList<LinkInfo> GetLinks(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            if (_links.TryGetValue(pageIndex, out var cached)) return cached;
            var links = WithPage(pageIndex, page =>
            {
                var list = new List<LinkInfo>();
                var position = 0;
                while (Native.FPDFLink_Enumerate(page, ref position, out var link) != 0 && list.Count < 5000)
                {
                    if (Native.FPDFLink_GetAnnotRect(link, out var r) == 0) continue;
                    var (target, top, uri) = ResolveTarget(Native.FPDFLink_GetDest(_doc, link), Native.FPDFLink_GetAction(link));
                    if (target < 0 && uri is null) continue;
                    list.Add(new LinkInfo(Normalise(r), target, top, uri));
                }
                return list;
            });
            _links[pageIndex] = links;
            return links;
        }
    }

    // ------------------------------------------------------------------ annotations

    private static readonly string[] SubtypeNames =
    [
        "Unknown", "Text", "Link", "FreeText", "Line", "Square", "Circle", "Polygon", "PolyLine", "Highlight", "Underline",
        "Squiggly", "StrikeOut", "Stamp", "Caret", "Ink", "Popup", "FileAttachment", "Sound", "Movie", "Widget", "Screen",
        "PrinterMark", "TrapNet", "Watermark", "3D", "RichMedia", "XFAWidget", "Redact",
    ];

    public List<AnnotationInfo> GetAnnotations(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            return WithPage(pageIndex, page =>
            {
                var list = new List<AnnotationInfo>();
                var count = Native.FPDFPage_GetAnnotCount(page);
                for (var i = 0; i < count; i++)
                {
                    var annot = Native.FPDFPage_GetAnnot(page, i);
                    if (annot == IntPtr.Zero) continue;
                    try
                    {
                        list.Add(ReadAnnotation(pageIndex, i, annot));
                    }
                    finally
                    {
                        Native.FPDFPage_CloseAnnot(annot);
                    }
                }
                return list;
            });
        }
    }

    private static AnnotationInfo ReadAnnotation(int pageIndex, int index, IntPtr annot)
    {
        var subtype = Native.FPDFAnnot_GetSubtype(annot);
        var name = subtype >= 0 && subtype < SubtypeNames.Length ? SubtypeNames[subtype] : "Unknown";
        Native.FPDFAnnot_GetRect(annot, out var r);
        uint color = 0xFFD400;
        if (Native.FPDFAnnot_GetColor(annot, 0, out var red, out var green, out var blue, out _) != 0)
        {
            color = (red << 16) | (green << 8) | blue;
        }
        var flags = Native.FPDFAnnot_GetFlags(annot);
        return new AnnotationInfo(
            pageIndex,
            index,
            name,
            Normalise(r),
            ReadUtf16((buffer, length) => Native.FPDFAnnot_GetStringValue(annot, "Contents", buffer, length)),
            ReadUtf16((buffer, length) => Native.FPDFAnnot_GetStringValue(annot, "T", buffer, length)),
            ReadUtf16((buffer, length) => Native.FPDFAnnot_GetStringValue(annot, "M", buffer, length)),
            color,
            (flags & 0x2) != 0);
    }

    // ------------------------------------------------------------------ page objects

    public List<PageObjectInfo> GetPageObjects(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            return WithPage(pageIndex, PageObjectReader.Read);
        }
    }

    public BitmapSource? GetImageObjectBitmap(int pageIndex, int objectIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            return WithPage(pageIndex, page =>
            {
                var obj = Native.FPDFPage_GetObject(page, objectIndex);
                if (obj == IntPtr.Zero || Native.FPDFPageObj_GetType(obj) != Native.PAGEOBJ_IMAGE) return null;
                var bitmap = Native.FPDFImageObj_GetRenderedBitmap(_doc, page, obj);
                return bitmap == IntPtr.Zero ? null : PageObjectReader.CopyBitmap(bitmap);
            });
        }
    }

    // ------------------------------------------------------------------ attachments, signatures, metadata

    public List<AttachmentInfo> GetAttachments()
    {
        var list = new List<AttachmentInfo>();
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            var count = Native.FPDFDoc_GetAttachmentCount(_doc);
            for (var i = 0; i < count; i++)
            {
                var attachment = Native.FPDFDoc_GetAttachment(_doc, i);
                if (attachment == IntPtr.Zero) continue;
                var name = ReadUtf16((buffer, length) => Native.FPDFAttachment_GetName(attachment, buffer, length));
                Native.FPDFAttachment_GetFile(attachment, IntPtr.Zero, 0, out var size);
                list.Add(new AttachmentInfo(i, name, size));
            }
        }
        return list;
    }

    public byte[] GetAttachmentData(int index)
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            var attachment = Native.FPDFDoc_GetAttachment(_doc, index);
            if (attachment == IntPtr.Zero) return [];
            Native.FPDFAttachment_GetFile(attachment, IntPtr.Zero, 0, out var size);
            if (size == 0) return [];
            var data = new byte[size];
            var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                Native.FPDFAttachment_GetFile(attachment, pin.AddrOfPinnedObject(), size, out _);
            }
            finally
            {
                pin.Free();
            }
            return data;
        }
    }

    public List<RawSignature> GetSignatures()
    {
        var list = new List<RawSignature>();
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            var count = Native.FPDF_GetSignatureCount(_doc);
            for (var i = 0; i < count; i++)
            {
                var signature = Native.FPDF_GetSignatureObject(_doc, i);
                if (signature == IntPtr.Zero) continue;
                var contents = ReadBytes((buffer, length) => Native.FPDFSignatureObj_GetContents(signature, buffer, length));
                var rangeCount = Native.FPDFSignatureObj_GetByteRange(signature, null, 0);
                var range = new int[rangeCount];
                if (rangeCount > 0) Native.FPDFSignatureObj_GetByteRange(signature, range, rangeCount);
                list.Add(new RawSignature(
                    contents,
                    range,
                    ReadAnsi((buffer, length) => Native.FPDFSignatureObj_GetSubFilter(signature, buffer, length)),
                    ReadUtf16((buffer, length) => Native.FPDFSignatureObj_GetReason(signature, buffer, length)),
                    ReadAnsi((buffer, length) => Native.FPDFSignatureObj_GetTime(signature, buffer, length))));
            }
        }
        return list;
    }

    public string GetMeta(string tag)
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            return ReadUtf16((buffer, length) => Native.FPDF_GetMetaText(_doc, tag, buffer, length));
        }
    }

    public string GetPageLabel(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            return ReadUtf16((buffer, length) => Native.FPDF_GetPageLabel(_doc, pageIndex, buffer, length));
        }
    }

    /// <summary>True when the document carries an XFA form, which PDF4Free cannot fill.</summary>
    public bool HasXfaForm
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                ThrowIfDisposed();
                return Native.FPDF_GetFormType(_doc) >= 2;
            }
        }
    }

    // ------------------------------------------------------------------ saving

    /// <summary>Serialises the document as PDFium currently holds it.</summary>
    public byte[] Save(bool removeSecurity = false)
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            return SaveLocked(_doc, removeSecurity);
        }
    }

    internal static byte[] SaveLocked(IntPtr doc, bool removeSecurity)
    {
        using var output = new MemoryStream();
        WriteBlockCallback callback = (_, data, size) =>
        {
            var chunk = new byte[size];
            Marshal.Copy(data, chunk, 0, (int)size);
            output.Write(chunk, 0, chunk.Length);
            return 1;
        };
        var fileWrite = new FpdfFileWrite { Version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(callback) };
        var ok = Native.FPDF_SaveAsCopy(doc, ref fileWrite, (uint)(removeSecurity ? Native.FPDF_REMOVE_SECURITY : Native.FPDF_NO_INCREMENTAL));
        GC.KeepAlive(callback);
        if (ok == 0) throw new InvalidOperationException("PDFium could not save the document.");
        return output.ToArray();
    }

    // ------------------------------------------------------------------ helpers

    internal T WithPage<T>(int pageIndex, Func<IntPtr, T> action)
    {
        ThrowIfDisposed();
        var page = Native.FPDF_LoadPage(_doc, pageIndex);
        if (page == IntPtr.Zero) throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page {pageIndex + 1} could not be loaded.");
        try
        {
            return action(page);
        }
        finally
        {
            Native.FPDF_ClosePage(page);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_doc == IntPtr.Zero, this);

    internal static PdfRect Normalise(FsRectF r) =>
        new(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));

    internal static byte[] ReadBytes(Func<IntPtr, uint, uint> read)
    {
        var size = read(IntPtr.Zero, 0);
        if (size == 0) return [];
        var data = new byte[size];
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            read(pin.AddrOfPinnedObject(), size);
        }
        finally
        {
            pin.Free();
        }
        return data;
    }

    /// <summary>Reads a PDFium "UTF-16LE, length in bytes including the terminator" string.</summary>
    internal static string ReadUtf16(Func<IntPtr, uint, uint> read)
    {
        var data = ReadBytes(read);
        return data.Length < 2 ? "" : Encoding.Unicode.GetString(data, 0, data.Length - 2).TrimEnd('\0');
    }

    internal static string ReadAnsi(Func<IntPtr, uint, uint> read)
    {
        var data = ReadBytes(read);
        return data.Length < 1 ? "" : Encoding.UTF8.GetString(data).TrimEnd('\0');
    }
}

public sealed record PageRenderOptions(
    bool Annotations = true,
    bool LcdText = false,
    bool Grayscale = false,
    bool ForPrint = false,
    uint Background = 0xFFFFFFFF,
    int ExtraRotation = 0)
{
    public static PageRenderOptions Default { get; } = new();
}
