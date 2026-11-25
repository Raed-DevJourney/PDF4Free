using System.Windows;
using System.Windows.Threading;
using PDF4Free.App.Setup;

namespace PDF4Free.App;

public partial class App : Application
{
    /// <summary>
    /// One program file plays three roles, chosen here:
    /// the uninstaller ("--uninstall"), the installer (when the file is named ...Setup.exe), or the application.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        if (HasSwitch(e.Args, "--uninstall"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(RunUninstall(HasSwitch(e.Args, "--quiet"), HasSwitch(e.Args, "--remove-settings")));
            return;
        }
        if (InstallService.IsSetupExecutable)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!RunSetup(e.Args)) return;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        StartApplication(e.Args);
    }

    private void StartApplication(string[] args)
    {
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        window.OpenStartupFiles([.. args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && !a.StartsWith('/'))]);
#if DEBUG
        if (args.Contains("--automation")) window.StartAutomation();
#endif
    }

    private static bool HasSwitch(string[] args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Shows the installer, or installs silently with "/S" (optional: "/D=folder", "/NoDesktop", "/NoOpenWith").
    /// Returns true when the user chose to run the program without installing it.
    /// </summary>
    private bool RunSetup(string[] args)
    {
        if (HasSwitch(args, "/S"))
        {
            Shutdown(InstallSilently(args));
            return false;
        }
        var setup = new SetupWindow();
        setup.ShowDialog();
        if (setup.RunWithoutInstalling) return true;
        Shutdown(0);
        return false;
    }

    private static int InstallSilently(string[] args)
    {
        var folder = args.FirstOrDefault(a => a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))?[3..].Trim('"')
                     ?? InstallService.InstalledFolder ?? InstallService.DefaultFolder;
        var options = new InstallOptions(folder, !HasSwitch(args, "/NoDesktop"), !HasSwitch(args, "/NoOpenWith"));
        try
        {
            InstallService.Register(InstallService.CopyProgram(options.Folder), options);
            return 0;
        }
        catch (Exception)
        {
            return 1; // A silent install has no window to report into; the exit code is the report.
        }
    }

    private static int RunUninstall(bool quiet, bool removeSettings)
    {
        if (InstallService.OtherInstancesRunning())
        {
            if (!quiet) MessageBox.Show("PDF4Free is still running. Close it, then uninstall again.", "PDF4Free", MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }
        if (!quiet)
        {
            if (MessageBox.Show("Remove PDF4Free from this computer?", "Uninstall PDF4Free", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return 2;
            removeSettings = MessageBox.Show("Also delete your PDF4Free settings and saved signatures?", "Uninstall PDF4Free", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
        try
        {
            InstallService.Uninstall(removeSettings);
            if (!quiet) MessageBox.Show("PDF4Free was removed.", "Uninstall PDF4Free", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show("PDF4Free could not be removed completely.\n\n" + ex.Message, "Uninstall PDF4Free", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A failed operation should never take the user's open documents down with it.
        e.Handled = true;
        MessageBox.Show(e.Exception.Message, "PDF4Free", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
