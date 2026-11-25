using System.ComponentModel;
using System.Windows.Media;

namespace PDF4Free.App.Views;

/// <summary>A row with a heading and a detail line, shown by the side-panel lists.</summary>
public sealed record ListRow(string Title, string Detail, object? Tag = null);

/// <summary>A page thumbnail whose picture is rendered the first time something asks for it (that is, when it becomes visible).</summary>
public sealed class PageThumb(int index, double width, double height, Func<int, Task<ImageSource?>> load) : INotifyPropertyChanged
{
    private ImageSource? _image;
    private bool _requested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; } = index;
    public string Label => (Index + 1).ToString();
    public double Width { get; } = width;
    public double Height { get; } = height;

    public ImageSource? Image
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                _ = LoadAsync();
            }
            return _image;
        }
    }

    private async Task LoadAsync()
    {
        var image = await load(Index);
        if (image is null) return;
        _image = image;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
    }

    /// <summary>Marks the picture stale; it is rendered again when next displayed (the old one stays until then).</summary>
    public void Invalidate()
    {
        _requested = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
    }

    /// <summary>Builds the thumbnails for a document. <paramref name="size"/> is the longest side in device-independent pixels.</summary>
    public static List<PageThumb> For(PDF4Free.Core.PdfSession session, double size, double dpiScale)
    {
        var thumbs = new List<PageThumb>(session.PageCount);
        for (var i = 0; i < session.PageCount; i++)
        {
            var (w, h) = session.Document.GetPageSize(i);
            var scale = size / Math.Max(w, h);
            double width = Math.Max(8, Math.Round(w * scale)), height = Math.Max(8, Math.Round(h * scale));
            thumbs.Add(new PageThumb(i, width, height, page => RenderAsync(session, page, width, height, dpiScale)));
        }
        return thumbs;
    }

    private static Task<ImageSource?> RenderAsync(PDF4Free.Core.PdfSession session, int page, double width, double height, double dpiScale) =>
        Task.Run<ImageSource?>(() =>
        {
            try
            {
                var pixelWidth = Math.Max(1, (int)Math.Round(width * dpiScale));
                var pixelHeight = Math.Max(1, (int)Math.Round(height * dpiScale));
                var document = session.Document;
                if (page >= document.PageCount) return null;
                return PDF4Free.Core.Pdfium.PdfiumDocument.ToBitmap(document.Render(page, pixelWidth, pixelHeight), pixelWidth, pixelHeight, 96 * dpiScale);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or ArgumentOutOfRangeException)
            {
                return null; // The document changed while the thumbnail was queued.
            }
        });
}
