using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PDF4Free.App.Services;

namespace PDF4Free.App.Dialogs;

/// <summary>Shows a web page (or local HTML file) in the Edge WebView2 engine and prints it to PDF.</summary>
public sealed class WebCaptureWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly CheckBox _landscape = new() { Content = "Landscape", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
    private readonly CheckBox _backgrounds = new() { Content = "Print backgrounds", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
    private readonly Button _convert = new() { Content = "Convert to PDF", IsDefault = true, IsEnabled = false, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0) };
    private readonly string _address;
    private readonly bool _automatic;
    private CoreWebView2Environment? _environment;

    private WebCaptureWindow(string address, bool automatic)
    {
        _address = address;
        _automatic = automatic;
        Title = "Create PDF from web page";
        Width = 1100;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 5, 16, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_landscape);
        buttons.Children.Add(_backgrounds);
        buttons.Children.Add(_convert);
        buttons.Children.Add(cancel);
        var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(_status);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_web);
        Content = root;

        _convert.Click += async (_, _) => await ConvertAsync();
        Loaded += async (_, _) => await StartAsync();
    }

    public byte[]? Result { get; private set; }

    /// <summary>Returns the PDF, or null when the user cancelled. With <paramref name="automatic"/> the page is converted as soon as it has loaded.</summary>
    public static Task<byte[]?> CaptureAsync(Window owner, string address, bool automatic)
    {
        var window = new WebCaptureWindow(address, automatic) { Owner = owner };
        window.ShowDialog();
        return Task.FromResult(window.Result);
    }

    private async Task StartAsync()
    {
        _status.Text = "Loading " + _address + " …";
        try
        {
            _environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppSettings.Folder, "WebView2"));
            await _web.EnsureCoreWebView2Async(_environment);
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                _convert.IsEnabled = e.IsSuccess;
                _status.Text = e.IsSuccess
                    ? "Loaded. Scroll through the page if parts of it load late, then convert."
                    : $"The page could not be loaded. Check the address and your internet connection, then try again. ({e.WebErrorStatus})";
                if (e.IsSuccess && _automatic) await ConvertAsync();
            };
            _web.CoreWebView2.Navigate(_address);
        }
        catch (Exception ex)
        {
            _status.Text = "The browser engine (Microsoft Edge WebView2) could not start: " + ex.Message;
        }
    }

    private async Task ConvertAsync()
    {
        if (_environment is null || _web.CoreWebView2 is null) return;
        _convert.IsEnabled = false;
        _status.Text = "Converting…";
        var temp = Path.Combine(Path.GetTempPath(), $"pdf4free-web-{Guid.NewGuid():N}.pdf");
        try
        {
            var settings = _environment.CreatePrintSettings();
            settings.Orientation = _landscape.IsChecked == true ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
            settings.ShouldPrintBackgrounds = _backgrounds.IsChecked == true;
            if (!await _web.CoreWebView2.PrintToPdfAsync(temp, settings)) throw new InvalidOperationException("The page could not be printed to PDF.");
            Result = await File.ReadAllBytesAsync(temp);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _status.Text = "Conversion failed: " + ex.Message;
            _convert.IsEnabled = true;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
