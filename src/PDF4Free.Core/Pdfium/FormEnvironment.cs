using System.Runtime.InteropServices;

namespace PDF4Free.Core.Pdfium;

/// <summary>
/// PDFium's "form fill environment" for one document. PDFium only draws form fields through it,
/// so every loaded document gets one. No scripting or interactive editing is wired up: the callbacks do nothing.
/// </summary>
internal sealed class FormEnvironment : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ThisOnly(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RectCallback(IntPtr self, IntPtr page, double left, double top, double right, double bottom);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void IntCallback(IntPtr self, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetTimerCallback(IntPtr self, int elapse, IntPtr timerFunction);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate SystemTime LocalTimeCallback(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetPageCallback(IntPtr self, IntPtr document, int pageIndex);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetCurrentPageCallback(IntPtr self, IntPtr document);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetRotationCallback(IntPtr self, IntPtr page);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PointerCallback(IntPtr self, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FocusCallback(IntPtr self, IntPtr value, uint length, int isFocus);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GoToCallback(IntPtr self, int pageIndex, int zoomMode, IntPtr positions, int count);

    // The delegates are static so the native side can never call into a collected object.
    private static readonly ThisOnly Nothing = _ => { };
    private static readonly RectCallback NoRect = (_, _, _, _, _, _) => { };
    private static readonly IntCallback NoInt = (_, _) => { };
    private static readonly SetTimerCallback NoTimer = (_, _, _) => 1;
    private static readonly LocalTimeCallback LocalTime = _ =>
    {
        var now = DateTime.Now;
        return new SystemTime
        {
            Year = (ushort)now.Year, Month = (ushort)now.Month, DayOfWeek = (ushort)now.DayOfWeek, Day = (ushort)now.Day,
            Hour = (ushort)now.Hour, Minute = (ushort)now.Minute, Second = (ushort)now.Second, Milliseconds = (ushort)now.Millisecond,
        };
    };
    private static readonly GetPageCallback NoPage = (_, _, _) => IntPtr.Zero;
    private static readonly GetCurrentPageCallback NoCurrentPage = (_, _) => IntPtr.Zero;
    private static readonly GetRotationCallback NoRotation = (_, _) => 0;
    private static readonly PointerCallback NoPointer = (_, _) => { };
    private static readonly FocusCallback NoFocus = (_, _, _, _) => { };
    private static readonly GoToCallback NoGoTo = (_, _, _, _, _) => { };

    private static readonly Delegate[] Table =
    [
        Nothing,        // Release
        NoRect,         // FFI_Invalidate
        NoRect,         // FFI_OutputSelectedRect
        NoInt,          // FFI_SetCursor
        NoTimer,        // FFI_SetTimer
        NoInt,          // FFI_KillTimer
        LocalTime,      // FFI_GetLocalTime
        Nothing,        // FFI_OnChange
        NoPage,         // FFI_GetPage
        NoCurrentPage,  // FFI_GetCurrentPage
        NoRotation,     // FFI_GetRotation
        NoPointer,      // FFI_ExecuteNamedAction
        NoFocus,        // FFI_SetTextFieldFocus
        NoPointer,      // FFI_DoURIAction
        NoGoTo,         // FFI_DoGoToAction
    ];

    private const int InfoSize = 1024;

    private IntPtr _info;

    private FormEnvironment(IntPtr info, IntPtr handle)
    {
        _info = info;
        Handle = handle;
    }

    public IntPtr Handle { get; private set; }

    /// <summary>Must be called while holding <see cref="PdfiumLibrary.Sync"/>. Returns null when PDFium refuses.</summary>
    public static FormEnvironment? Create(IntPtr document)
    {
        var info = Marshal.AllocHGlobal(InfoSize);
        for (var offset = 0; offset < InfoSize; offset += 8) Marshal.WriteInt64(info, offset, 0);
        Marshal.WriteInt32(info, 0, 1);
        for (var i = 0; i < Table.Length; i++)
        {
            Marshal.WriteIntPtr(info, IntPtr.Size * (i + 1), Marshal.GetFunctionPointerForDelegate(Table[i]));
        }
        var handle = Native.FPDFDOC_InitFormFillEnvironment(document, info);
        if (handle == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(info);
            return null;
        }
        Native.FPDF_SetFormFieldHighlightAlpha(handle, 0);
        return new FormEnvironment(info, handle);
    }

    /// <summary>Must be called while holding <see cref="PdfiumLibrary.Sync"/>, before the document is closed.</summary>
    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.FPDFDOC_ExitFormFillEnvironment(Handle);
            Handle = IntPtr.Zero;
        }
        if (_info != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_info);
            _info = IntPtr.Zero;
        }
    }
}
