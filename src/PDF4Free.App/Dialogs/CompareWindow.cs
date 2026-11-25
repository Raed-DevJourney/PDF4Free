using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF4Free.App.Views;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.App.Dialogs;

/// <summary>Shows the differences between two documents: a list of changes and the two pages side by side with the changes marked.</summary>
public sealed class CompareWindow : Window
{
    private sealed record Change(bool Added, DiffSpan Span);

    private const double Dpi = 150;

    private readonly PdfiumDocument _old;
    private readonly PdfiumDocument _new;
    private readonly CompareResult _result;
    private readonly ListBox _list;
    private readonly Image _oldImage = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top };
    private readonly Image _newImage = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _oldCaption = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 6, 8, 4) };
    private readonly TextBlock _newCaption = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 6, 8, 4) };
    private readonly string _oldName;
    private readonly string _newName;

    public CompareWindow(Window owner, PdfiumDocument oldDoc, PdfiumDocument newDoc, CompareResult result, string oldName, string newName)
    {
        _old = oldDoc;
        _new = newDoc;
        _result = result;
        _oldName = oldName;
        _newName = newName;
        Owner = owner;
        Title = $"Compare — {oldName}  ↔  {newName}";
        Width = 1400;
        Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var changes = result.Removed.Select(s => new Change(false, s)).Concat(result.Added.Select(s => new Change(true, s)))
            .OrderBy(c => c.Span.PageIndex).ThenByDescending(c => c.Span.Rects.Count > 0 ? c.Span.Rects[0].Top : 0).ToList();
        _list = new ListBox
        {
            ItemTemplate = (DataTemplate)Application.Current.FindResource("RowTemplate"),
            ItemsSource = changes.Select(c => new ListRow($"{(c.Added ? "＋ Added" : "－ Removed")}  ·  page {c.Span.PageIndex + 1}", c.Span.Text, c)).ToList(),
            BorderThickness = new Thickness(0),
        };
        _list.SelectionChanged += (_, _) => ShowSelected();

        var summary = new TextBlock
        {
            Margin = new Thickness(12, 10, 12, 8), TextWrapping = TextWrapping.Wrap,
            Text = result.Identical
                ? "The text of the two documents is identical."
                : $"{result.Removed.Count} removal(s) and {result.Added.Count} addition(s).\nOld: {result.OldWordCount} words on {oldDoc.PageCount} page(s).  New: {result.NewWordCount} words on {newDoc.PageCount} page(s).",
        };
        var left = new DockPanel();
        DockPanel.SetDock(summary, Dock.Top);
        left.Children.Add(summary);
        left.Children.Add(_list);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(left);
        AddPane(grid, 1, _oldCaption, _oldImage);
        AddPane(grid, 2, _newCaption, _newImage);
        Content = grid;

        Closed += (_, _) =>
        {
            _old.Dispose();
            _new.Dispose();
        };
        if (changes.Count > 0) _list.SelectedIndex = 0;
        else ShowPages(0, 0, null);
    }

    private static void AddPane(Grid grid, int column, TextBlock caption, Image image)
    {
        // No sideways scrolling: the page picture shrinks to the width of its pane.
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = image, Padding = new Thickness(8) };
        var pane = new DockPanel();
        DockPanel.SetDock(caption, Dock.Top);
        pane.Children.Add(caption);
        pane.Children.Add(scroll);
        var border = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = pane };
        border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private void ShowSelected()
    {
        if (_list.SelectedItem is not ListRow { Tag: Change change }) return;
        var page = change.Span.PageIndex;
        ShowPages(Math.Min(page, _old.PageCount - 1), Math.Min(page, _new.PageCount - 1), change);
    }

    private void ShowPages(int oldPage, int newPage, Change? current)
    {
        _oldCaption.Text = $"Old: {_oldName} — page {oldPage + 1}";
        _newCaption.Text = $"New: {_newName} — page {newPage + 1}";
        _oldImage.Source = Paint(_old, oldPage, _result.Removed, Color.FromRgb(229, 57, 53), current is { Added: false } ? current.Span : null);
        _newImage.Source = Paint(_new, newPage, _result.Added, Color.FromRgb(46, 160, 67), current is { Added: true } ? current.Span : null);
    }

    /// <summary>Renders a page and tints the changed words; the selected change gets an outline.</summary>
    private static ImageSource Paint(PdfiumDocument doc, int page, IReadOnlyList<DiffSpan> spans, Color color, DiffSpan? current)
    {
        var bitmap = doc.RenderBitmap(page, Dpi);
        var geometry = doc.GetGeometry(page);
        var scale = Dpi / 72.0;
        var fill = new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B));
        var outline = new Pen(new SolidColorBrush(color), 2);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
            foreach (var span in spans.Where(s => s.PageIndex == page))
            {
                foreach (var rect in span.Rects)
                {
                    var view = geometry.ToView(rect);
                    var box = new Rect(view.X * scale, view.Y * scale, view.Width * scale, view.Height * scale);
                    context.DrawRectangle(fill, ReferenceEquals(span, current) ? outline : null, box);
                }
            }
        }
        var target = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
