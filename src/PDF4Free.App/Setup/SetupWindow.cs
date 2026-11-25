using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PDF4Free.App.Setup;

/// <summary>The installer's single page: where to install, two options, Install.</summary>
public sealed class SetupWindow : Window
{
    private readonly TextBox _folder = new() { MinWidth = 330 };
    private readonly CheckBox _desktop = new() { Content = "Create a desktop shortcut", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox _openWith = new() { Content = "Add PDF4Free to the \"Open with\" menu for PDF files", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MinHeight = 36 };
    private readonly Button _install = new() { Content = "Install", IsDefault = true, MinWidth = 110, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _portable = new() { Content = "Run without installing", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _close = new() { Content = "Cancel", IsCancel = true, MinWidth = 90, Padding = new Thickness(14, 6, 14, 6) };
    private string? _installedExe;

    public SetupWindow()
    {
        Title = "PDF4Free Setup";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/PDF4Free.ico"));

        var existing = InstallService.InstalledFolder;
        _folder.Text = existing ?? InstallService.DefaultFolder;

        var logo = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/PDF4Free.png")), Width = 72, Height = 72, Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = $"Install PDF4Free {InstallService.Version}", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = existing is null
                ? "A free PDF viewer and editor. It is installed for your Windows account only, so no administrator rights are needed."
                : "PDF4Free is already installed. Installing again replaces it with this version; your settings are kept.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.78, Margin = new Thickness(0, 6, 0, 0),
        });
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        DockPanel.SetDock(logo, Dock.Left);
        top.Children.Add(logo);
        top.Children.Add(heading);

        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Install PDF4Free in" };
            if (dialog.ShowDialog(this) == true) _folder.Text = System.IO.Path.Combine(dialog.FolderName, InstallService.ProductName);
        };
        var folderRow = new DockPanel { Margin = new Thickness(0, 4, 0, 10) };
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folder);

        _install.Click += async (_, _) => await InstallAsync();
        _portable.Click += (_, _) =>
        {
            RunWithoutInstalling = true;
            Close();
        };
        _close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_portable);
        buttons.Children.Add(_install);
        buttons.Children.Add(_close);

        var body = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };
        body.Children.Add(top);
        body.Children.Add(new TextBlock { Text = "Install in" });
        body.Children.Add(folderRow);
        body.Children.Add(_desktop);
        body.Children.Add(_openWith);
        body.Children.Add(_status);
        body.Children.Add(buttons);
        Content = body;
    }

    /// <summary>The user chose to use the program straight from the setup file.</summary>
    public bool RunWithoutInstalling { get; private set; }

    private async Task InstallAsync()
    {
        if (_installedExe is not null)
        {
            // After a successful install this button reads "Launch PDF4Free".
            Process.Start(new ProcessStartInfo(_installedExe) { UseShellExecute = true });
            Close();
            return;
        }
        var options = new InstallOptions(_folder.Text.Trim(), _desktop.IsChecked == true, _openWith.IsChecked == true);
        SetBusy(true, "Installing…");
        try
        {
            var exe = await Task.Run(() => InstallService.CopyProgram(options.Folder));
            InstallService.Register(exe, options);
            _installedExe = exe;
            _status.Text = "PDF4Free is installed. You can start it from the Start menu" + (options.DesktopShortcut ? " or the desktop shortcut." : ".")
                           + " To remove it later, use Windows Settings > Apps > Installed apps.";
            _install.Content = "Launch PDF4Free";
            _close.Content = "Close";
            _portable.Visibility = Visibility.Collapsed;
            _folder.IsEnabled = _desktop.IsEnabled = _openWith.IsEnabled = false;
            SetBusy(false, _status.Text);
        }
        catch (Exception ex)
        {
            SetBusy(false, "Installation did not complete: " + ex.Message);
        }
    }

    private void SetBusy(bool busy, string message)
    {
        _status.Text = message;
        _install.IsEnabled = !busy;
        _portable.IsEnabled = !busy;
        _close.IsEnabled = !busy;
    }
}
