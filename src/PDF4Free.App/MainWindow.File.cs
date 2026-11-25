using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF4Free.App.Dialogs;
using PDF4Free.App.Services;
using PDF4Free.Core;
using PDF4Free.Core.Conversion;
using PDF4Free.Core.Editing;

namespace PDF4Free.App;

/// <summary>Opening, saving, printing and creating documents; properties, preferences and read-aloud.</summary>
public partial class MainWindow
{
    private static string CreateFilter =>
        "Supported files|" + string.Join(';', CreatePdf.AllExtensions.Select(e => "*" + e)) + "|All files|*.*";

    // ------------------------------------------------------------------ open

    public void OpenStartupFiles(string[] paths)
    {
        foreach (var path in paths.Where(File.Exists)) OpenPath(path);
    }

    private void OpenCommand()
    {
        foreach (var path in Ui.PickOpenMany(Ui.PdfFilter + "|" + CreateFilter, "Open")) OpenPath(path);
    }

    private void OpenPath(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            Fire(() => CreateFromPathsAsync([path]));
            return;
        }
        var full = Path.GetFullPath(path);
        if (_tabs.FirstOrDefault(t => string.Equals(t.Session.FilePath, full, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            DocTabs.SelectedItem = open.Item;
            return;
        }
        try
        {
            AddTab(PdfSession.Open(full, wrong => AskPassword(full, wrong)));
            AppSettings.Current.AddRecent(full);
            RefreshRecentFiles();
            SetStatus($"Opened {Path.GetFileName(full)}.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Opening was cancelled.");
        }
        catch (Exception ex)
        {
            Ui.Warn($"\"{Path.GetFileName(path)}\" could not be opened.\n\n{ex.Message}");
        }
    }

    private static string? AskPassword(string path, bool wrong) =>
        Ui.PromptPassword("Password required", wrong
            ? "That password is not correct. Please try again."
            : $"\"{Path.GetFileName(path)}\" is protected. Enter its password to open it.");

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths.Where(File.Exists)) OpenPath(path);
    }

    private void RefreshRecentFiles()
    {
        var recent = AppSettings.Current.RecentFiles.Where(File.Exists).Take(8).ToList();
        RecentCaption.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Items.Clear();
        foreach (var path in recent)
        {
            var link = new Button
            {
                Content = new TextBlock { Text = path, TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, ToolTip = path,
            };
            link.Click += (_, _) => OpenPath(path);
            RecentList.Items.Add(link);
        }
    }

    /// <summary>Reads a PDF so its pages can be reused elsewhere, asking for a password when one is needed.</summary>
    private static byte[] ReadPdfForReuse(string path)
    {
        using var session = PdfSession.Open(path, wrong => AskPassword(path, wrong));
        if (session.IsRestricted)
        {
            throw new InvalidOperationException($"\"{Path.GetFileName(path)}\" is protected against changes. Its permissions password is needed to reuse its pages.");
        }
        return session.Bytes;
    }

    /// <summary>Turns any supported file into PDF bytes.</summary>
    private async Task<byte[]> ToPdfAsync(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)) return ReadPdfForReuse(path);
        if (CreatePdf.IsWebFile(path))
        {
            return await WebCaptureWindow.CaptureAsync(this, new Uri(Path.GetFullPath(path)).AbsoluteUri, automatic: true)
                   ?? throw new OperationCanceledException();
        }
        return await Task.Run(() => CreatePdf.FromFile(path));
    }

    // ------------------------------------------------------------------ save

    private Task SaveCommandAsync(bool saveAs) => Active is { } tab ? SaveAsync(tab, saveAs) : Task.CompletedTask;

    private async Task<bool> SaveAsync(DocumentTab tab, bool saveAs)
    {
        tab.Viewer.EndEditors();
        // Let any edit that is still being applied finish first.
        await _editGate.WaitAsync();
        _editGate.Release();
        var path = tab.Session.FilePath;
        if (saveAs || path is null)
        {
            path = Ui.PickSave(Ui.PdfFilter, Path.ChangeExtension(tab.Session.DisplayName, ".pdf"));
            if (path is null) return false;
        }
        else if (tab.Session.SecurityRemoved && !Ui.Confirm(
                     "This file was password-protected when you opened it.\n\nSaving now replaces it with a copy that has NO password. " +
                     "(Use Protect > Encrypt first if it should stay protected.)\n\nSave without a password?"))
        {
            return false;
        }
        try
        {
            tab.Session.Save(path);
            AppSettings.Current.AddRecent(path);
            RefreshRecentFiles();
            SetStatus($"Saved {Path.GetFileName(path)}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Warn($"The file could not be saved.\n\n{ex.Message}\n\nIf it is open in another program, close it there or use Save As.");
            return false;
        }
    }

    private void UndoCommand()
    {
        if (Session?.CanUndo != true) return;
        Viewer?.EndEditors();
        var what = Session.UndoDescription;
        Session.Undo();
        SetStatus($"Undid: {what}.");
    }

    private void RedoCommand()
    {
        if (Session?.CanRedo != true) return;
        var what = Session.RedoDescription;
        Session.Redo();
        SetStatus($"Redid: {what}.");
    }

    // ------------------------------------------------------------------ create

    private void NewDocument(byte[] pdf, string name)
    {
        AddTab(PdfSession.Create(pdf, name));
        SetStatus($"Created {name}. It has not been saved yet.");
    }

    private void CreateFromFilesCommand()
    {
        var paths = Ui.PickOpenMany(CreateFilter, "Create PDF from files");
        if (paths.Length > 0) Fire(() => CreateFromPathsAsync(paths));
    }

    private async Task CreateFromPathsAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            byte[]? pdf = null;
            await RunBusyAsync($"Converting {Path.GetFileName(path)}", async () => pdf = await ToPdfAsync(path));
            if (pdf is not null) NewDocument(pdf, Path.GetFileNameWithoutExtension(path) + ".pdf");
        }
    }

    private void CreateBlankCommand()
    {
        (string Name, double Width, double Height)[] sizes =
            [("Letter (8.5 × 11 in)", 612, 792), ("A4 (210 × 297 mm)", 595.28, 841.89), ("Legal (8.5 × 14 in)", 612, 1008), ("A3 (297 × 420 mm)", 841.89, 1190.55), ("A5 (148 × 210 mm)", 419.53, 595.28)];
        var dialog = new FormDialog("New blank document", this, "Create");
        var size = dialog.AddChoice("Page size", [.. sizes.Select(s => s.Name)], 0);
        var orientation = dialog.AddChoice("Orientation", ["Portrait", "Landscape"], 0);
        var pages = dialog.AddNumber("Pages", 1, 1, 500);
        if (!dialog.Ask()) return;
        var (_, width, height) = sizes[size.Value];
        if (orientation.Value == 1) (width, height) = (height, width);
        NewDocument(CreatePdf.Blank(width, height, (int)pages.Value), "Untitled.pdf");
    }

    private static BitmapSource? ClipboardPicture()
    {
        if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image) return null;
        // Clipboard bitmaps often carry a meaningless (fully transparent) alpha channel: drop it.
        var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        opaque.Freeze();
        return opaque;
    }

    private void CreateFromClipboardCommand()
    {
        if (ClipboardPicture() is not { } picture)
        {
            Ui.Info("The clipboard does not contain a picture.\n\nCopy an image or take a screenshot (Win+Shift+S) first.");
            return;
        }
        NewDocument(CreatePdf.FromBitmap(picture), "Clipboard.pdf");
    }

    private void CreateFromScannerCommand()
    {
        try
        {
            var pages = ScanService.Scan(() => Ui.Confirm("Scan another page into the same document?", "Scan"));
            if (pages.Count == 0) return;
            NewDocument(CreatePdf.FromImages(pages), $"Scan {DateTime.Now:yyyy-MM-dd HHmm}.pdf");
            SetStatus($"Scanned {pages.Count} page(s). Use Convert > Recognize Text to make them searchable.");
        }
        catch (Exception ex)
        {
            Ui.Warn("Scanning did not work.\n\n" + ex.Message);
        }
    }

    private async Task CreateFromWebAsync()
    {
        var address = Ui.Prompt("Create PDF from a web page", "Web address", "https://");
        if (string.IsNullOrWhiteSpace(address)) return;
        if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Ui.Warn("Enter a web address such as https://example.com.");
            return;
        }
        var pdf = await WebCaptureWindow.CaptureAsync(this, uri.AbsoluteUri, automatic: false);
        if (pdf is not null) NewDocument(pdf, (uri.Host.Length > 0 ? uri.Host : "Web page") + ".pdf");
    }

    // ------------------------------------------------------------------ print

    private void PrintCommand()
    {
        if (Session is null || Viewer is null) return;
        if (!Session.Document.Security.CanPrint)
        {
            Ui.Warn("The author of this document has not allowed printing.");
            return;
        }
        try
        {
            if (PrintService.Print(this, Session, Viewer.CurrentPage)) SetStatus("Sent to the printer.");
        }
        catch (Exception ex)
        {
            Ui.Warn("Printing failed.\n\n" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ properties, preferences, about

    private void PropertiesCommand()
    {
        if (Session is null) return;
        var session = Session;
        var info = DocumentService.ReadInfo(session.Document);
        var security = session.Document.Security;
        var (width, height) = session.Document.GetPageSize(Viewer?.CurrentPage ?? 0);
        var facts = new StringBuilder();
        facts.AppendLine($"File: {session.FilePath ?? "(not saved yet)"}");
        facts.AppendLine($"Size: {FormatSize(session.Bytes.Length)}   ·   Pages: {session.PageCount}   ·   PDF version: {session.Document.FileVersion / 10.0:0.0}");
        facts.AppendLine($"Current page size: {width / 72:0.##} × {height / 72:0.##} in  ({width / 72 * 25.4:0} × {height / 72 * 25.4:0} mm)");
        facts.AppendLine($"Created: {info.Created}   ·   Modified: {info.Modified}");
        facts.AppendLine($"Application: {info.Creator}   ·   PDF producer: {info.Producer}");
        facts.AppendLine(security.IsEncrypted
            ? $"Security: encrypted. Printing {Allowed(security.CanPrint)}, copying {Allowed(security.CanCopy)}, changing {Allowed(security.CanModify)}, commenting {Allowed(security.CanAnnotate)}."
            : session.SecurityRemoved ? "Security: the original file is password-protected; this working copy is unlocked." : "Security: none.");
        if (!session.IsRestricted)
        {
            try
            {
                var fonts = DocumentService.ListFonts(session.Bytes);
                facts.AppendLine("Fonts: " + (fonts.Count == 0 ? "none" : string.Join(", ", fonts.Take(14).Select(f => f.Name + (f.Embedded ? "" : " (not embedded)"))) + (fonts.Count > 14 ? $" … and {fonts.Count - 14} more" : "")));
            }
            catch (Exception)
            {
                facts.AppendLine("Fonts: could not be listed.");
            }
        }

        var dialog = new FormDialog("Document properties", this, session.IsRestricted ? "Close" : "Save");
        dialog.AddNote(facts.ToString().TrimEnd());
        var title = dialog.AddText("Title", info.Title);
        var author = dialog.AddText("Author", info.Author);
        var subject = dialog.AddText("Subject", info.Subject);
        var keywords = dialog.AddText("Keywords", info.Keywords);
        if (!dialog.Ask() || session.IsRestricted) return;
        var updated = info with { Title = title.Value, Author = author.Value, Subject = subject.Value, Keywords = keywords.Value };
        if (updated != info) Fire(() => Do("Edit properties", b => DocumentService.WriteInfo(b, updated)));
    }

    private static string Allowed(bool allowed) => allowed ? "allowed" : "not allowed";

    private void PreferencesCommand()
    {
        var settings = AppSettings.Current;
        var languages = OcrService.Languages();
        var names = new List<string> { "Automatic (Windows display language)" };
        names.AddRange(languages.Select(l => $"{l.DisplayName} ({l.Tag})"));
        var dialog = new FormDialog("Preferences", this);
        var author = dialog.AddText("Your name (for comments and stamps)", settings.Author);
        var language = dialog.AddChoice("Text recognition language", names, Math.Max(0, languages.ToList().FindIndex(l => l.Tag == settings.OcrLanguage) + 1));
        dialog.AddNote("Recognition languages are installed in Windows: Settings > Time & language > Language & region > (language) > Language options.");
        if (!dialog.Ask()) return;
        settings.Author = string.IsNullOrWhiteSpace(author.Value) ? Environment.UserName : author.Value.Trim();
        settings.OcrLanguage = language.Value == 0 ? "" : languages[language.Value - 1].Tag;
        settings.Save();
    }

    private void AboutCommand() =>
        Ui.Info("PDF4Free 1.0\n\nA free PDF viewer and editor for Windows.\n\n" +
                "Built on open-source components:\n" +
                " •  PDFium (Google / Foxit) — rendering and content editing — BSD-3-Clause / Apache-2.0\n" +
                " •  PDFsharp and MigraDoc 6.2 (empira) — document editing — MIT\n" +
                " •  Windows OCR, WebView2 and Windows speech — part of Windows\n\n" +
                "PDF4Free is not affiliated with or endorsed by Adobe. Adobe and Acrobat are trademarks of Adobe Inc.", "About PDF4Free");

    // ------------------------------------------------------------------ read aloud

    private System.Speech.Synthesis.SpeechSynthesizer? _speech;
    private bool _reading;

    private void ToggleReadAloud()
    {
        if (_reading)
        {
            StopReadingAloud();
            return;
        }
        if (Session is null || Viewer is null) return;
        _speech ??= CreateSpeech();
        _reading = true;
        ReadPage(Viewer.CurrentPage);
        SetToggle(_readAloudButton, true);
    }

    private System.Speech.Synthesis.SpeechSynthesizer CreateSpeech()
    {
        var speech = new System.Speech.Synthesis.SpeechSynthesizer();
        speech.SpeakCompleted += (_, e) => Dispatcher.Invoke(() =>
        {
            if (!_reading || e.Cancelled || Viewer is null || Session is null) return;
            var next = Viewer.CurrentPage + 1;
            if (next >= Session.PageCount)
            {
                StopReadingAloud();
                return;
            }
            Viewer.GoToPage(next);
            ReadPage(next);
        });
        return speech;
    }

    private void ReadPage(int page)
    {
        var text = Session!.Document.GetText(page).Text;
        SetStatus(string.IsNullOrWhiteSpace(text) ? $"Page {page + 1} has no readable text." : $"Reading page {page + 1} aloud…");
        _speech!.SpeakAsync(string.IsNullOrWhiteSpace(text) ? " " : text);
    }

    private void StopReadingAloud()
    {
        if (!_reading) return;
        _reading = false;
        _speech?.SpeakAsyncCancelAll();
        SetToggle(_readAloudButton, false);
        SetStatus("Stopped reading.");
    }
}
