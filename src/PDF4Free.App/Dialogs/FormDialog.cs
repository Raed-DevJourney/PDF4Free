using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace PDF4Free.App.Dialogs;

/// <summary>A value the user enters in a <see cref="FormDialog"/>.</summary>
public sealed class Field<T>(Func<T> read)
{
    public T Value => read();
}

/// <summary>
/// A dialog assembled from labelled rows (text, numbers, choices, colours, files). Most of the application's
/// option dialogs are built with it, which keeps them consistent and short.
/// </summary>
public sealed class FormDialog : Window
{
    public static readonly (string Name, uint Rgb)[] Palette =
    [
        ("Black", 0x000000), ("Dark grey", 0x555555), ("Red", 0xD32F2F), ("Orange", 0xEF6C00), ("Yellow", 0xFFD400),
        ("Green", 0x2E7D32), ("Teal", 0x00897B), ("Blue", 0x1565C0), ("Purple", 0x6A1B9A), ("Pink", 0xD81B60), ("White", 0xFFFFFF),
    ];

    private readonly Grid _rows = new() { Margin = new Thickness(20, 16, 20, 8) };
    private readonly List<Func<string?>> _validators = [];
    private readonly Button _ok;

    public FormDialog(string title, Window? owner, string okText = "OK")
    {
        Title = title;
        Owner = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        MinWidth = 420;
        MaxWidth = 760;
        _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _ok = new Button { Content = okText, IsDefault = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
        _ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 8, 20, 18) };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_rows);
        Content = root;
    }

    private void Accept()
    {
        foreach (var validator in _validators)
        {
            if (validator() is not { } problem) continue;
            MessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    /// <summary>Adds a check that runs when OK is pressed; return a message to keep the dialog open.</summary>
    public void Validate(Func<string?> validator) => _validators.Add(validator);

    private void AddRow(string label, FrameworkElement editor)
    {
        var row = _rows.RowDefinitions.Count;
        _rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 16, 6) };
        Grid.SetRow(caption, row);
        _rows.Children.Add(caption);
        editor.Margin = new Thickness(0, 5, 0, 5);
        editor.MinWidth = 240;
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        _rows.Children.Add(editor);
    }

    public void AddNote(string text)
    {
        var row = _rows.RowDefinitions.Count;
        _rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 8), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetRow(note, row);
        Grid.SetColumnSpan(note, 2);
        _rows.Children.Add(note);
    }

    public Field<string> AddText(string label, string value = "", bool multiline = false)
    {
        var box = new TextBox { Text = value };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = 90;
            box.MaxHeight = 220;
            box.Width = 380;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        AddRow(label, box);
        if (_rows.RowDefinitions.Count == 1) Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return new Field<string>(() => box.Text);
    }

    public Field<string> AddPassword(string label)
    {
        var box = new PasswordBox();
        AddRow(label, box);
        if (_rows.RowDefinitions.Count == 1) Loaded += (_, _) => box.Focus();
        return new Field<string>(() => box.Password);
    }

    public Field<double> AddNumber(string label, double value, double minimum, double maximum)
    {
        var box = new TextBox { Text = value.ToString("0.##", CultureInfo.CurrentCulture), Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        AddRow(label, box);
        double Read() => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) ? Math.Clamp(number, minimum, maximum) : value;
        Validate(() => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) && number >= minimum && number <= maximum
            ? null
            : $"{label}: enter a number between {minimum} and {maximum}.");
        return new Field<double>(Read);
    }

    public Field<double> AddSlider(string label, double value, double minimum, double maximum)
    {
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = value, Width = 200 };
        var readout = new TextBlock { Width = 44, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        void Show() => readout.Text = maximum <= 1 ? $"{slider.Value * 100:0}%" : $"{slider.Value:0.#}";
        slider.ValueChanged += (_, _) => Show();
        Show();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(readout);
        AddRow(label, panel);
        return new Field<double>(() => slider.Value);
    }

    public Field<bool> AddCheck(string label, bool value)
    {
        var box = new CheckBox { Content = label, IsChecked = value };
        AddRow("", box);
        return new Field<bool>(() => box.IsChecked == true);
    }

    public Field<int> AddChoice(string label, IReadOnlyList<string> options, int selected = 0)
    {
        var combo = new ComboBox { ItemsSource = options, SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, options.Count - 1)) };
        AddRow(label, combo);
        return new Field<int>(() => Math.Max(0, combo.SelectedIndex));
    }

    public Field<uint> AddColor(string label, uint value)
    {
        var combo = new ComboBox();
        var entries = Palette.ToList();
        if (entries.All(e => e.Rgb != value)) entries.Insert(0, ($"#{value:X6}", value));
        foreach (var (name, rgb) in entries)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Tag = rgb };
            item.Children.Add(new Border
            {
                Width = 18, Height = 14, Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray,
                Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)),
            });
            item.Children.Add(new TextBlock { Text = name });
            combo.Items.Add(item);
        }
        combo.SelectedIndex = entries.FindIndex(e => e.Rgb == value);
        AddRow(label, combo);
        return new Field<uint>(() => combo.SelectedItem is StackPanel { Tag: uint rgb } ? rgb : value);
    }

    /// <summary>A font family picker limited to fonts that are installed.</summary>
    public Field<string> AddFont(string label, string value)
    {
        var names = Fonts.SystemFontFamilies.Select(f => f.Source).Where(n => !n.Contains('#')).OrderBy(n => n).ToList();
        var combo = new ComboBox { ItemsSource = names, SelectedItem = names.Contains(value) ? value : "Arial", IsTextSearchEnabled = true };
        AddRow(label, combo);
        return new Field<string>(() => combo.SelectedItem as string ?? "Arial");
    }

    /// <summary>A page-range box ("1-3, 7"); empty means every page. The value is a list of zero-based indexes.</summary>
    public Field<List<int>> AddPageRange(string label, int pageCount, string initial = "")
    {
        var box = new TextBox { Text = initial, Width = 160, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Examples: 1-3, 5, 8-   (leave empty for all pages)" };
        var hint = new TextBlock { Text = $"of {pageCount} (empty = all)", Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(hint);
        AddRow(label, panel);
        List<int>? Parse() => string.IsNullOrWhiteSpace(box.Text)
            ? Enumerable.Range(0, pageCount).ToList()
            : PDF4Free.Core.Editing.PageService.ParseRange(box.Text, pageCount);
        Validate(() => Parse() is null ? $"{label}: \"{box.Text}\" is not a valid page range for a {pageCount}-page document." : null);
        return new Field<List<int>>(() => Parse() ?? []);
    }

    public Field<string> AddFile(string label, string filter, string value = "", bool save = false, string defaultName = "")
    {
        var box = new TextBox { Text = value, Width = 300 };
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            FileDialog dialog = save ? new SaveFileDialog { Filter = filter, FileName = defaultName } : new OpenFileDialog { Filter = filter };
            if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(browse);
        AddRow(label, panel);
        return new Field<string>(() => box.Text.Trim());
    }

    public Field<string> AddFolder(string label, string value = "")
    {
        var box = new TextBox { Text = value, Width = 300 };
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(box.Text) ? box.Text : "" };
            if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(browse);
        AddRow(label, panel);
        Validate(() => string.IsNullOrWhiteSpace(box.Text) ? $"{label}: choose a folder." : null);
        return new Field<string>(() => box.Text.Trim());
    }

    public bool Ask() => ShowDialog() == true;
}
