using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PDF4Free.App.Dialogs;
using PDF4Free.App.Views;
using PDF4Free.Core;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.App;

/// <summary>The navigation panels on the left: thumbnails, bookmarks, comments, search, attachments, signatures, form fields.</summary>
public partial class MainWindow
{
    private TabItem _thumbsTab = null!;
    private TabItem _bookmarksTab = null!;
    private TabItem _commentsTab = null!;
    private TabItem _searchTab = null!;
    private TabItem _attachmentsTab = null!;
    private TabItem _signaturesTab = null!;
    private TabItem _fieldsTab = null!;

    private ListBox _thumbList = null!;
    private TreeView _bookmarkTree = null!;
    private ListBox _commentList = null!;
    private TextBox _searchBox = null!;
    private CheckBox _searchCase = null!;
    private CheckBox _searchWord = null!;
    private CheckBox _searchRegex = null!;
    private TextBlock _searchSummary = null!;
    private ListBox _searchList = null!;
    private ListBox _attachmentList = null!;
    private ListBox _signatureList = null!;
    private ListBox _fieldList = null!;

    private IReadOnlyList<SearchHit> _searchHits = [];
    private int _searchIndex = -1;
    private CancellationTokenSource? _searchCancel;
    private bool _syncingThumbs;
    private List<BookmarkNode> _bookmarks = [];

    // ------------------------------------------------------------------ construction

    private TabItem NavTab(string icon, string tip, UIElement content)
    {
        var item = new TabItem
        {
            Header = new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, ToolTip = tip },
            Content = content,
            ToolTip = tip,
        };
        // The header is only an icon glyph: give the tab a spoken name.
        System.Windows.Automation.AutomationProperties.SetName(item, tip);
        NavTabs.Items.Add(item);
        return item;
    }

    private static ListBox RowList()
    {
        var list = new ListBox
        {
            ItemTemplate = (DataTemplate)Application.Current.FindResource("RowTemplate"),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        // No sideways scrolling: the detail line wraps to the panel width instead.
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        return list;
    }

    private static Button PanelButton(string text, Action action, string tip)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 3, 10, 3), ToolTip = tip };
        // Several panels have an "Add" or "Delete" button: the description tells assistive tools (and tests) which is which.
        System.Windows.Automation.AutomationProperties.SetHelpText(button, tip);
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static DockPanel WithToolbar(UIElement content, params UIElement[] buttons)
    {
        var bar = new WrapPanel { Margin = new Thickness(8, 8, 8, 6) };
        foreach (var button in buttons) bar.Children.Add(button);
        var panel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        panel.Children.Add(content);
        return panel;
    }

    private void BuildNavigationPanels()
    {
        _thumbList = new ListBox { ItemTemplate = (DataTemplate)FindResource("ThumbTemplate"), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
        ScrollViewer.SetCanContentScroll(_thumbList, true);
        _thumbList.SelectionChanged += (_, _) =>
        {
            if (!_syncingThumbs && _thumbList.SelectedItem is PageThumb thumb) Viewer?.GoToPage(thumb.Index);
        };
        _thumbsTab = NavTab(Icons.Page, "Page thumbnails", _thumbList);

        _bookmarkTree = new TreeView { ItemTemplate = (HierarchicalDataTemplate)FindResource("BookmarkTemplate"), BorderThickness = new Thickness(0) };
        _bookmarkTree.SelectedItemChanged += (_, e) =>
        {
            if (e.NewValue is BookmarkNode { PageIndex: >= 0 } node) Viewer?.GoToPage(node.PageIndex, node.Top);
        };
        _bookmarksTab = NavTab(Icons.Bookmark, "Bookmarks", WithToolbar(_bookmarkTree,
            PanelButton("Add", () => Fire(AddBookmarkAsync), "Bookmark the current page (under the selected bookmark, if any)"),
            PanelButton("Rename", () => Fire(RenameBookmarkAsync), "Rename the selected bookmark"),
            PanelButton("Delete", () => Fire(DeleteBookmarkAsync), "Delete the selected bookmark")));

        _commentList = RowList();
        _commentList.SelectionChanged += (_, _) =>
        {
            if (_commentList.SelectedItem is ListRow { Tag: AnnotationInfo a }) Viewer?.SelectAnnotation(a.PageIndex, a.Index);
        };
        _commentList.MouseDoubleClick += (_, _) => Fire(() => Viewer?.EditSelectedAnnotationAsync() ?? Task.CompletedTask);
        _commentsTab = NavTab(Icons.Comment, "Comments", WithToolbar(_commentList,
            PanelButton("Edit", () => Fire(() => Viewer?.EditSelectedAnnotationAsync() ?? Task.CompletedTask), "Edit the selected comment's text"),
            PanelButton("Delete", () => Fire(() => Viewer?.DeleteSelectionAsync() ?? Task.CompletedTask), "Delete the selected comment")));

        _searchTab = NavTab(Icons.Search, "Find (Ctrl+F)", BuildSearchPanel());

        _attachmentList = RowList();
        _attachmentsTab = NavTab(Icons.Attach, "Attachments", WithToolbar(_attachmentList,
            PanelButton("Add", () => Fire(AttachFileAsync), "Embed a file"),
            PanelButton("Save", SaveAttachmentCommand, "Save the selected attachment to disk"),
            PanelButton("Delete", () => Fire(DeleteAttachmentAsync), "Remove the selected attachment")));

        _signatureList = RowList();
        _signaturesTab = NavTab(Icons.Certificate, "Digital signatures", WithToolbar(_signatureList,
            PanelButton("Validate", () => Fire(ValidateSignaturesAsync), "Check every digital signature in this document")));

        _fieldList = RowList();
        _fieldList.SelectionChanged += (_, _) =>
        {
            if (_fieldList.SelectedItem is ListRow { Tag: FormFieldInfo f }) Viewer?.BringIntoView(f.PageIndex, f.Rect);
        };
        _fieldsTab = NavTab(Icons.Forms, "Form fields", WithToolbar(_fieldList,
            PanelButton("Delete", () => Fire(DeleteSelectedFieldAsync), "Delete the selected form field")));

        NavTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, NavTabs) && ReferenceEquals(NavTabs.SelectedItem, _signaturesTab)) Fire(ValidateSignaturesAsync);
        };
    }

    private UIElement BuildSearchPanel()
    {
        _searchBox = new TextBox { Margin = new Thickness(8, 8, 8, 4) };
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Fire(RunSearchAsync);
        };
        _searchCase = new CheckBox { Content = "Match case", Margin = new Thickness(8, 2, 8, 0) };
        _searchWord = new CheckBox { Content = "Whole words", Margin = new Thickness(8, 2, 8, 0) };
        _searchRegex = new CheckBox { Content = "Regular expression", Margin = new Thickness(8, 2, 8, 0) };
        _searchSummary = new TextBlock { Margin = new Thickness(10, 6, 8, 4), Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        _searchList = RowList();
        _searchList.SelectionChanged += (_, _) =>
        {
            if (_searchList.SelectedIndex < 0 || _searchList.SelectedIndex == _searchIndex) return;
            _searchIndex = _searchList.SelectedIndex;
            Viewer?.SetSearchHits(_searchHits, _searchIndex);
        };
        var buttons = new WrapPanel { Margin = new Thickness(8, 6, 8, 0) };
        buttons.Children.Add(PanelButton("Find", () => Fire(RunSearchAsync), "Search the whole document (Enter)"));
        buttons.Children.Add(PanelButton("Previous", () => StepSearch(-1), "Previous match (Shift+F3)"));
        buttons.Children.Add(PanelButton("Next", () => StepSearch(+1), "Next match (F3)"));
        var top = new StackPanel();
        top.Children.Add(_searchBox);
        top.Children.Add(_searchCase);
        top.Children.Add(_searchWord);
        top.Children.Add(_searchRegex);
        top.Children.Add(buttons);
        top.Children.Add(_searchSummary);
        var panel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(_searchList);
        return panel;
    }

    private void ShowNavigation(TabItem tab)
    {
        if (NavColumn.ActualWidth < 120) NavColumn.Width = new GridLength(270);
        NavTabs.SelectedItem = tab;
    }

    // ------------------------------------------------------------------ refresh

    private void RefreshPanels(ChangeKind kind)
    {
        var session = Session;
        RefreshThumbnails(session, kind);
        _bookmarks = session is null ? [] : SafeRead(() => session.Document.GetBookmarks(), []);
        _bookmarkTree.ItemsSource = _bookmarks;
        Fire(ReloadCommentsAsync);
        RefreshAttachments();
        RefreshFieldsPanel();
        _signatureList.ItemsSource = null;
        if (ReferenceEquals(NavTabs.SelectedItem, _signaturesTab)) Fire(ValidateSignaturesAsync);
        ClearSearch(keepQuery: true);
    }

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ArgumentOutOfRangeException)
        {
            return fallback;
        }
    }

    private void RefreshThumbnails(PdfSession? session, ChangeKind kind)
    {
        if (session is null)
        {
            _thumbList.ItemsSource = null;
            return;
        }
        var existing = _thumbList.ItemsSource as List<PageThumb>;
        var sameDocument = ReferenceEquals(_thumbList.Tag, session);
        if (kind == ChangeKind.Content && sameDocument && existing is not null && existing.Count == session.PageCount)
        {
            foreach (var thumb in existing) thumb.Invalidate();
            return;
        }
        _thumbList.Tag = session;
        _thumbList.ItemsSource = PageThumb.For(session, 150, VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    private void SyncThumbnailSelection(int page)
    {
        if (_thumbList.ItemsSource is not List<PageThumb> thumbs || page < 0 || page >= thumbs.Count || _thumbList.SelectedIndex == page) return;
        _syncingThumbs = true;
        _thumbList.SelectedIndex = page;
        if (ReferenceEquals(NavTabs.SelectedItem, _thumbsTab)) _thumbList.ScrollIntoView(thumbs[page]);
        _syncingThumbs = false;
    }

    private async Task ReloadCommentsAsync()
    {
        var session = Session;
        if (session is null)
        {
            _commentList.ItemsSource = null;
            return;
        }
        var rows = await Task.Run(() => SafeRead(() =>
        {
            var list = new List<ListRow>();
            for (var p = 0; p < session.PageCount; p++)
            {
                foreach (var a in session.Document.GetAnnotations(p).Where(a => a.IsMarkup))
                {
                    var detail = a.Contents.Length > 0 ? a.Contents : "(no text)";
                    list.Add(new ListRow($"{a.DisplayType}  ·  page {p + 1}" + (a.Author.Length > 0 ? $"  ·  {a.Author}" : ""), detail, a));
                }
            }
            return list;
        }, []));
        if (ReferenceEquals(Session, session)) _commentList.ItemsSource = rows;
    }

    private void RefreshAttachments()
    {
        var session = Session;
        _attachmentList.ItemsSource = session is null
            ? null
            : SafeRead(() => session.Document.GetAttachments().Select(a => new ListRow(a.Name, FormatSize(a.Size), a)).ToList(), []);
    }

    private void RefreshFieldsPanel()
    {
        _fieldList.ItemsSource = Viewer?.Fields
            .Select(f => new ListRow(f.Name.Length > 0 ? f.Name : "(unnamed)", $"{f.DateFormat?.Kind ?? f.Type.ToString()}  ·  page {f.PageIndex + 1}" + (f.Value.Length > 0 ? $"  ·  {f.Value}" : ""), f))
            .ToList();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} bytes",
    };

    // ------------------------------------------------------------------ search

    private void FindCommand()
    {
        if (Viewer is null) return;
        ShowNavigation(_searchTab);
        if (Viewer.SelectedText is { Length: > 0 and < 100 } selected && !selected.Contains('\n')) _searchBox.Text = selected.Trim();
        _searchBox.Focus();
        _searchBox.SelectAll();
    }

    private void ClearSearch(bool keepQuery)
    {
        _searchCancel?.Cancel();
        _searchHits = [];
        _searchIndex = -1;
        _searchList.ItemsSource = null;
        _searchSummary.Text = "";
        if (!keepQuery) _searchBox.Text = "";
        Viewer?.SetSearchHits(_searchHits, -1);
    }

    private async Task RunSearchAsync()
    {
        var session = Session;
        var query = _searchBox.Text;
        if (session is null || query.Length == 0)
        {
            ClearSearch(keepQuery: true);
            return;
        }
        _searchCancel?.Cancel();
        var cancel = _searchCancel = new CancellationTokenSource();
        var options = new SearchOptions(_searchCase.IsChecked == true, _searchWord.IsChecked == true, _searchRegex.IsChecked == true);
        _searchSummary.Text = "Searching…";
        const int limit = 5000;
        List<SearchHit> hits;
        try
        {
            hits = await Task.Run(() => SafeRead(() => TextFinder.Search(session.Document, query, options, null, cancel.Token).Take(limit).ToList(), []));
        }
        catch (InvalidOperationException ex)
        {
            _searchSummary.Text = ex.Message;
            return;
        }
        if (cancel.IsCancellationRequested || !ReferenceEquals(Session, session)) return;
        _searchHits = hits;
        _searchIndex = hits.Count > 0 ? 0 : -1;
        _searchList.ItemsSource = hits.Select(h => new ListRow($"Page {h.PageIndex + 1}", h.Context, h)).ToList();
        _searchList.SelectedIndex = _searchIndex;
        _searchSummary.Text = hits.Count == 0 ? "No matches. (Scanned pages need Convert > Recognize Text first.)"
            : hits.Count >= limit ? $"Showing the first {limit} matches." : $"{hits.Count} match{(hits.Count == 1 ? "" : "es")}.";
        Viewer?.SetSearchHits(hits, _searchIndex);
    }

    private void StepSearch(int direction)
    {
        if (_searchHits.Count == 0)
        {
            if (_searchBox.Text.Length > 0) Fire(RunSearchAsync);
            else FindCommand();
            return;
        }
        _searchIndex = (_searchIndex + direction + _searchHits.Count) % _searchHits.Count;
        _searchList.SelectedIndex = _searchIndex;
        _searchList.ScrollIntoView(_searchList.SelectedItem);
        Viewer?.SetSearchHits(_searchHits, _searchIndex);
    }

    // ------------------------------------------------------------------ bookmarks

    private static List<int>? PathTo(List<BookmarkNode> nodes, BookmarkNode target)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (ReferenceEquals(nodes[i], target)) return [i];
            if (PathTo(nodes[i].Children, target) is { } below)
            {
                below.Insert(0, i);
                return below;
            }
        }
        return null;
    }

    private List<int>? SelectedBookmarkPath() =>
        _bookmarkTree.SelectedItem is BookmarkNode node ? PathTo(_bookmarks, node) : null;

    private async Task AddBookmarkAsync()
    {
        if (Viewer is null || Session is null) return;
        var page = Viewer.CurrentPage;
        var suggestion = Viewer.SelectedText is { Length: > 0 and < 80 } selected ? selected.Trim() : $"Page {page + 1}";
        var title = Ui.Prompt("Add bookmark", "Title", suggestion);
        if (string.IsNullOrWhiteSpace(title)) return;
        var parent = SelectedBookmarkPath() ?? [];
        if (await Do("Add bookmark", b => DocumentService.AddBookmark(b, parent, title.Trim(), page))) ShowNavigation(_bookmarksTab);
    }

    private async Task RenameBookmarkAsync()
    {
        if (SelectedBookmarkPath() is not { } path || _bookmarkTree.SelectedItem is not BookmarkNode node) return;
        var title = Ui.Prompt("Rename bookmark", "Title", node.Title);
        if (string.IsNullOrWhiteSpace(title) || title == node.Title) return;
        await Do("Rename bookmark", b => DocumentService.RenameBookmark(b, path, title.Trim()));
    }

    private async Task DeleteBookmarkAsync()
    {
        if (SelectedBookmarkPath() is not { } path) return;
        await Do("Delete bookmark", b => DocumentService.DeleteBookmark(b, path));
    }

    // ------------------------------------------------------------------ attachments

    private async Task AttachFileAsync()
    {
        if (Session is null) return;
        var path = Ui.PickOpen("All files|*.*", "Attach a file");
        if (path is null) return;
        var name = Path.GetFileName(path);
        var data = await File.ReadAllBytesAsync(path);
        if (await Do("Attach file", b => PdfiumOps.AddAttachment(b, name, data))) ShowNavigation(_attachmentsTab);
    }

    private void SaveAttachmentCommand()
    {
        if (Session is null || _attachmentList.SelectedItem is not ListRow { Tag: AttachmentInfo attachment }) return;
        var path = Ui.PickSave("All files|*.*", Path.GetFileName(attachment.Name));
        if (path is null) return;
        File.WriteAllBytes(path, Session.Document.GetAttachmentData(attachment.Index));
        SetStatus($"Saved {Path.GetFileName(path)}.");
    }

    private async Task DeleteAttachmentAsync()
    {
        if (_attachmentList.SelectedItem is not ListRow { Tag: AttachmentInfo attachment }) return;
        await Do("Delete attachment", b => PdfiumOps.DeleteAttachment(b, attachment.Index));
    }

    // ------------------------------------------------------------------ comments

    private async Task DeleteAllCommentsAsync()
    {
        if (Session is null || !Ui.Confirm("Delete every comment in this document?")) return;
        await Do("Delete all comments", b => AnnotationService.DeleteAll(b));
    }

    private void ExportCommentsCommand()
    {
        if (Session is null) return;
        var rows = (_commentList.ItemsSource as List<ListRow>) ?? [];
        if (rows.Count == 0)
        {
            Ui.Info("This document has no comments.");
            return;
        }
        var path = Ui.PickSave("Text file (*.txt)|*.txt", Path.GetFileNameWithoutExtension(Session.DisplayName) + " - comments.txt");
        if (path is null) return;
        var text = new StringBuilder($"Comments in {Session.DisplayName}\r\n\r\n");
        foreach (var row in rows)
        {
            var a = (AnnotationInfo)row.Tag!;
            text.Append($"Page {a.PageIndex + 1}  |  {a.DisplayType}  |  {a.Author}  |  {DocumentService.FormatDate(a.Modified)}\r\n");
            if (a.Contents.Length > 0) text.Append("    " + a.Contents.Replace("\n", "\n    ") + "\r\n");
            text.Append("\r\n");
        }
        File.WriteAllText(path, text.ToString());
        SetStatus($"Saved {rows.Count} comments to {Path.GetFileName(path)}.");
    }

    // ------------------------------------------------------------------ form fields panel

    private async Task DeleteSelectedFieldAsync()
    {
        if (_fieldList.SelectedItem is not ListRow { Tag: FormFieldInfo field }) return;
        await Do("Delete form field", b => FormService.DeleteField(b, field.PageIndex, field.AnnotIndex));
    }

    // ------------------------------------------------------------------ signatures panel

    private async Task ValidateSignaturesAsync()
    {
        var session = Session;
        if (session is null)
        {
            _signatureList.ItemsSource = null;
            return;
        }
        var bytes = session.Bytes;
        var reports = await Task.Run(() => SignatureService.Verify(bytes));
        if (!ReferenceEquals(Session, session)) return;
        _signatureList.ItemsSource = reports.Count == 0
            ? [new ListRow("No digital signatures", "This document has not been signed with a certificate.")]
            : reports.Select(r => new ListRow(
                (r.IntegrityValid && r.CoversWholeDocument ? "✔ " : "✖ ") + r.Signer,
                $"{r.Summary}\nSigned: {r.SignedAt}" + (r.Reason.Length > 0 ? $"\nReason: {r.Reason}" : "") + (r.Issuer.Length > 0 ? $"\nIssued by: {r.Issuer}" : ""), r)).ToList();
    }
}
