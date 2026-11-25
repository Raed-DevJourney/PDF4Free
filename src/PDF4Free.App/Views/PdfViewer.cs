using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PDF4Free.Core;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.App.Views;

public enum FitMode { None, Width, Page }

/// <summary>
/// The scrolling page view of one document. Pages are laid out in a column; only the pages near the viewport
/// have visuals, and each is rendered by PDFium on a background thread at the exact device resolution.
/// </summary>
public sealed partial class PdfViewer : Grid
{
    private const double PageGap = 14;
    private const double EdgeMargin = 18;
    private const long MaxBasePixels = 9_000_000;
    public const double MinZoom = 0.1;
    public const double MaxZoom = 16;

    private readonly ScrollViewer _scroll;
    private readonly Canvas _surface;
    private readonly Dictionary<int, PageView> _pages = [];
    private readonly Dictionary<int, PageGeometry> _geometry = [];
    private readonly DispatcherTimer _renderDelay;
    private readonly HashSet<int> _renderQueue = [];
    private Rect[] _layout = [];
    private double _zoom = 1;
    private double _dpi = 1;
    private int _version;
    private bool _rendering;
    private bool _layoutPending;

    public PdfViewer()
    {
        Focusable = true;
        FocusVisualStyle = null;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        _surface = new Canvas { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            // Always present, so that fitting to the width cannot flip-flop as the bar appears and disappears.
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Focusable = false,
            Content = _surface,
            CanContentScroll = false,
        };
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        Children.Add(_scroll);

        _renderDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, (_, _) => OnRenderDelayElapsed(), Dispatcher) { IsEnabled = false };
        _scroll.ScrollChanged += (_, e) => OnScrollChanged(e);
        Loaded += (_, _) => _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        AttachInput();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpi = newDpi.DpiScaleX;
        RebuildLayout();
    }

    // ------------------------------------------------------------------ public surface

    public PdfSession? Session { get; private set; }
    public ToolOptions Options { get; } = new();
    public FitMode Fit { get; private set; } = FitMode.Width;
    public int CurrentPage { get; private set; }
    public double Zoom => _zoom;
    public int PageCount => Session?.PageCount ?? 0;

    /// <summary>Raised when the current page or the zoom changed.</summary>
    public event Action? ViewChanged;

    /// <summary>Raised when the active tool changed.</summary>
    public event Action? ToolChanged;

    /// <summary>Raised when the text, comment or object selection changed.</summary>
    public event Action? SelectionChanged;

    /// <summary>A short message for the status bar.</summary>
    public event Action<string>? Status;

    /// <summary>The user dragged the area for a certificate signature.</summary>
    public event Action<int, PdfRect>? CertificateAreaChosen;

    /// <summary>Runs an edit with a busy indicator and error handling; supplied by the main window.</summary>
    public Func<string, Func<byte[], byte[]>, ChangeKind, Task<bool>>? Edit { get; set; }

    public void Attach(PdfSession session)
    {
        if (Session is not null) Session.Changed -= OnSessionChanged;
        Session = session;
        session.Changed += OnSessionChanged;
        _version++;
        ResetCaches();
        ClearPages();
        RebuildLayout();
        _fieldsReload = ReloadFormFieldsAsync();
    }

    /// <summary>Points to device-independent pixels at the current zoom.</summary>
    private double Scale => _zoom * 96.0 / 72.0;

    // ------------------------------------------------------------------ layout

    private void OnSessionChanged(ChangeKind kind)
    {
        _version++;
        ResetCaches();
        ClearSelection(silent: true);
        if (kind == ChangeKind.Structure) RebuildLayout();
        foreach (var page in _pages.Values) QueueRender(page.Index);
        foreach (var page in _pages.Values) page.Overlay.InvalidateVisual();
        _fieldsReload = ReloadFormFieldsAsync();
        SelectionChanged?.Invoke();
        ViewChanged?.Invoke();
    }

    private void ResetCaches()
    {
        _geometry.Clear();
        _annotations.Clear();
        _objects.Clear();
    }

    private void ClearPages()
    {
        foreach (var page in _pages.Values) _surface.Children.Remove(page);
        _pages.Clear();
        _renderQueue.Clear();
    }

    private double Snap(double value) => Math.Round(value * _dpi) / _dpi;

    private void RebuildLayout()
    {
        if (Session is null)
        {
            _layout = [];
            return;
        }
        if (_scroll.ViewportWidth <= 0)
        {
            _layoutPending = true;
            return;
        }
        _layoutPending = false;
        ApplyFit();
        var hadLayout = _layout.Length > 0;
        var anchor = CaptureAnchor(new Point(_scroll.ViewportWidth / 2, 0));
        ComputeLayout();
        if (hadLayout) RestoreAnchor(anchor);
        RealizeVisiblePages();
        UpdateCurrentPage();
        ViewChanged?.Invoke();
    }

    private void ComputeLayout()
    {
        var session = Session!;
        var count = session.PageCount;
        var sizes = new Size[count];
        double widest = 0;
        for (var i = 0; i < count; i++)
        {
            var (w, h) = session.Document.GetPageSize(i);
            sizes[i] = new Size(Snap(w * Scale), Snap(h * Scale));
            widest = Math.Max(widest, sizes[i].Width);
        }
        var extentWidth = Math.Max(widest + 2 * EdgeMargin, _scroll.ViewportWidth);
        var layout = new Rect[count];
        var y = EdgeMargin;
        for (var i = 0; i < count; i++)
        {
            layout[i] = new Rect(Snap((extentWidth - sizes[i].Width) / 2), Snap(y), sizes[i].Width, sizes[i].Height);
            y += sizes[i].Height + PageGap;
        }
        _layout = layout;
        _surface.Width = extentWidth;
        _surface.Height = Math.Max(y - PageGap + EdgeMargin, _scroll.ViewportHeight);
        foreach (var (index, page) in _pages.ToList())
        {
            if (index >= count)
            {
                _surface.Children.Remove(page);
                _pages.Remove(index);
                continue;
            }
            Place(page);
        }
    }

    private void Place(PageView page)
    {
        var rect = _layout[page.Index];
        page.Resize(rect.Width, rect.Height);
        Canvas.SetLeft(page, rect.X);
        Canvas.SetTop(page, rect.Y);
    }

    private void ApplyFit()
    {
        if (Session is null || Fit == FitMode.None || Session.PageCount == 0) return;
        var available = _scroll.ViewportWidth - 2 * EdgeMargin - 2;
        if (available <= 50) return;
        double widest = 0;
        for (var i = 0; i < Session.PageCount; i++) widest = Math.Max(widest, Session.Document.GetPageSize(i).Width);
        var zoom = available / (widest * 96.0 / 72.0);
        if (Fit == FitMode.Page)
        {
            var (w, h) = Session.Document.GetPageSize(Math.Clamp(CurrentPage, 0, Session.PageCount - 1));
            zoom = Math.Min(available / (w * 96.0 / 72.0), (_scroll.ViewportHeight - 2 * EdgeMargin) / (h * 96.0 / 72.0));
        }
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    private void OnViewportResized()
    {
        if (Session is null) return;
        if (_layoutPending || Fit != FitMode.None) RebuildLayout();
        else
        {
            ComputeLayout();
            RealizeVisiblePages();
        }
    }

    // ------------------------------------------------------------------ zoom and navigation

    public void SetZoom(double zoom, Point? viewportAnchor = null)
    {
        if (Session is null) return;
        Fit = FitMode.None;
        var clamped = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(clamped - _zoom) < 0.0001) return;
        var anchor = CaptureAnchor(viewportAnchor ?? new Point(_scroll.ViewportWidth / 2, _scroll.ViewportHeight / 2));
        _zoom = clamped;
        ComputeLayout();
        RestoreAnchor(anchor);
        RealizeVisiblePages(renderNow: false);
        _renderDelay.Stop();
        _renderDelay.Start();
        ViewChanged?.Invoke();
    }

    public void SetFit(FitMode mode)
    {
        Fit = mode;
        RebuildLayout();
        foreach (var page in _pages.Values) QueueRender(page.Index);
        ViewChanged?.Invoke();
    }

    public void ZoomIn() => SetZoom(NextZoomStep(_zoom, +1));

    public void ZoomOut() => SetZoom(NextZoomStep(_zoom, -1));

    private static double NextZoomStep(double zoom, int direction)
    {
        double[] steps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 6, 8, 12, 16];
        if (direction > 0) return steps.FirstOrDefault(s => s > zoom + 0.001, MaxZoom);
        return steps.LastOrDefault(s => s < zoom - 0.001, MinZoom);
    }

    /// <summary>The document position under a viewport point, as (page, fraction across, fraction down).</summary>
    private (int Page, double FractionX, double FractionY, Point Viewport) CaptureAnchor(Point viewport)
    {
        if (_layout.Length == 0) return (0, 0, 0, viewport);
        var content = new Point(viewport.X + _scroll.HorizontalOffset, viewport.Y + _scroll.VerticalOffset);
        var index = PageNear(content.Y);
        var rect = _layout[index];
        return (index, (content.X - rect.X) / Math.Max(1, rect.Width), (content.Y - rect.Y) / Math.Max(1, rect.Height), viewport);
    }

    private void RestoreAnchor((int Page, double FractionX, double FractionY, Point Viewport) anchor)
    {
        if (_layout.Length == 0) return;
        var rect = _layout[Math.Clamp(anchor.Page, 0, _layout.Length - 1)];
        _scroll.ScrollToHorizontalOffset(rect.X + anchor.FractionX * rect.Width - anchor.Viewport.X);
        _scroll.ScrollToVerticalOffset(rect.Y + anchor.FractionY * rect.Height - anchor.Viewport.Y);
    }

    /// <summary>The page at a vertical content position, or the nearest one.</summary>
    private int PageNear(double y)
    {
        int low = 0, high = _layout.Length - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_layout[middle].Bottom + PageGap / 2 < y) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    public void GoToPage(int index, double? topInPoints = null)
    {
        if (_layout.Length == 0) return;
        index = Math.Clamp(index, 0, _layout.Length - 1);
        var offset = _layout[index].Y - EdgeMargin / 2;
        if (topInPoints is { } top && TryGeometry(index) is { } geometry)
        {
            offset += Math.Max(0, geometry.ToView(new PdfPoint(geometry.Box.Left, top)).Y * Scale - 20);
        }
        _scroll.ScrollToVerticalOffset(offset);
        _scroll.UpdateLayout();
        RealizeVisiblePages();
        UpdateCurrentPage();
    }

    /// <summary>Scrolls so that a rectangle on a page (PDF coordinates) is comfortably in view.</summary>
    public void BringIntoView(int pageIndex, PdfRect rect)
    {
        if (pageIndex < 0 || pageIndex >= _layout.Length || TryGeometry(pageIndex) is not { } geometry) return;
        var view = geometry.ToView(rect);
        var page = _layout[pageIndex];
        var target = new Rect(page.X + view.X * Scale, page.Y + view.Y * Scale, view.Width * Scale, view.Height * Scale);
        var visible = new Rect(_scroll.HorizontalOffset, _scroll.VerticalOffset, _scroll.ViewportWidth, _scroll.ViewportHeight);
        if (visible.Contains(target)) return;
        _scroll.ScrollToVerticalOffset(target.Y - _scroll.ViewportHeight / 3);
        if (target.Left < visible.Left || target.Right > visible.Right) _scroll.ScrollToHorizontalOffset(target.X - _scroll.ViewportWidth / 3);
        _scroll.UpdateLayout();
        RealizeVisiblePages();
    }

    private void OnScrollChanged(ScrollChangedEventArgs e)
    {
        if (Session is null) return;
        // The viewport size only becomes known (and changes) through this event, not through SizeChanged.
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
        {
            OnViewportResized();
            return;
        }
        RealizeVisiblePages();
        UpdateCurrentPage();
        if (e.VerticalChange != 0 || e.HorizontalChange != 0)
        {
            // Sharp tiles follow the viewport once scrolling settles.
            _renderDelay.Stop();
            _renderDelay.Start();
        }
    }

    private void UpdateCurrentPage()
    {
        if (_layout.Length == 0) return;
        var page = PageNear(_scroll.VerticalOffset + _scroll.ViewportHeight * 0.4);
        if (page == CurrentPage) return;
        CurrentPage = page;
        ViewChanged?.Invoke();
    }

    // ------------------------------------------------------------------ page realisation and rendering

    private void RealizeVisiblePages(bool renderNow = true)
    {
        if (Session is null || _layout.Length == 0) return;
        var top = _scroll.VerticalOffset - 500;
        var bottom = _scroll.VerticalOffset + _scroll.ViewportHeight + 500;
        var first = PageNear(top);
        var wanted = new HashSet<int>();
        for (var i = first; i < _layout.Length && _layout[i].Top <= bottom; i++) wanted.Add(i);

        foreach (var index in _pages.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _surface.Children.Remove(_pages[index]);
            _pages.Remove(index);
            _renderQueue.Remove(index);
        }
        foreach (var index in wanted)
        {
            if (!_pages.TryGetValue(index, out var page))
            {
                page = new PageView(index);
                var captured = page;
                page.Overlay.Painter = dc => PaintOverlay(dc, captured.Index);
                _pages[index] = page;
                _surface.Children.Add(page);
                Place(page);
            }
            if (renderNow) QueueRender(index);
        }
    }

    private void OnRenderDelayElapsed()
    {
        _renderDelay.Stop();
        foreach (var page in _pages.Values) QueueRender(page.Index);
    }

    private void QueueRender(int pageIndex)
    {
        if (!_pages.ContainsKey(pageIndex)) return;
        _renderQueue.Add(pageIndex);
        if (!_rendering) _ = ProcessRenderQueueAsync();
    }

    private async Task ProcessRenderQueueAsync()
    {
        _rendering = true;
        try
        {
            while (_renderQueue.Count > 0 && Session is not null)
            {
                var index = _renderQueue.OrderBy(i => Math.Abs(i - CurrentPage)).First();
                _renderQueue.Remove(index);
                if (!_pages.TryGetValue(index, out var page) || index >= _layout.Length) continue;
                await RenderPageAsync(page);
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private async Task RenderPageAsync(PageView page)
    {
        var session = Session!;
        var version = _version;
        var rect = _layout[page.Index];
        var fullWidth = Math.Max(1, (int)Math.Round(rect.Width * _dpi));
        var fullHeight = Math.Max(1, (int)Math.Round(rect.Height * _dpi));
        var exactScale = Scale * _dpi;
        var fitsInOneBitmap = (long)fullWidth * fullHeight <= MaxBasePixels;

        // The base bitmap: the whole page, at exact resolution when affordable, otherwise reduced.
        var reduce = fitsInOneBitmap ? 1.0 : Math.Sqrt((double)MaxBasePixels / ((double)fullWidth * fullHeight));
        var baseWidth = Math.Max(1, (int)Math.Round(fullWidth * reduce));
        var baseHeight = Math.Max(1, (int)Math.Round(fullHeight * reduce));
        var baseScale = exactScale * reduce;
        var index = page.Index;
        if (page.BaseVersion != version || Math.Abs(page.BaseScale - baseScale) > baseScale * 0.004)
        {
            var bitmap = await Task.Run(() => TryRender(session, () => PdfiumDocument.ToBitmap(session.Document.Render(index, baseWidth, baseHeight), baseWidth, baseHeight, 96 * _dpi * reduce)));
            if (bitmap is null || version != _version || !_pages.ContainsKey(index)) return;
            page.Base.Source = bitmap;
            page.BaseVersion = version;
            page.BaseScale = baseScale;
        }

        if (fitsInOneBitmap)
        {
            if (!page.TileArea.IsEmpty) page.HideTile();
            return;
        }

        // The sharp tile: only the part of the page that is on screen, at exact resolution.
        var viewport = new Rect(_scroll.HorizontalOffset - rect.X, _scroll.VerticalOffset - rect.Y, _scroll.ViewportWidth, _scroll.ViewportHeight);
        viewport.Inflate(_scroll.ViewportWidth * 0.25, _scroll.ViewportHeight * 0.25);
        viewport.Intersect(new Rect(0, 0, rect.Width, rect.Height));
        if (viewport.IsEmpty)
        {
            page.HideTile();
            return;
        }
        var visibleNow = new Rect(_scroll.HorizontalOffset - rect.X, _scroll.VerticalOffset - rect.Y, _scroll.ViewportWidth, _scroll.ViewportHeight);
        visibleNow.Intersect(new Rect(0, 0, rect.Width, rect.Height));
        var upToDate = page.TileVersion == version && Math.Abs(page.TileScale - exactScale) < exactScale * 0.004 && page.TileArea.Contains(visibleNow);
        if (upToDate) return;

        var x = (int)Math.Floor(viewport.X * _dpi);
        var y = (int)Math.Floor(viewport.Y * _dpi);
        var width = Math.Max(1, (int)Math.Ceiling(viewport.Width * _dpi));
        var height = Math.Max(1, (int)Math.Ceiling(viewport.Height * _dpi));
        var tile = await Task.Run(() => TryRender(session, () => PdfiumDocument.ToBitmap(
            session.Document.RenderRegion(index, fullWidth, fullHeight, x, y, width, height, PageRenderOptions.Default), width, height, 96 * _dpi)));
        if (tile is null || version != _version || !_pages.ContainsKey(index)) return;
        page.ShowTile(tile, new Rect(x / _dpi, y / _dpi, width / _dpi, height / _dpi));
        page.TileVersion = version;
        page.TileScale = exactScale;
    }

    private static T? TryRender<T>(PdfSession session, Func<T> render) where T : class
    {
        try
        {
            return render();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ArgumentOutOfRangeException)
        {
            return null; // The document changed underneath us; the change handler queues a fresh render.
        }
    }

    private PageGeometry? TryGeometry(int pageIndex)
    {
        if (Session is null || pageIndex < 0 || pageIndex >= Session.PageCount) return null;
        if (_geometry.TryGetValue(pageIndex, out var cached)) return cached;
        try
        {
            return _geometry[pageIndex] = Session.Document.GetGeometry(pageIndex);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ObjectDisposedException)
        {
            return null;
        }
    }

    private void InvalidateOverlay(int pageIndex)
    {
        if (_pages.TryGetValue(pageIndex, out var page)) page.Overlay.InvalidateVisual();
    }

    private void InvalidateOverlays()
    {
        foreach (var page in _pages.Values) page.Overlay.InvalidateVisual();
    }

    private void Say(string message) => Status?.Invoke(message);

    private Task<bool> Commit(string description, Func<byte[], byte[]> edit, ChangeKind kind = ChangeKind.Content) =>
        Edit is null ? Task.FromResult(false) : Edit(description, edit, kind);

    private AnnotStyle CommentStyle(uint? color = null, uint? fill = null) =>
        new(color ?? Options.Color, Options.Opacity, Options.LineWidth, fill, Options.FontSize, Services.AppSettings.Current.Author);
}
