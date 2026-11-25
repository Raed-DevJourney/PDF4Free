using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PDF4Free.App.Dialogs;
using PDF4Free.App.Services;
using PDF4Free.App.Views;
using PDF4Free.Core;
using PDF4Free.Core.Conversion;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.App;

/// <summary>The Protect, Sign and Tools tabs: encryption, redaction, sanitising, signatures, compare and batch.</summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------ security

    private async Task EncryptAsync()
    {
        if (Session is null || Active is not { } tab) return;
        var dialog = new FormDialog("Protect with a password", this, "Save protected copy…");
        dialog.AddNote("Open password: needed to view the document. Permissions password: needed to change these settings or lift the restrictions below. Set either or both.");
        var open = dialog.AddPassword("Open password (optional)");
        var openAgain = dialog.AddPassword("Repeat open password");
        var owner = dialog.AddPassword("Permissions password");
        var ownerAgain = dialog.AddPassword("Repeat permissions password");
        var print = dialog.AddCheck("Allow printing", true);
        var copy = dialog.AddCheck("Allow copying text and pictures", true);
        var modify = dialog.AddCheck("Allow changing the document", false);
        var annotate = dialog.AddCheck("Allow commenting", true);
        var forms = dialog.AddCheck("Allow filling in form fields", true);
        var strength = dialog.AddChoice("Encryption", ["AES 256-bit (Acrobat X and later)", "AES 128-bit (older readers)"], 0);
        dialog.Validate(() => open.Value != openAgain.Value ? "The two open passwords are not the same." : null);
        dialog.Validate(() => owner.Value != ownerAgain.Value ? "The two permissions passwords are not the same." : null);
        dialog.Validate(() => open.Value.Length == 0 && owner.Value.Length == 0 ? "Enter at least one password." : null);
        dialog.Validate(() => owner.Value.Length == 0 && !(print.Value && copy.Value && modify.Value && annotate.Value && forms.Value)
            ? "Restrictions only take effect with a permissions password. Enter one, or allow everything." : null);
        dialog.Validate(() => owner.Value.Length > 0 && owner.Value == open.Value ? "Use a different permissions password: anyone who can open the file would otherwise be able to remove the restrictions." : null);
        if (!dialog.Ask()) return;

        var suggested = Path.GetFileNameWithoutExtension(tab.Session.DisplayName) + " - protected.pdf";
        var path = Ui.PickSave(Ui.PdfFilter, suggested, "Save protected copy");
        if (path is null) return;
        var options = new EncryptionOptions(open.Value, owner.Value, print.Value, copy.Value, modify.Value, annotate.Value, forms.Value, strength.Value == 0);
        tab.Viewer.EndEditors();
        if (!await RunBusyAsync("Encrypt", () => Task.Run(() => tab.Session.SaveTransformed(path, b => DocumentService.Encrypt(b, options), encrypted: true)))) return;
        AppSettings.Current.AddRecent(path);
        RefreshRecentFiles();
        RefreshInfoBar();
        Ui.Info($"Saved the protected file:\n{path}\n\nKeep the password safe: without it the file cannot be opened or unlocked.\n\nYou are still working on the unlocked copy in this window.");
    }

    private void UnlockCommand()
    {
        if (Session is null) return;
        if (!Session.IsRestricted)
        {
            Ui.Info(Session.SecurityRemoved ? "This document is already unlocked for editing." : "This document is not protected.");
            return;
        }
        while (true)
        {
            var password = Ui.PromptPassword("Unlock document", "Enter the permissions password to allow editing.");
            if (password is null) return;
            if (Session.TryUnlock(password)) break;
            Ui.Warn("That is not the permissions password of this document.");
        }
        RefreshCommandStates();
        RefreshInfoBar();
        SetStatus("The document is unlocked for editing.");
    }

    // ------------------------------------------------------------------ redaction

    private async Task FindAndRedactAsync()
    {
        if (Session is null || Active is not { } tab) return;
        var session = tab.Session;
        var kinds = new List<string> { "The text I type below" };
        kinds.AddRange(TextFinder.Patterns.Keys);
        kinds.Add("A regular expression I type below");
        var dialog = new FormDialog("Find text to redact", this, "Mark matches");
        var kind = dialog.AddChoice("Look for", kinds, 0);
        var query = dialog.AddText("Text or expression", Viewer?.SelectedText is { Length: > 0 and < 80 } s ? s.Trim() : "");
        var matchCase = dialog.AddCheck("Match case", false);
        var wholeWord = dialog.AddCheck("Whole words only", false);
        var pages = dialog.AddPageRange("Pages", session.PageCount);
        dialog.Validate(() => (kind.Value == 0 || kind.Value == kinds.Count - 1) && query.Value.Trim().Length == 0 ? "Type what to look for." : null);
        if (!dialog.Ask()) return;
        var isPattern = kind.Value > 0 && kind.Value < kinds.Count - 1;
        var pattern = isPattern ? TextFinder.Patterns[kinds[kind.Value]] : query.Value;
        var options = new SearchOptions(matchCase.Value, wholeWord.Value, Regex: kind.Value != 0);
        var range = pages.Value;
        List<SearchHit> hits = [];
        if (!await RunBusyAsync("Search", () => Task.Run(() => hits = [.. TextFinder.Search(session.Document, pattern, options, range)]))) return;
        if (hits.Count == 0)
        {
            Ui.Info("Nothing was found.\n\nScanned pages contain no text until you run Convert > Recognize Text.");
            return;
        }
        var marked = 0;
        var author = AppSettings.Current.Author;
        if (await RunEditAsync(tab, "Mark for redaction", b => RedactionService.MarkHits(b, hits, author, out marked), ChangeKind.Content))
        {
            Ui.Info($"Marked {hits.Count} match(es) on {hits.Select(h => h.PageIndex).Distinct().Count()} page(s).\n\nReview the red marks, remove any you do not want (click a mark, then Delete), then choose Protect > Apply.");
        }
    }

    private async Task ApplyRedactionsAsync()
    {
        if (Session is null || Active is not { } tab) return;
        var bytes = tab.Session.Bytes;
        Dictionary<int, List<PdfRect>> marks = [];
        if (!await RunBusyAsync("Check redaction marks", () => Task.Run(() => marks = RedactionService.ReadMarks(bytes)))) return;
        if (marks.Count == 0)
        {
            Ui.Info("There are no redaction marks yet.\n\nUse Mark Area, Mark Text or Find & Mark first.");
            return;
        }
        var dialog = new FormDialog("Apply redactions", this, "Redact permanently");
        dialog.AddNote($"{marks.Values.Sum(v => v.Count)} marked area(s) on {marks.Count} page(s) will be blacked out.\n\n" +
                       "To guarantee that nothing can be recovered, each affected page is converted to a picture: its text, links, comments and form fields are replaced by the image with the marked areas removed.");
        var ocr = dialog.AddCheck("Recognize the text again so the pages stay searchable", OcrService.Languages().Count > 0);
        var sanitize = dialog.AddCheck("Also remove metadata, attachments and scripts from the document", true);
        if (!dialog.Ask()) return;
        var again = ocr.Value;
        var clean = sanitize.Value;
        var language = AppSettings.Current.OcrLanguage;
        var done = await RunEditAsync(tab, "Apply redactions", b =>
        {
            var redacted = RedactionService.Apply(b, out var affected);
            if (again && affected.Count > 0) redacted = OcrService.AddTextLayerAsync(redacted, affected, language, force: true).GetAwaiter().GetResult().Result;
            return clean ? DocumentService.Sanitize(redacted, new SanitizeOptions(Comments: false, Thumbnails: true)) : redacted;
        }, ChangeKind.Structure);
        if (done) Ui.Info("The redactions were applied.\n\nSave the document under a new name so the original, unredacted file is not overwritten by accident. Undo history still holds the unredacted version until you close this document.");
    }

    private async Task ClearRedactionsAsync()
    {
        if (Session is null) return;
        await Do("Clear redaction marks", b => AnnotationService.DeleteAll(b, subtype => subtype == "Redact"));
    }

    private async Task SanitizeAsync()
    {
        if (Session is null) return;
        var dialog = new FormDialog("Remove hidden information", this, "Remove");
        dialog.AddNote("Removes information that is not visible on the pages but travels with the file.");
        var metadata = dialog.AddCheck("Document properties and metadata (title, author, editing software…)", true);
        var scripts = dialog.AddCheck("JavaScript and automatic actions", true);
        var attachments = dialog.AddCheck("Attached files", true);
        var comments = dialog.AddCheck("Comments and markup", true);
        var links = dialog.AddCheck("Links", false);
        var bookmarks = dialog.AddCheck("Bookmarks", false);
        var thumbnails = dialog.AddCheck("Embedded page thumbnails", true);
        var flatten = dialog.AddCheck("Flatten form fields (values stay visible, fields disappear)", false);
        if (!dialog.Ask()) return;
        var options = new SanitizeOptions(metadata.Value, scripts.Value, attachments.Value, comments.Value, links.Value, bookmarks.Value, thumbnails.Value, flatten.Value);
        await Do("Remove hidden information", b => DocumentService.Sanitize(b, options));
    }

    // ------------------------------------------------------------------ fill & sign

    private void AddSignatureCommand()
    {
        if (Viewer is null) return;
        var dialog = new SignatureDialog(this);
        if (dialog.ShowDialog() != true || dialog.Result is not { } png) return;
        ArmPicture(png, asStamp: false, "Click where the signature should go, or drag to set its size.");
    }

    private void AddDateCommand()
    {
        if (Viewer is null) return;
        string[] formats = ["d MMMM yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "MMMM d, yyyy"];
        var now = DateTime.Now;
        var dialog = new FormDialog("Add date", this);
        var format = dialog.AddChoice("Format", [.. formats.Select(f => now.ToString(f))], 0);
        if (!dialog.Ask()) return;
        ShowViewer();
        Viewer.Options.PresetText = now.ToString(formats[format.Value]);
        Viewer.Tool = ViewerTool.AddText;
        SetStatus("Click where the date should go; then click elsewhere to finish.");
    }

    // ------------------------------------------------------------------ certificates

    private void StartCertificateSignCommand()
    {
        if (Viewer is null || Session is null) return;
        var dialog = new FormDialog("Sign with a certificate", this, "Continue");
        dialog.AddNote("A certificate signature proves who signed the document and that it has not been changed since. Sign last: any later edit invalidates the signature.");
        var visible = dialog.AddChoice("Appearance", ["Visible: I will drag the area for the signature", "Invisible: no mark on the page"], 0);
        if (!dialog.Ask()) return;
        if (visible.Value == 1)
        {
            Fire(() => SignWithCertificateAsync(Viewer.CurrentPage, null));
            return;
        }
        ShowViewer();
        Viewer.Tool = ViewerTool.CertificateArea;
        RefreshToolUi();
    }

    private async Task SignWithCertificateAsync(int page, PdfRect? area)
    {
        if (Session is null || Active is not { } tab) return;
        if (Viewer is not null) Viewer.Tool = ViewerTool.Select;
        tab.Viewer.EndEditors();
        var store = SignatureService.StoreCertificates();
        var sources = store.Select(c => $"{SignatureService.SubjectName(c)}  (valid to {c.NotAfter:yyyy-MM-dd})").ToList();
        sources.Add("A digital ID file (.pfx / .p12)…");
        var dialog = new FormDialog("Sign with a certificate", this, "Sign and save…");
        var source = dialog.AddChoice("Digital ID", sources, 0);
        var file = dialog.AddFile("ID file", "Digital ID (*.pfx;*.p12)|*.pfx;*.p12");
        var password = dialog.AddPassword("ID file password");
        var reason = dialog.AddText("Reason (optional)", "");
        var location = dialog.AddText("Location (optional)", "");
        dialog.AddNote("No digital ID yet? Use Sign > Create Digital ID to make a self-signed one.");
        dialog.Validate(() => source.Value == sources.Count - 1 && !File.Exists(file.Value) ? "Choose the digital ID file." : null);
        if (!dialog.Ask()) return;

        X509Certificate2 certificate;
        try
        {
            certificate = source.Value < store.Count ? store[source.Value] : SignatureService.LoadPfx(file.Value, password.Value);
        }
        catch (CryptographicException)
        {
            Ui.Warn("The digital ID file could not be opened. Check the password.");
            return;
        }
        var path = Ui.PickSave(Ui.PdfFilter, Path.GetFileNameWithoutExtension(tab.Session.DisplayName) + " - signed.pdf", "Save signed document");
        if (path is null) return;
        var bytes = tab.Session.Bytes;
        var request = new SignRequest(certificate, page, area, reason.Value.Trim(), location.Value.Trim());
        if (!await RunBusyAsync("Sign document", () => Task.Run(() => File.WriteAllBytes(path, SignatureService.Sign(bytes, request))))) return;
        OpenPath(path);
        ShowNavigation(_signaturesTab);
        Ui.Info($"The signed document was saved and opened:\n{path}\n\nDo not edit it further: any change would invalidate the signature.");
    }

    private void CreateDigitalIdCommand()
    {
        var dialog = new FormDialog("Create a self-signed digital ID", this, "Create");
        dialog.AddNote("A self-signed ID lets you sign documents. Recipients can check that the document is unchanged, but must decide for themselves whether to trust your ID, because no certificate authority vouches for it.");
        var name = dialog.AddText("Name", AppSettings.Current.Author);
        var email = dialog.AddText("E-mail (optional)", "");
        var organisation = dialog.AddText("Organisation (optional)", "");
        var years = dialog.AddNumber("Valid for (years)", 5, 1, 20);
        var password = dialog.AddPassword("Password to protect the ID file");
        var again = dialog.AddPassword("Repeat password");
        dialog.Validate(() => string.IsNullOrWhiteSpace(name.Value) ? "Enter your name." : null);
        dialog.Validate(() => password.Value.Length < 6 ? "Choose a password of at least 6 characters." : null);
        dialog.Validate(() => password.Value != again.Value ? "The two passwords are not the same." : null);
        if (!dialog.Ask()) return;
        var path = Ui.PickSave("Digital ID (*.pfx)|*.pfx", name.Value.Trim() + ".pfx", "Save digital ID");
        if (path is null) return;
        File.WriteAllBytes(path, SignatureService.CreateSelfSignedPfx(name.Value.Trim(), email.Value.Trim(), organisation.Value.Trim(), password.Value, (int)years.Value));
        Ui.Info($"Your digital ID was saved to:\n{path}\n\nKeep this file and its password private: anyone who has both can sign as you.");
    }

    // ------------------------------------------------------------------ compare

    private async Task CompareAsync()
    {
        var dialog = new FormDialog("Compare two PDF files", this, "Compare");
        var older = dialog.AddFile("Old version", Ui.PdfFilter);
        var newer = dialog.AddFile("New version", Ui.PdfFilter, Session?.FilePath ?? "");
        dialog.AddNote("The text of the two files is compared word by word. Words that were removed are marked in the old version, words that were added in the new one.");
        dialog.Validate(() => !File.Exists(older.Value) || !File.Exists(newer.Value) ? "Choose both files." : null);
        if (!dialog.Ask()) return;
        PdfiumDocument? oldDoc = null, newDoc = null;
        CompareResult? result = null;
        string oldPath = older.Value, newPath = newer.Value;
        var ok = await RunBusyAsync("Compare files", async () =>
        {
            var oldBytes = ReadPdfForReuse(oldPath);
            var newBytes = ReadPdfForReuse(newPath);
            await Task.Run(() =>
            {
                oldDoc = PdfiumDocument.Load(oldBytes);
                newDoc = PdfiumDocument.Load(newBytes);
                result = CompareService.Compare(oldDoc, newDoc);
            });
        });
        if (!ok || result is null || oldDoc is null || newDoc is null)
        {
            oldDoc?.Dispose();
            newDoc?.Dispose();
            return;
        }
        new CompareWindow(this, oldDoc, newDoc, result, Path.GetFileName(oldPath), Path.GetFileName(newPath)).Show();
    }

    // ------------------------------------------------------------------ batch processing

    private static readonly string[] BatchActions =
    [
        "Compress", "Add a text watermark", "Add page numbers", "Protect with an open password", "Recognize text (OCR)",
        "Flatten comments and form fields", "Remove hidden information", "Rotate every page 90° clockwise",
        "Export pages as PNG images", "Export text (.txt)", "Export to Word (.docx)",
    ];

    private async Task BatchAsync()
    {
        var files = Ui.PickOpenMany(Ui.PdfFilter, "Choose the PDF files to process");
        if (files.Length == 0) return;
        var dialog = new FormDialog($"Batch process {files.Length} file(s)", this, "Run");
        var action = dialog.AddChoice("Action", BatchActions, 0);
        var text = dialog.AddText("Watermark text (for the watermark action)", "CONFIDENTIAL");
        var password = dialog.AddPassword("Password (for the protect action)");
        var folder = dialog.AddFolder("Save results into", Path.Combine(Path.GetDirectoryName(files[0]) ?? "", "Processed"));
        dialog.Validate(() => action.Value == 3 && password.Value.Length == 0 ? "Enter the password to protect the files with." : null);
        dialog.Validate(() => action.Value == 1 && text.Value.Trim().Length == 0 ? "Enter the watermark text." : null);
        if (!dialog.Ask()) return;

        var chosen = action.Value;
        var watermark = text.Value;
        var secret = password.Value;
        var target = folder.Value;
        var language = AppSettings.Current.OcrLanguage;
        var failures = new StringBuilder();
        var succeeded = 0;
        Directory.CreateDirectory(target);
        await RunBusyAsync("Batch process", async () =>
        {
            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                SetStatus($"Batch: {i + 1} of {files.Length} — {Path.GetFileName(file)}");
                try
                {
                    var source = ReadPdfForReuse(file);
                    await Task.Run(() => RunBatchAction(chosen, source, file, target, watermark, secret, language));
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    failures.AppendLine($"{Path.GetFileName(file)}: skipped (no password given)");
                }
                catch (Exception ex)
                {
                    failures.AppendLine($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        });
        var summary = $"{succeeded} of {files.Length} file(s) processed.\nResults are in:\n{target}";
        if (failures.Length > 0) summary += "\n\nProblems:\n" + failures;
        if (Ui.Confirm(summary + "\n\nOpen the results folder?", "Batch process")) Ui.ShellOpen(target);
    }

    private static void RunBatchAction(int action, byte[] source, string file, string folder, string watermark, string password, string language)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var output = Path.Combine(folder, name + ".pdf");
        switch (action)
        {
            case 0:
                File.WriteAllBytes(output, OptimizeService.Optimize(source, OptimizeOptions.Balanced, out _));
                break;
            case 1:
                File.WriteAllBytes(output, ContentService.AddWatermark(source, new WatermarkOptions { Text = watermark }));
                break;
            case 2:
                File.WriteAllBytes(output, ContentService.AddHeaderFooter(source, new HeaderFooterOptions { FooterCenter = "Page {page} of {pages}" }));
                break;
            case 3:
                File.WriteAllBytes(output, DocumentService.Encrypt(source, new EncryptionOptions(password, "")));
                break;
            case 4:
                using (var probe = PdfiumDocument.Load(source))
                {
                    var pages = Enumerable.Range(0, probe.PageCount).ToList();
                    File.WriteAllBytes(output, OcrService.AddTextLayerAsync(source, pages, language).GetAwaiter().GetResult().Result);
                }
                break;
            case 5:
                File.WriteAllBytes(output, PdfiumOps.Flatten(source));
                break;
            case 6:
                File.WriteAllBytes(output, DocumentService.Sanitize(source, new SanitizeOptions()));
                break;
            case 7:
                using (var probe = PdfiumDocument.Load(source))
                {
                    File.WriteAllBytes(output, PageService.Rotate(source, Enumerable.Range(0, probe.PageCount), 90));
                }
                break;
            default:
                using (var document = PdfiumDocument.Load(source))
                {
                    var pages = Enumerable.Range(0, document.PageCount).ToList();
                    if (action == 8) ExportService.ToImages(document, pages, Path.Combine(folder, name), name, ImageExportFormat.Png, 150);
                    else if (action == 9) File.WriteAllText(Path.Combine(folder, name + ".txt"), ExportService.ToText(document, pages), Encoding.UTF8);
                    else ExportService.ToWord(document, pages, Path.Combine(folder, name + ".docx"), WordExportMode.EditableText);
                }
                break;
        }
    }
}
