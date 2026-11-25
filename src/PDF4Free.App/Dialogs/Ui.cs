using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace PDF4Free.App.Dialogs;

/// <summary>Short-hands for the message boxes, prompts and file pickers used across the application.</summary>
public static class Ui
{
    public const string PdfFilter = "PDF documents (*.pdf)|*.pdf";
    public const string ImageFilter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*";

    private static Window? Owner => Application.Current?.MainWindow;

    public static void Info(string message, string title = "PDF4Free") =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Warn(string message, string title = "PDF4Free") =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static bool Confirm(string message, string title = "PDF4Free") =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    /// <summary>Yes / No / Cancel; null means cancel.</summary>
    public static bool? YesNoCancel(string message, string title = "PDF4Free") =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public static string? Prompt(string title, string label, string value = "", bool multiline = false)
    {
        var dialog = new FormDialog(title, Owner);
        var field = dialog.AddText(label, value, multiline);
        return dialog.Ask() ? field.Value : null;
    }

    public static string? PromptPassword(string title, string note)
    {
        var dialog = new FormDialog(title, Owner);
        dialog.AddNote(note);
        var field = dialog.AddPassword("Password");
        return dialog.Ask() ? field.Value : null;
    }

    public static string? PickOpen(string filter, string title = "Open")
    {
        var dialog = new OpenFileDialog { Filter = filter, Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public static string[] PickOpenMany(string filter, string title = "Open")
    {
        var dialog = new OpenFileDialog { Filter = filter, Title = title, Multiselect = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public static string? PickSave(string filter, string defaultName, string title = "Save as")
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = defaultName, Title = title, OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public static string? PickFolder(string title = "Choose a folder")
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    /// <summary>Opens a file or folder with its default application.</summary>
    public static void ShellOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Warn("Windows could not open " + path + ":\n" + ex.Message);
        }
    }
}
