using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF4Free.App.Services;

namespace PDF4Free.App.Dialogs;

/// <summary>Creates a handwritten-style signature: drawn with the mouse or pen, typed in a script font, or taken from a picture.</summary>
public sealed class SignatureDialog : Window
{
    private static readonly string[] ScriptFonts = ["Segoe Script", "Ink Free", "Lucida Handwriting", "Brush Script MT", "Mistral", "Gabriola", "Segoe Print"];

    private readonly TabControl _tabs = new() { Margin = new Thickness(16, 14, 16, 8) };
    private readonly InkCanvas _ink = new() { Background = Brushes.White, Height = 200 };
    private readonly TextBox _typed = new() { FontSize = 16, Margin = new Thickness(0, 0, 0, 8) };
    private readonly ComboBox _font = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _preview = new() { FontSize = 44, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black };
    private readonly Image _picture = new() { Stretch = Stretch.Uniform, Height = 200 };
    private readonly CheckBox _transparent = new() { Content = "Make the white paper transparent", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ListBox _saved = new() { Height = 236 };
    private readonly CheckBox _remember = new() { Content = "Save this signature for next time", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _color = new() { Width = 110, ItemsSource = new[] { "Black", "Blue" }, SelectedIndex = 0 };
    private byte[]? _pictureBytes;

    public SignatureDialog(Window owner)
    {
        Owner = owner;
        Title = "Add signature";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _tabs.Items.Add(new TabItem { Header = "Draw", Content = BuildDrawTab() });
        _tabs.Items.Add(new TabItem { Header = "Type", Content = BuildTypeTab() });
        _tabs.Items.Add(new TabItem { Header = "Picture", Content = BuildPictureTab() });
        var savedFiles = AppSettings.SavedSignatures();
        if (savedFiles.Count > 0)
        {
            _tabs.Items.Add(new TabItem { Header = "Saved", Content = BuildSavedTab(savedFiles) });
            _tabs.SelectedIndex = 3;
        }

        var ok = new Button { Content = "Place signature", IsDefault = true, MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var bottom = new DockPanel { Margin = new Thickness(16, 4, 16, 16) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_remember);
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_tabs);
        Content = root;
    }

    /// <summary>The signature as a PNG with a transparent background.</summary>
    public byte[]? Result { get; private set; }

    private Color InkColor => _color.SelectedIndex == 1 ? Color.FromRgb(0x10, 0x3C, 0xA8) : Colors.Black;

    private UIElement BuildDrawTab()
    {
        _ink.DefaultDrawingAttributes = new DrawingAttributes { Color = Colors.Black, Width = 2.6, Height = 2.6, FitToCurve = true, StylusTip = StylusTip.Ellipse };
        _color.SelectionChanged += (_, _) =>
        {
            _ink.DefaultDrawingAttributes.Color = InkColor;
            foreach (var stroke in _ink.Strokes) stroke.DrawingAttributes.Color = InkColor;
            _preview.Foreground = new SolidColorBrush(InkColor);
        };
        var clear = new Button { Content = "Clear", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 12, 0) };
        clear.Click += (_, _) => _ink.Strokes.Clear();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        tools.Children.Add(clear);
        tools.Children.Add(new TextBlock { Text = "Ink colour", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        tools.Children.Add(_color);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "Sign in the box with the mouse, a pen or your finger.", Opacity = 0.75, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _ink });
        panel.Children.Add(tools);
        return panel;
    }

    private UIElement BuildTypeTab()
    {
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fonts = ScriptFonts.Where(installed.Contains).ToList();
        if (fonts.Count == 0) fonts.Add("Segoe UI");
        _font.ItemsSource = fonts;
        _font.SelectedIndex = 0;
        _typed.Text = AppSettings.Current.Author;
        void Update()
        {
            _preview.Text = _typed.Text;
            _preview.FontFamily = new FontFamily(_font.SelectedItem as string ?? "Segoe UI");
        }
        _typed.TextChanged += (_, _) => Update();
        _font.SelectionChanged += (_, _) => Update();
        Update();
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "Your name", Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(_typed);
        panel.Children.Add(new TextBlock { Text = "Style", Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(_font);
        panel.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = Brushes.White, Height = 110, Child = _preview });
        return panel;
    }

    private UIElement BuildPictureTab()
    {
        var browse = new Button { Content = "Choose a picture of your signature…", Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Ui.ImageFilter };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                _pictureBytes = File.ReadAllBytes(dialog.FileName);
                _picture.Source = Decode(_pictureBytes);
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
            {
                _pictureBytes = null;
                MessageBox.Show(this, "That file could not be read as a picture.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(browse);
        panel.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = Brushes.White, Child = _picture });
        panel.Children.Add(_transparent);
        return panel;
    }

    private UIElement BuildSavedTab(List<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                var image = new Image { Source = Decode(File.ReadAllBytes(file)), Height = 64, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4) };
                _saved.Items.Add(new ListBoxItem { Content = image, Tag = file, Background = Brushes.White });
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
            {
                // A damaged saved signature is simply not offered.
            }
        }
        _saved.SelectedIndex = 0;
        var delete = new Button { Content = "Delete selected", Padding = new Thickness(12, 3, 12, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        delete.Click += (_, _) =>
        {
            if (_saved.SelectedItem is not ListBoxItem { Tag: string path } item) return;
            File.Delete(path);
            _saved.Items.Remove(item);
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(_saved);
        panel.Children.Add(delete);
        return panel;
    }

    private static BitmapSource Decode(byte[] data)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    private void Accept()
    {
        var saved = _tabs.SelectedIndex == 3;
        var png = _tabs.SelectedIndex switch
        {
            0 => FromInk(),
            1 => FromTyped(),
            2 => FromPicture(),
            _ => _saved.SelectedItem is ListBoxItem { Tag: string path } ? File.ReadAllBytes(path) : null,
        };
        if (png is null)
        {
            MessageBox.Show(this, "Draw, type or choose a signature first.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!saved && _remember.IsChecked == true) AppSettings.SaveSignature(png);
        Result = png;
        DialogResult = true;
    }

    private byte[]? FromInk()
    {
        if (_ink.Strokes.Count == 0) return null;
        var bounds = _ink.Strokes.GetBounds();
        const double pad = 6;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(pad - bounds.X, pad - bounds.Y));
            _ink.Strokes.Draw(context);
        }
        return Render(visual, bounds.Width + 2 * pad, bounds.Height + 2 * pad);
    }

    private byte[]? FromTyped()
    {
        if (string.IsNullOrWhiteSpace(_typed.Text)) return null;
        var text = new FormattedText(_typed.Text.Trim(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(_font.SelectedItem as string ?? "Segoe UI"), 64, new SolidColorBrush(InkColor), 1.0);
        const double pad = 10;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawText(text, new Point(pad, pad));
        }
        return Render(visual, text.WidthIncludingTrailingWhitespace + 2 * pad, text.Height + 2 * pad);
    }

    private static byte[] Render(Visual visual, double width, double height)
    {
        const double scale = 3;
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return ToPng(bitmap);
    }

    private byte[]? FromPicture()
    {
        if (_pictureBytes is null) return null;
        var source = new FormatConvertedBitmap(Decode(_pictureBytes), PixelFormats.Bgra32, null, 0);
        if (_transparent.IsChecked != true) return ToPng(source);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Paper-coloured pixels fade out; the darker the ink, the more opaque it stays.
            var lightest = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
            if (lightest >= 235) pixels[i + 3] = 0;
            else if (lightest > 160) pixels[i + 3] = (byte)(pixels[i + 3] * (235 - lightest) / 75);
        }
        return ToPng(BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride));
    }

    private static byte[] ToPng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
