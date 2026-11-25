using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PDF4Free.Core;

namespace PDF4Free.App.Views;

/// <summary>A grid of page thumbnails with multi-selection and drag-and-drop reordering.</summary>
public sealed class PageOrganizer : Grid
{
    private const string DragFormat = "PDF4Free.Pages";

    private readonly ListBox _list;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Rectangle _marker = new() { Width = 4, Fill = Brushes.DodgerBlue, RadiusX = 2, RadiusY = 2, Visibility = Visibility.Collapsed };
    private readonly PdfSession _session;
    private Point _pressPoint;
    private PageThumb? _pressedSelected;
    private bool _dragging;

    public PageOrganizer(PdfSession session)
    {
        _session = session;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Extended,
            ItemTemplate = (DataTemplate)Application.Current.FindResource("ThumbTemplate"),
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel))),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            AllowDrop = true,
            Padding = new Thickness(12),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.PreviewMouseLeftButtonDown += OnPress;
        _list.PreviewMouseMove += OnMove;
        _list.PreviewMouseLeftButtonUp += OnRelease;
        _list.DragOver += OnDragOver;
        _list.DragLeave += (_, _) => _marker.Visibility = Visibility.Collapsed;
        _list.Drop += OnDrop;
        _list.MouseDoubleClick += (_, _) =>
        {
            if (_list.SelectedItem is PageThumb thumb) PageActivated?.Invoke(thumb.Index);
        };
        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
        _overlay.Children.Add(_marker);
        Children.Add(_list);
        Children.Add(_overlay);
        Reload();
    }

    /// <summary>The user dropped the selected pages before the page with this index (page count = at the end).</summary>
    public event Action<IReadOnlyList<int>, int>? MoveRequested;

    /// <summary>A page was double-clicked.</summary>
    public event Action<int>? PageActivated;

    public event Action? SelectionChanged;

    public IReadOnlyList<int> SelectedPages => [.. _list.SelectedItems.OfType<PageThumb>().Select(t => t.Index).OrderBy(i => i)];

    public void Reload()
    {
        var keep = SelectedPages;
        _list.ItemsSource = PageThumb.For(_session, 190, VisualTreeHelper.GetDpi(this).DpiScaleX);
        Select(keep);
    }

    public void Select(IEnumerable<int> pages)
    {
        if (_list.ItemsSource is not List<PageThumb> thumbs) return;
        _list.SelectedItems.Clear();
        foreach (var page in pages)
        {
            if (page >= 0 && page < thumbs.Count) _list.SelectedItems.Add(thumbs[page]);
        }
        if (_list.SelectedItems.Count > 0) _list.ScrollIntoView(_list.SelectedItems[0]);
    }

    private ListBoxItem? ItemAt(Point point)
    {
        var hit = _list.InputHitTest(point) as DependencyObject;
        while (hit is not null and not ListBoxItem) hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        return hit as ListBoxItem;
    }

    // ------------------------------------------------------------------ starting a drag

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(_list);
        _pressedSelected = null;
        _dragging = false;
        if (Keyboard.Modifiers != ModifierKeys.None || ItemAt(_pressPoint) is not { IsSelected: true, DataContext: PageThumb thumb }) return;
        // Pressing an already-selected page must not collapse a multi-selection: the user may be about to drag it.
        _pressedSelected = thumb;
        e.Handled = true;
        _list.Focus();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragging || _list.SelectedItems.Count == 0) return;
        var now = e.GetPosition(_list);
        if (Math.Abs(now.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (ItemAt(_pressPoint) is not { IsSelected: true }) return;
        _dragging = true;
        DragDrop.DoDragDrop(_list, new DataObject(DragFormat, true), DragDropEffects.Move);
        _dragging = false;
        _pressedSelected = null;
        _marker.Visibility = Visibility.Collapsed;
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        // A plain click (no drag) on a selected page selects just that page, as a list normally would.
        if (_pressedSelected is { } thumb && !_dragging)
        {
            _list.SelectedItems.Clear();
            _list.SelectedItems.Add(thumb);
        }
        _pressedSelected = null;
    }

    // ------------------------------------------------------------------ dropping

    /// <summary>The insertion position for a drop at <paramref name="point"/> and where to draw the marker.</summary>
    private (int InsertBefore, Rect Marker)? DropTarget(Point point)
    {
        if (_list.ItemsSource is not List<PageThumb> thumbs || thumbs.Count == 0) return null;
        var item = ItemAt(point);
        if (item is null)
        {
            if (_list.ItemContainerGenerator.ContainerFromIndex(thumbs.Count - 1) is not ListBoxItem last) return null;
            var lastBounds = new Rect(last.TranslatePoint(new Point(0, 0), this), last.RenderSize);
            return (thumbs.Count, new Rect(lastBounds.Right, lastBounds.Top, 4, lastBounds.Height));
        }
        var index = _list.ItemContainerGenerator.IndexFromContainer(item);
        var bounds = new Rect(item.TranslatePoint(new Point(0, 0), this), item.RenderSize);
        var after = point.X > item.TranslatePoint(new Point(0, 0), _list).X + item.ActualWidth / 2;
        return (after ? index + 1 : index, new Rect(after ? bounds.Right - 2 : bounds.Left - 2, bounds.Top, 4, bounds.Height));
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat) || DropTarget(e.GetPosition(_list)) is not { } target)
        {
            e.Effects = DragDropEffects.None;
            _marker.Visibility = Visibility.Collapsed;
            return;
        }
        e.Effects = DragDropEffects.Move;
        _marker.Height = target.Marker.Height;
        Canvas.SetLeft(_marker, target.Marker.X);
        Canvas.SetTop(_marker, target.Marker.Y);
        _marker.Visibility = Visibility.Visible;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _marker.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DragFormat) || DropTarget(e.GetPosition(_list)) is not { } target) return;
        e.Handled = true;
        var pages = SelectedPages;
        if (pages.Count > 0) MoveRequested?.Invoke(pages, target.InsertBefore);
    }
}
