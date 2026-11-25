using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PDF4Free.App.Views;

namespace PDF4Free.App.Dialogs;

/// <summary>One document to combine: a file on disk, or the bytes of a document that is open in the application.</summary>
public sealed record CombineEntry(string Name, string? Path, byte[]? Bytes);

/// <summary>Lets the user collect files and arrange the order in which they are merged.</summary>
public sealed class CombineDialog : Window
{
    private readonly ObservableCollection<ListRow> _rows = [];
    private readonly ListBox _list;
    private readonly CheckBox _bookmarks = new() { Content = "Add a bookmark for each file", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly string _filter;

    public CombineDialog(Window owner, IReadOnlyList<CombineEntry> openDocuments, string filter)
    {
        _filter = filter;
        Owner = owner;
        Title = "Combine files into one PDF";
        Width = 640;
        Height = 520;
        MinWidth = 520;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AllowDrop = true;

        _list = new ListBox
        {
            ItemsSource = _rows,
            ItemTemplate = (DataTemplate)Application.Current.FindResource("RowTemplate"),
            Margin = new Thickness(16, 8, 8, 8),
            SelectionMode = SelectionMode.Extended,
        };

        var side = new StackPanel { Margin = new Thickness(0, 8, 16, 8), Width = 150 };
        side.Children.Add(SideButton("Add files…", AddFiles));
        if (openDocuments.Count > 0) side.Children.Add(SideButton("Add open documents", () => { foreach (var entry in openDocuments) Add(entry); }));
        side.Children.Add(SideButton("Remove", RemoveSelected));
        side.Children.Add(SideButton("Move up", () => MoveSelected(-1)));
        side.Children.Add(SideButton("Move down", () => MoveSelected(+1)));

        var ok = new Button { Content = "Combine", IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            if (_rows.Count == 0)
            {
                MessageBox.Show(this, "Add at least one file.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 100 };
        var bottom = new DockPanel { Margin = new Thickness(16, 4, 16, 16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_bookmarks);

        var note = new TextBlock
        {
            Text = "PDFs, pictures, text files, Office documents and web pages can be mixed. Drop files here or use Add files. They are joined from top to bottom.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(16, 14, 16, 0),
        };
        var root = new DockPanel();
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(note);
        root.Children.Add(bottom);
        root.Children.Add(side);
        root.Children.Add(_list);
        Content = root;

        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            foreach (var path in paths.Where(File.Exists)) Add(new CombineEntry(System.IO.Path.GetFileName(path), path, null));
        };
    }

    public List<CombineEntry> Entries => [.. _rows.Select(r => (CombineEntry)r.Tag!)];

    public bool AddBookmarks => _bookmarks.IsChecked == true;

    private static Button SideButton(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(8, 5, 8, 5), HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => action();
        return button;
    }

    private void Add(CombineEntry entry) => _rows.Add(new ListRow(entry.Name, entry.Path ?? "Open in PDF4Free (current state, including unsaved changes)", entry));

    private void AddFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Ui.PdfFilter + "|" + _filter, Multiselect = true, Title = "Add files" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames) Add(new CombineEntry(System.IO.Path.GetFileName(path), path, null));
    }

    private void RemoveSelected()
    {
        foreach (var row in _list.SelectedItems.OfType<ListRow>().ToList()) _rows.Remove(row);
    }

    private void MoveSelected(int direction)
    {
        var selected = _list.SelectedItems.OfType<ListRow>().OrderBy(r => _rows.IndexOf(r)).ToList();
        if (selected.Count == 0) return;
        if (direction > 0) selected.Reverse();
        foreach (var row in selected)
        {
            var index = _rows.IndexOf(row);
            var target = index + direction;
            if (target < 0 || target >= _rows.Count || selected.Contains(_rows[target])) continue;
            _rows.Move(index, target);
        }
        foreach (var row in selected) _list.SelectedItems.Add(row);
    }
}
