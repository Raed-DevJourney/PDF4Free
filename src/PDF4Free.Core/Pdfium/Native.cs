using System.Runtime.InteropServices;

namespace PDF4Free.Core.Pdfium;

[StructLayout(LayoutKind.Sequential)]
internal struct FsRectF
{
    public float Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FsSizeF
{
    public float Width, Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FsPointF
{
    public float X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FsMatrix
{
    public float A, B, C, D, E, F;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FsQuadPointsF
{
    public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int WriteBlockCallback(IntPtr self, IntPtr data, uint size);

[StructLayout(LayoutKind.Sequential)]
internal struct FpdfFileWrite
{
    public int Version;
    public IntPtr WriteBlock;
}

/// <summary>Raw P/Invoke surface of pdfium.dll. Every call must be made while holding <see cref="PdfiumLibrary.Sync"/>.</summary>
internal static class Native
{
    private const string Dll = "pdfium";

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_GRAYSCALE = 0x08;
    public const int FPDF_PRINTING = 0x800;
    public const int FPDF_REVERSE_BYTE_ORDER = 0x10;

    public const int FPDFBitmap_BGRx = 3;
    public const int FPDFBitmap_BGRA = 4;

    public const uint FPDF_ERR_PASSWORD = 4;

    public const int FPDF_NO_INCREMENTAL = 2;
    public const int FPDF_REMOVE_SECURITY = 3;

    public const int PAGEOBJ_TEXT = 1, PAGEOBJ_PATH = 2, PAGEOBJ_IMAGE = 3, PAGEOBJ_SHADING = 4, PAGEOBJ_FORM = 5;

    public const uint PDFACTION_GOTO = 1, PDFACTION_REMOTEGOTO = 2, PDFACTION_URI = 3, PDFACTION_LAUNCH = 4;

    // ---- library ----
    [DllImport(Dll)] public static extern void FPDF_InitLibrary();
    [DllImport(Dll)] public static extern void FPDF_DestroyLibrary();
    [DllImport(Dll)] public static extern uint FPDF_GetLastError();

    // ---- document ----
    [DllImport(Dll)] public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Dll)] public static extern int FPDF_GetFileVersion(IntPtr doc, out int version);
    [DllImport(Dll)] public static extern uint FPDF_GetDocPermissions(IntPtr doc);
    [DllImport(Dll)] public static extern uint FPDF_GetDocUserPermissions(IntPtr doc);
    [DllImport(Dll)] public static extern int FPDF_GetSecurityHandlerRevision(IntPtr doc);
    [DllImport(Dll)] public static extern uint FPDF_GetMetaText(IntPtr doc, [MarshalAs(UnmanagedType.LPUTF8Str)] string tag, IntPtr buffer, uint buflen);
    [DllImport(Dll)] public static extern uint FPDF_GetPageLabel(IntPtr doc, int pageIndex, IntPtr buffer, uint buflen);
    [DllImport(Dll)] public static extern IntPtr FPDF_CreateNewDocument();
    [DllImport(Dll)] public static extern int FPDF_SaveAsCopy(IntPtr doc, ref FpdfFileWrite fileWrite, uint flags);
    [DllImport(Dll)] public static extern int FPDF_ImportPages(IntPtr destDoc, IntPtr srcDoc, [MarshalAs(UnmanagedType.LPUTF8Str)] string? pageRange, int index);
    [DllImport(Dll)] public static extern int FPDF_GetFormType(IntPtr doc);

    // ---- page ----
    [DllImport(Dll)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Dll)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Dll)] public static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Dll)] public static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Dll)] public static extern int FPDF_GetPageBoundingBox(IntPtr page, out FsRectF rect);
    [DllImport(Dll)] public static extern int FPDF_GetPageSizeByIndexF(IntPtr doc, int index, out FsSizeF size);
    [DllImport(Dll)] public static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport(Dll)] public static extern void FPDFPage_SetRotation(IntPtr page, int rotate);
    [DllImport(Dll)] public static extern int FPDFPage_Flatten(IntPtr page, int flag);
    [DllImport(Dll)] public static extern int FPDFPage_GenerateContent(IntPtr page);
    [DllImport(Dll)] public static extern int FPDFPage_GetMediaBox(IntPtr page, out float left, out float bottom, out float right, out float top);
    [DllImport(Dll)] public static extern int FPDFPage_GetCropBox(IntPtr page, out float left, out float bottom, out float right, out float top);
    [DllImport(Dll)] public static extern int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);

    // ---- rendering ----
    [DllImport(Dll)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [DllImport(Dll)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Dll)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Dll)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFBitmap_GetWidth(IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFBitmap_GetHeight(IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFBitmap_GetFormat(IntPtr bitmap);
    [DllImport(Dll)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // ---- form fill environment (needed to draw form fields) ----
    [DllImport(Dll)] public static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr doc, IntPtr formInfo);
    [DllImport(Dll)] public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr handle);
    [DllImport(Dll)] public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr handle);
    [DllImport(Dll)] public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr handle);
    [DllImport(Dll)] public static extern void FPDF_FFLDraw(IntPtr handle, IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Dll)] public static extern void FPDF_SetFormFieldHighlightAlpha(IntPtr handle, byte alpha);

    // ---- text ----
    [DllImport(Dll)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Dll)] public static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Dll)] public static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Dll)] public static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [DllImport(Dll)] public static extern double FPDFText_GetFontSize(IntPtr textPage, int index);
    [DllImport(Dll)] public static extern int FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);
    [DllImport(Dll)] public static extern int FPDFText_GetLooseCharBox(IntPtr textPage, int index, out FsRectF rect);
    [DllImport(Dll)] public static extern int FPDFText_GetFillColor(IntPtr textPage, int index, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Dll)] public static extern uint FPDFText_GetFontInfo(IntPtr textPage, int index, IntPtr buffer, uint buflen, out int flags);
    [DllImport(Dll)] public static extern int FPDFText_GetFontWeight(IntPtr textPage, int index);
    [DllImport(Dll)] public static extern IntPtr FPDFText_GetTextObject(IntPtr textPage, int index);
    [DllImport(Dll)] public static extern int FPDFText_GetCharOrigin(IntPtr textPage, int index, out double x, out double y);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetMatrix(IntPtr pageObject, out FsMatrix matrix);

    // ---- page objects ----
    [DllImport(Dll)] public static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Dll)] public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [DllImport(Dll)] public static extern int FPDFPage_RemoveObject(IntPtr page, IntPtr pageObject);
    [DllImport(Dll)] public static extern void FPDFPageObj_Destroy(IntPtr pageObject);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetType(IntPtr pageObject);
    [DllImport(Dll)] public static extern void FPDFPageObj_Transform(IntPtr pageObject, double a, double b, double c, double d, double e, double f);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetBounds(IntPtr pageObject, out float left, out float bottom, out float right, out float top);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetFillColor(IntPtr pageObject, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Dll)] public static extern int FPDFTextObj_GetFontSize(IntPtr textObject, out float size);
    [DllImport(Dll)] public static extern uint FPDFTextObj_GetText(IntPtr textObject, IntPtr textPage, IntPtr buffer, uint length);
    [DllImport(Dll)] public static extern IntPtr FPDFTextObj_GetFont(IntPtr textObject);
    [DllImport(Dll)] public static extern nuint FPDFFont_GetBaseFontName(IntPtr font, IntPtr buffer, nuint length);
    [DllImport(Dll)] public static extern nuint FPDFFont_GetFamilyName(IntPtr font, IntPtr buffer, nuint length);
    [DllImport(Dll)] public static extern int FPDFFont_GetWeight(IntPtr font);
    [DllImport(Dll)] public static extern int FPDFFont_GetItalicAngle(IntPtr font, out int angle);
    [DllImport(Dll)] public static extern int FPDFFont_GetIsEmbedded(IntPtr font);
    [DllImport(Dll)] public static extern int FPDFImageObj_GetImagePixelSize(IntPtr imageObject, out uint width, out uint height);
    [DllImport(Dll)] public static extern IntPtr FPDFImageObj_GetRenderedBitmap(IntPtr doc, IntPtr page, IntPtr imageObject);
    [DllImport(Dll)] public static extern IntPtr FPDFImageObj_GetBitmap(IntPtr imageObject);

    // ---- annotations (read side) ----
    [DllImport(Dll)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Dll)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Dll)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetRect(IntPtr annot, out FsRectF rect);
    [DllImport(Dll)] public static extern uint FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, IntPtr buffer, uint buflen);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetColor(IntPtr annot, int type, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Dll)] public static extern nuint FPDFAnnot_CountAttachmentPoints(IntPtr annot);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetAttachmentPoints(IntPtr annot, nuint quadIndex, out FsQuadPointsF quad);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetFlags(IntPtr annot);
    [DllImport(Dll)] public static extern uint FPDFAnnot_GetFormAdditionalActionJavaScript(IntPtr form, IntPtr annot, int fieldEvent, IntPtr buffer, uint buflen);

    // ---- bookmarks, links, actions ----
    [DllImport(Dll)] public static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr doc, IntPtr bookmark);
    [DllImport(Dll)] public static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr doc, IntPtr bookmark);
    [DllImport(Dll)] public static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, IntPtr buffer, uint buflen);
    [DllImport(Dll)] public static extern IntPtr FPDFBookmark_GetDest(IntPtr doc, IntPtr bookmark);
    [DllImport(Dll)] public static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);
    [DllImport(Dll)] public static extern uint FPDFAction_GetType(IntPtr action);
    [DllImport(Dll)] public static extern IntPtr FPDFAction_GetDest(IntPtr doc, IntPtr action);
    [DllImport(Dll)] public static extern uint FPDFAction_GetURIPath(IntPtr doc, IntPtr action, IntPtr buffer, uint buflen);
    [DllImport(Dll)] public static extern int FPDFDest_GetDestPageIndex(IntPtr doc, IntPtr dest);
    [DllImport(Dll)] public static extern int FPDFDest_GetLocationInPage(IntPtr dest, out int hasX, out int hasY, out int hasZoom, out float x, out float y, out float zoom);
    [DllImport(Dll)] public static extern int FPDFLink_Enumerate(IntPtr page, ref int startPos, out IntPtr link);
    [DllImport(Dll)] public static extern IntPtr FPDFLink_GetDest(IntPtr doc, IntPtr link);
    [DllImport(Dll)] public static extern IntPtr FPDFLink_GetAction(IntPtr link);
    [DllImport(Dll)] public static extern int FPDFLink_GetAnnotRect(IntPtr link, out FsRectF rect);

    // ---- attachments ----
    [DllImport(Dll)] public static extern int FPDFDoc_GetAttachmentCount(IntPtr doc);
    [DllImport(Dll)] public static extern IntPtr FPDFDoc_GetAttachment(IntPtr doc, int index);
    [DllImport(Dll)] public static extern IntPtr FPDFDoc_AddAttachment(IntPtr doc, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [DllImport(Dll)] public static extern int FPDFDoc_DeleteAttachment(IntPtr doc, int index);
    [DllImport(Dll)] public static extern uint FPDFAttachment_GetName(IntPtr attachment, IntPtr buffer, uint buflen);
    [DllImport(Dll)] public static extern int FPDFAttachment_GetFile(IntPtr attachment, IntPtr buffer, uint buflen, out uint outBuflen);
    [DllImport(Dll)] public static extern int FPDFAttachment_SetFile(IntPtr attachment, IntPtr doc, IntPtr contents, uint len);

    // ---- signatures ----
    [DllImport(Dll)] public static extern int FPDF_GetSignatureCount(IntPtr doc);
    [DllImport(Dll)] public static extern IntPtr FPDF_GetSignatureObject(IntPtr doc, int index);
    [DllImport(Dll)] public static extern uint FPDFSignatureObj_GetContents(IntPtr signature, IntPtr buffer, uint length);
    [DllImport(Dll)] public static extern uint FPDFSignatureObj_GetByteRange(IntPtr signature, [Out] int[]? buffer, uint length);
    [DllImport(Dll)] public static extern uint FPDFSignatureObj_GetSubFilter(IntPtr signature, IntPtr buffer, uint length);
    [DllImport(Dll)] public static extern uint FPDFSignatureObj_GetReason(IntPtr signature, IntPtr buffer, uint length);
    [DllImport(Dll)] public static extern uint FPDFSignatureObj_GetTime(IntPtr signature, IntPtr buffer, uint length);
}
