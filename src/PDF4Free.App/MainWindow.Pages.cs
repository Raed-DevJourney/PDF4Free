using System.Windows;
using PDF4Free.App.Dialogs;
using PDF4Free.App.Views;
using PDF4Free.Core;
using PDF4Free.Core.Conversion;
using PDF4Free.Core.Editing;

namespace PDF4Free.App;

/// <summary>The Organize tab: page grid, rotate, delete, move, insert, extract, split and combine.</summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------ page grid

    private void ToggleOrganizer()
    {
        if (Active is not { } tab)
        {
            SetToggle(_organizeButton, false);
            return;
        }
        if (tab.Organizing)
        {
            ShowViewer();
            return;
        }
        tab.Viewer.EndEditors();
        if (tab.Organizer is null)
        {
            var organizer = new PageOrganizer(tab.Session);
            organizer.MoveRequested += (pages, before) => Fire(() => MovePagesToAsync(tab, pages, before));
            organizer.PageActivated += page =>
            {
                ShowViewer();
                tab.Viewer.GoToPage(page);
            };
            tab.Organizer = organizer;
            tab.Host.Children.Add(organizer);
        }
        tab.Organizer.Visibility = Visibility.Visible;
        tab.Organizer.Select([tab.Viewer.CurrentPage]);
        SetToggle(_organizeButton, true);
        RefreshToolUi();
        SetStatus("Page grid: Ctrl- or Shift-click to select several pages, drag to reorder, double-click to open a page.");
    }

    /// <summary>Leaves the page grid and shows the normal page view.</summary>
    private void ShowViewer()
    {
        if (Active is not { } tab) return;
        if (tab.Organizer is not null) tab.Organizer.Visibility = Visibility.Collapsed;
        SetToggle(_organizeButton, false);
        tab.Viewer.Focus();
    }

    /// <summary>The pages a command applies to: the grid selection, or else the page being viewed.</summary>
    private IReadOnlyList<int> TargetPages()
    {
        if (Active is not { } tab) return [];
        if (tab.Organizing && tab.Organizer!.SelectedPages is { Count: > 0 } selected) return selected;
        return [tab.Viewer.CurrentPage];
    }

    private void SelectInGrid(IEnumerable<int> pages)
    {
        if (Active is { Organizing: true } tab) tab.Organizer!.Select(pages);
    }

    private static string Describe(IReadOnlyList<int> pages) => pages.Count == 1 ? $"page {pages[0] + 1}" : $"{pages.Count} pages";

    // ------------------------------------------------------------------ commands on the selected pages

    private async Task RotatePagesAsync(int degrees)
    {
        var pages = TargetPages();
        if (pages.Count == 0) return;
        if (await Do("Rotate " + Describe(pages), b => PageService.Rotate(b, pages, degrees), ChangeKind.Structure)) SelectInGrid(pages);
    }

    private async Task DeletePagesAsync()
    {
        var pages = TargetPages();
        if (Session is null || pages.Count == 0) return;
        if (pages.Count >= Session.PageCount)
        {
            Ui.Info("A document must keep at least one page.");
            return;
        }
        if (!Ui.Confirm($"Delete {Describe(pages)}?")) return;
        await Do("Delete " + Describe(pages), b => PageService.Delete(b, pages), ChangeKind.Structure);
    }

    private async Task DuplicatePagesAsync()
    {
        var pages = TargetPages();
        if (pages.Count == 0) return;
        if (await Do("Duplicate " + Describe(pages), b => PageService.Duplicate(b, pages), ChangeKind.Structure))
        {
            SelectInGrid(Enumerable.Range(pages[^1] + 1, pages.Count));
        }
    }

    private void ExtractPagesCommand()
    {
        var pages = TargetPages();
        if (Session is null || pages.Count == 0) return;
        if (!(Active?.Organizing ?? false))
        {
            var dialog = new FormDialog("Extract pages", this, "Extract");
            var range = dialog.AddPageRange("Pages", Session.PageCount, (Viewer!.CurrentPage + 1).ToString());
            if (!dialog.Ask()) return;
            pages = range.Value;
        }
        var source = Session.Bytes;
        var name = Path.GetFileNameWithoutExtension(Session.DisplayName) + " - extract.pdf";
        Fire(async () =>
        {
            byte[]? extracted = null;
            if (await RunBusyAsync("Extract " + Describe(pages), async () => extracted = await Task.Run(() => PageService.Extract(source, pages))) && extracted is not null)
            {
                NewDocument(extracted, name);
            }
        });
    }

    private async Task MovePagesAsync(int direction)
    {
        var pages = TargetPages();
        if (Session is null || pages.Count == 0) return;
        var before = direction < 0 ? pages[0] - 1 : pages[^1] + 2;
        if (before < 0 || before > Session.PageCount) return;
        if (Active is { } tab) await MovePagesToAsync(tab, pages, before);
    }

    private async Task MovePagesToAsync(DocumentTab tab, IReadOnlyList<int> pages, int insertBefore)
    {
        var count = tab.Session.PageCount;
        if (!await RunEditAsync(tab, "Move " + Describe(pages), b => PageService.Move(b, pages, insertBefore, count), ChangeKind.Structure)) return;
        var start = insertBefore - pages.Count(p => p < insertBefore);
        tab.Organizer?.Select(Enumerable.Range(start, pages.Count));
        if (!tab.Organizing) tab.Viewer.GoToPage(start);
    }

    private async Task ReversePagesAsync()
    {
        if (Session is null || Session.PageCount < 2) return;
        var order = Enumerable.Range(0, Session.PageCount).Reverse().ToList();
        await Do("Reverse page order", b => PageService.Reorder(b, order), ChangeKind.Structure);
    }

    // ------------------------------------------------------------------ insert and replace

    private int? AskInsertPosition(string title)
    {
        if (Session is null || Viewer is null) return null;
        var current = TargetPages() is { Count: > 0 } pages ? pages[^1] : Viewer.CurrentPage;
        var dialog = new FormDialog(title, this, "Insert");
        var where = dialog.AddChoice("Position", [$"After page {current + 1}", $"Before page {current + 1}", "At the beginning", "At the end"], 0);
        if (!dialog.Ask()) return null;
        return where.Value switch
        {
            0 => current + 1,
            1 => current,
            2 => 0,
            _ => Session.PageCount,
        };
    }

    private async Task InsertFromFileAsync()
    {
        if (Session is null) return;
        var paths = Ui.PickOpenMany(Ui.PdfFilter + "|" + CreateFilter, "Insert pages from files");
        if (paths.Length == 0 || AskInsertPosition("Insert pages") is not { } position) return;
        var donors = new List<byte[]>();
        if (!await RunBusyAsync("Reading files", async () =>
            {
                foreach (var path in paths) donors.Add(await ToPdfAsync(path));
            })) return;
        var donor = donors.Count == 1 ? donors[0] : await Task.Run(() => PageService.Merge(donors));
        if (await Do("Insert pages", b => PageService.InsertFrom(b, position, donor), ChangeKind.Structure)) Viewer?.GoToPage(position);
    }

    private async Task InsertBlankAsync()
    {
        if (Session is null || Viewer is null || AskInsertPosition("Insert blank page") is not { } position) return;
        var (width, height) = Session.Document.GetPageSize(Viewer.CurrentPage);
        if (await Do("Insert blank page", b => PageService.InsertBlank(b, position, width, height), ChangeKind.Structure)) Viewer.GoToPage(position);
    }

    private async Task InsertClipboardAsync()
    {
        if (Session is null) return;
        if (ClipboardPicture() is not { } picture)
        {
            Ui.Info("The clipboard does not contain a picture.");
            return;
        }
        if (AskInsertPosition("Insert clipboard picture") is not { } position) return;
        var donor = CreatePdf.FromBitmap(picture);
        if (await Do("Insert picture page", b => PageService.InsertFrom(b, position, donor), ChangeKind.Structure)) Viewer?.GoToPage(position);
    }

    private async Task ReplacePageAsync()
    {
        if (Session is null || Viewer is null) return;
        var path = Ui.PickOpen(Ui.PdfFilter, "Replace with a page from");
        if (path is null) return;
        var donor = ReadPdfForReuse(path);
        int donorPages;
        using (var probe = PdfSession.Create(donor, "donor")) donorPages = probe.PageCount;
        var dialog = new FormDialog("Replace page", this, "Replace");
        dialog.AddNote($"Page {Viewer.CurrentPage + 1} of this document will be replaced with a page of \"{Path.GetFileName(path)}\" ({donorPages} pages).");
        var number = dialog.AddNumber("Use page", 1, 1, donorPages);
        if (!dialog.Ask()) return;
        var target = Viewer.CurrentPage;
        var source = (int)number.Value - 1;
        await Do("Replace page", b => PageService.Replace(b, target, donor, source), ChangeKind.Structure);
    }

    // ------------------------------------------------------------------ split and combine

    private void SplitCommand()
    {
        if (Session is null) return;
        var session = Session;
        var baseName = Path.GetFileNameWithoutExtension(session.DisplayName);
        var dialog = new FormDialog("Split document", this, "Split");
        var mode = dialog.AddChoice("Split", ["Every N pages", "One file per page", "By page ranges", "At top-level bookmarks"], 0);
        var every = dialog.AddNumber("N (pages per file)", 2, 1, Math.Max(1, session.PageCount));
        var ranges = dialog.AddText("Ranges, one file per group (for example 1-3; 4-10; 11-)", "");
        var folder = dialog.AddFolder("Save into", session.FilePath is { } p ? Path.GetDirectoryName(p) ?? "" : "");
        var parts = new List<IReadOnlyList<int>>();
        dialog.Validate(() =>
        {
            parts = SplitPlan(session, mode.Value, (int)every.Value, ranges.Value);
            return parts.Count == 0 ? "That does not describe any pages. Check the ranges, or pick a different way to split." : null;
        });
        if (!dialog.Ask()) return;
        var source = session.Bytes;
        var target = folder.Value;
        Fire(async () =>
        {
            if (!await RunBusyAsync("Split document", () => Task.Run(() =>
                {
                    Directory.CreateDirectory(target);
                    for (var i = 0; i < parts.Count; i++)
                    {
                        File.WriteAllBytes(Path.Combine(target, $"{baseName} - part {i + 1:00}.pdf"), PageService.Extract(source, parts[i]));
                    }
                }))) return;
            if (Ui.Confirm($"Created {parts.Count} files in\n{target}\n\nOpen that folder?")) Ui.ShellOpen(target);
        });
    }

    private static List<IReadOnlyList<int>> SplitPlan(PdfSession session, int mode, int every, string ranges)
    {
        var count = session.PageCount;
        var plan = new List<IReadOnlyList<int>>();
        switch (mode)
        {
            case 0:
                for (var start = 0; start < count; start += every) plan.Add([.. Enumerable.Range(start, Math.Min(every, count - start))]);
                break;
            case 1:
                for (var i = 0; i < count; i++) plan.Add([i]);
                break;
            case 2:
                foreach (var group in ranges.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (PageService.ParseRange(group, count) is not { } pages) return [];
                    plan.Add(pages);
                }
                break;
            default:
                var starts = session.Document.GetBookmarks().Select(b => b.PageIndex).Where(i => i > 0 && i < count).Distinct().OrderBy(i => i).ToList();
                starts.Insert(0, 0);
                for (var i = 0; i < starts.Count; i++)
                {
                    var end = i + 1 < starts.Count ? starts[i + 1] : count;
                    plan.Add([.. Enumerable.Range(starts[i], end - starts[i])]);
                }
                if (plan.Count < 2) return [];
                break;
        }
        return plan;
    }

    private void CombineCommand()
    {
        var open = _tabs.Select(t => new CombineEntry(t.Session.DisplayName, null, t.Session.IsRestricted ? null : t.Session.Bytes)).Where(e => e.Bytes is not null).ToList();
        var dialog = new CombineDialog(this, open, CreateFilter);
        if (dialog.ShowDialog() != true || dialog.Entries.Count == 0) return;
        var entries = dialog.Entries;
        var bookmarks = dialog.AddBookmarks;
        Fire(async () =>
        {
            byte[]? combined = null;
            if (!await RunBusyAsync("Combine files", async () =>
                {
                    var sources = new List<byte[]>();
                    foreach (var entry in entries) sources.Add(entry.Bytes ?? await ToPdfAsync(entry.Path!));
                    var titles = bookmarks ? entries.Select(e => Path.GetFileNameWithoutExtension(e.Name)).ToList() : null;
                    combined = await Task.Run(() => PageService.Merge(sources, titles));
                }) || combined is null) return;
            NewDocument(combined, "Combined.pdf");
        });
    }
}
