using System.Runtime.InteropServices;

namespace PDF4Free.App.Services;

/// <summary>Acquires pages from a scanner through Windows Image Acquisition (WIA), using the scanner's own dialog.</summary>
public static class ScanService
{
    private const string PngFormat = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
    private const int ScannerDevice = 1;
    private const int ColorIntent = 1;
    private const int NoDeviceError = unchecked((int)0x80210015);

    /// <summary>Scans one page, then keeps scanning while <paramref name="askForMore"/> returns true. Returns PNG images.</summary>
    public static List<byte[]> Scan(Func<bool> askForMore)
    {
        var type = Type.GetTypeFromProgID("WIA.CommonDialog")
                   ?? throw new NotSupportedException("Windows Image Acquisition is not available on this computer.");
        dynamic dialog = Activator.CreateInstance(type)!;
        var pages = new List<byte[]>();
        do
        {
            dynamic? image;
            try
            {
                image = dialog.ShowAcquireImage(ScannerDevice, ColorIntent, 0, PngFormat, false, true, false);
            }
            catch (COMException ex) when (ex.HResult == NoDeviceError)
            {
                throw new InvalidOperationException("No scanner was found. Check that it is connected and switched on.", ex);
            }
            if (image is null) break;
            var temp = Path.Combine(Path.GetTempPath(), $"pdf4free-scan-{Guid.NewGuid():N}.png");
            try
            {
                image.SaveFile(temp);
                pages.Add(File.ReadAllBytes(temp));
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        while (askForMore());
        return pages;
    }
}
