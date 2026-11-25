using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PDF4Free.App.Setup;

public sealed record InstallOptions(string Folder, bool DesktopShortcut, bool OpenWithMenu);

/// <summary>
/// Installs and removes PDF4Free for the current Windows user. The setup program is the application itself,
/// published as one self-contained file: installing copies that file, adds shortcuts and registers an uninstaller.
/// Everything lives under the user's profile and HKEY_CURRENT_USER, so no administrator rights are needed.
/// </summary>
public static class InstallService
{
    public const string ProductName = "PDF4Free";
    private const string ExeName = "PDF4Free.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PDF4Free";
    private const string ClassesKey = @"Software\Classes";
    private const string DocumentClass = "PDF4Free.Document";

    public static string Version => typeof(InstallService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string CurrentExe => Environment.ProcessPath ?? throw new InvalidOperationException("The program's own path is not known.");

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);

    /// <summary>True when this program file is the downloadable setup (its name says so), not an installed copy.</summary>
    public static bool IsSetupExecutable =>
        Path.GetFileNameWithoutExtension(CurrentExe).Contains("setup", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where PDF4Free is installed for this user, or null.</summary>
    public static string? InstalledFolder
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") as string;
        }
    }

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName + ".lnk");

    private static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk");

    // ------------------------------------------------------------------ install

    /// <summary>Copies the program into place. Safe to run on a background thread.</summary>
    public static string CopyProgram(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) throw new InvalidOperationException("Choose a full folder path such as C:\\Programs\\PDF4Free.");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, ExeName);
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(CurrentExe), StringComparison.OrdinalIgnoreCase)) return target;
        try
        {
            File.Copy(CurrentExe, target, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("PDF4Free could not be copied to " + folder + ". If PDF4Free is running, close it and try again.\n\n" + ex.Message, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("You do not have permission to write to " + folder + ". Choose a folder inside your user profile.", ex);
        }
        return target;
    }

    /// <summary>Adds shortcuts and registry entries for a program copied with <see cref="CopyProgram"/>.</summary>
    public static void Register(string installedExe, InstallOptions options)
    {
        CreateShortcut(StartMenuShortcut, installedExe);
        if (options.DesktopShortcut) CreateShortcut(DesktopShortcut, installedExe);
        else DeleteFile(DesktopShortcut);

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", ProductName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "PDF4Free project");
            key.SetValue("DisplayIcon", installedExe);
            key.SetValue("InstallLocation", options.Folder);
            key.SetValue("UninstallString", $"\"{installedExe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{installedExe}\" --uninstall --quiet");
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("EstimatedSize", (int)(new FileInfo(installedExe).Length / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        if (options.OpenWithMenu) RegisterOpenWith(installedExe);
        else UnregisterOpenWith();
        NotifyShell();
    }

    /// <summary>Lists PDF4Free under "Open with" for PDF files. It does not change the default PDF application.</summary>
    private static void RegisterOpenWith(string installedExe)
    {
        var command = $"\"{installedExe}\" \"%1\"";
        using (var document = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{DocumentClass}"))
        {
            document.SetValue("", "PDF document");
            using var icon = document.CreateSubKey("DefaultIcon");
            icon.SetValue("", installedExe + ",0");
            using var open = document.CreateSubKey(@"shell\open\command");
            open.SetValue("", command);
        }
        using (var progIds = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\.pdf\OpenWithProgids"))
        {
            progIds.SetValue(DocumentClass, "");
        }
        using var application = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\Applications\{ExeName}");
        application.SetValue("FriendlyAppName", ProductName);
        using (var open = application.CreateSubKey(@"shell\open\command")) open.SetValue("", command);
        using (var types = application.CreateSubKey("SupportedTypes")) types.SetValue(".pdf", "");
    }

    private static void UnregisterOpenWith()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesKey}\{DocumentClass}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesKey}\Applications\{ExeName}", throwOnMissingSubKey: false);
        using var progIds = Registry.CurrentUser.OpenSubKey($@"{ClassesKey}\.pdf\OpenWithProgids", writable: true);
        progIds?.DeleteValue(DocumentClass, throwOnMissingValue: false);
    }

    private static void CreateShortcut(string linkPath, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell")
                   ?? throw new InvalidOperationException("Windows Script Host is not available, so shortcuts could not be created.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = "View, edit, convert, sign and protect PDF documents";
            link.IconLocation = target + ",0";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    // ------------------------------------------------------------------ uninstall

    /// <summary>Other running copies of the program (they would keep the program file locked).</summary>
    public static bool OtherInstancesRunning()
    {
        var self = Environment.ProcessId;
        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)).Any(p => p.Id != self);
    }

    /// <summary>
    /// Removes shortcuts and registry entries, optionally the user's settings, and arranges for the program file to be
    /// deleted once this process has ended. Call it and then exit.
    /// </summary>
    public static void Uninstall(bool removeSettings)
    {
        var folder = InstalledFolder;
        DeleteFile(StartMenuShortcut);
        DeleteFile(DesktopShortcut);
        UnregisterOpenWith();
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        NotifyShell();
        if (removeSettings && Directory.Exists(Services.AppSettings.Folder)) Directory.Delete(Services.AppSettings.Folder, recursive: true);
        if (folder is not null) DeleteProgramAfterExit(folder);
    }

    /// <summary>
    /// A running program cannot delete itself, so a short command-prompt job does it a moment later. Only the program
    /// file is deleted, and the folder only if that leaves it empty: nothing else the user may have put there is touched.
    /// </summary>
    private static void DeleteProgramAfterExit(string folder)
    {
        var exe = Path.Combine(folder, ExeName);
        if (!File.Exists(exe)) return;
        var start = new ProcessStartInfo("cmd.exe")
        {
            // The paths travel as environment variables and are expanded late ("!NAME!"), so nothing in a folder
            // name can be mistaken for part of the command.
            Arguments = "/v:on /c \"ping 127.0.0.1 -n 4 >nul & del /f /q \"!P4F_EXE!\" & rmdir \"!P4F_DIR!\" & if exist \"!P4F_CACHE!\" rmdir /s /q \"!P4F_CACHE!\"\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };
        start.Environment["P4F_EXE"] = exe;
        start.Environment["P4F_DIR"] = folder;
        // Where the single-file program unpacks its native libraries.
        start.Environment["P4F_CACHE"] = Path.Combine(Path.GetTempPath(), ".net", ProductName);
        Process.Start(start);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A shortcut that cannot be removed is left behind rather than failing the whole operation.
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Tells Explorer that file associations changed.</summary>
    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}
