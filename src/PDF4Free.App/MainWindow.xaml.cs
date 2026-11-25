using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PDF4Free.App.Dialogs;
using PDF4Free.App.Services;
using PDF4Free.App.Views;
using PDF4Free.Core;

namespace PDF4Free.App;

/// <summary>Everything that belongs to one open document.</summary>
public sealed class DocumentTab(PdfSession session, PdfViewer viewer, TabItem item, TextBlock title, Grid host)
{
    public PdfSession Session { get; } = session;
    public PdfViewer Viewer { get; } = viewer;
    public TabItem Item { get; } = item;
    public TextBlock Title { get; } = title;
    public Grid Host { get; } = host;
    public PageOrganizer? Organizer { get; set; }
    public bool Organizing => Organizer is { Visibility: Visibility.Visible };
}

/// <summary>The application shell. The partial files group the commands by ribbon tab.</summary>
public partial class MainWindow : Window
{
    private readonly List<DocumentTab> _tabs = [];
    private readonly SemaphoreSlim _editGate = new(1, 1);
    private readonly List<ButtonBase> _needsDocument = [];
    private readonly List<ButtonBase> _needsEditable = [];
    private readonly Dictionary<ViewerTool, ToggleButton> _toolButtons = [];
    private int _busyCount;

    public MainWindow()
    {
        InitializeComponent();
        BuildRibbon();
        BuildNavigationPanels();
        NavColumn.Width = new GridLength(Math.Clamp(AppSettings.Current.NavigationWidth, 0, 600));
        DocTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, DocTabs)) OnActiveDocumentChanged();
        };
        WelcomeOpen.Click += (_, _) => OpenCommand();
        WelcomeCreate.Click += (_, _) => CreateFromFilesCommand();
        WelcomeCombine.Click += (_, _) => CombineCommand();
        Drop += OnFilesDropped;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Closing += OnWindowClosing;
        RegisterShortcuts();
        RefreshRecentFiles();
        OnActiveDocumentChanged();
    }

#if DEBUG
    /// <summary>Starts the debug-only test pipe (see <see cref="AutomationServer"/>).</summary>
    public void StartAutomation() => new AutomationServer(() => Viewer, () => Session, async () =>
    {
        await _editGate.WaitAsync();
        _editGate.Release();
    }).Start();
#endif

    private DocumentTab? Active => DocTabs.SelectedItem is TabItem { Tag: DocumentTab tab } ? tab : null;
    private PdfViewer? Viewer => Active?.Viewer;
    private PdfSession? Session => Active?.Session;

    // ------------------------------------------------------------------ status and busy state

    private void SetStatus(string message) => StatusText.Text = message;

    private void BeginBusy(string message)
    {
        _busyCount++;
        Busy.Visibility = Visibility.Visible;
        StatusText.Text = message;
        Mouse.OverrideCursor = Cursors.AppStarting;
    }

    private void EndBusy()
    {
        _busyCount = Math.Max(0, _busyCount - 1);
        if (_busyCount > 0) return;
        Busy.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = null;
    }

    /// <summary>Runs background work with the busy indicator; returns false (after telling the user) when it fails.</summary>
    private async Task<bool> RunBusyAsync(string description, Func<Task> work)
    {
        BeginBusy(description + "…");
        try
        {
            await work();
            SetStatus(description + ": done.");
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus(description + ": cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            SetStatus(description + ": failed.");
            Ui.Warn($"{description} could not be completed.\n\n{ex.Message}");
            return false;
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>Applies an edit to a document as one undoable step. Edits are queued so they never overlap.</summary>
    private async Task<bool> RunEditAsync(DocumentTab tab, string description, Func<byte[], byte[]> edit, ChangeKind kind)
    {
        if (tab.Session.IsRestricted)
        {
            Ui.Warn("This document is protected against changes.\n\nUse Protect > Unlock and enter the permissions password to edit it.");
            return false;
        }
        await _editGate.WaitAsync();
        try
        {
            return await RunBusyAsync(description, () => tab.Session.ApplyAsync(description, edit, kind));
        }
        finally
        {
            _editGate.Release();
        }
    }

    /// <summary>Applies an edit to the active document.</summary>
    private Task<bool> Do(string description, Func<byte[], byte[]> edit, ChangeKind kind = ChangeKind.Content) =>
        Active is { } tab ? RunEditAsync(tab, description, edit, kind) : Task.FromResult(false);

    // ------------------------------------------------------------------ tabs

    private DocumentTab AddTab(PdfSession session)
    {
        var viewer = new PdfViewer();
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button
        {
            Content = new TextBlock { Text = Icons.Close, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9 },
            Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(10, 0, 0, 0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, ToolTip = "Close (Ctrl+W)", Focusable = false,
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(title);
        header.Children.Add(close);
        var host = new Grid();
        host.Children.Add(viewer);
        var item = new TabItem { Header = header, Content = host };
        var tab = new DocumentTab(session, viewer, item, title, host);
        item.Tag = tab;
        close.Click += (_, _) => _ = CloseTabAsync(tab);

        viewer.Edit = (description, edit, kind) => RunEditAsync(tab, description, edit, kind);
        viewer.Attach(session);
        viewer.Status += SetStatus;
        viewer.ViewChanged += () => { if (ReferenceEquals(Active, tab)) RefreshViewStatus(); };
        viewer.ToolChanged += () => { if (ReferenceEquals(Active, tab)) RefreshToolUi(); };
        viewer.SelectionChanged += () => { if (ReferenceEquals(Active, tab)) RefreshCommandStates(); };
        viewer.FieldsChanged += () => { if (ReferenceEquals(Active, tab)) RefreshFieldsPanel(); };
        viewer.CertificateAreaChosen += (page, rect) => _ = SignWithCertificateAsync(page, rect);
        session.StateChanged += () => OnSessionStateChanged(tab);
        session.Changed += kind => OnSessionContentChanged(tab, kind);

        _tabs.Add(tab);
        DocTabs.Items.Add(item);
        DocTabs.SelectedItem = item;
        OnSessionStateChanged(tab);
        viewer.Focus();
        return tab;
    }

    private void OnSessionStateChanged(DocumentTab tab)
    {
        tab.Title.Text = (tab.Session.IsDirty ? "● " : "") + tab.Session.DisplayName;
        tab.Item.ToolTip = tab.Session.FilePath ?? "Not saved yet";
        if (ReferenceEquals(Active, tab)) RefreshCommandStates();
    }

    private void OnSessionContentChanged(DocumentTab tab, ChangeKind kind)
    {
        tab.Organizer?.Reload();
        if (!ReferenceEquals(Active, tab)) return;
        RefreshPanels(kind);
        RefreshViewStatus();
        RefreshInfoBar();
    }

    private void OnActiveDocumentChanged()
    {
        var tab = Active;
        Welcome.Visibility = tab is null ? Visibility.Visible : Visibility.Collapsed;
        Title = tab is null ? "PDF4Free" : $"{tab.Session.DisplayName} — PDF4Free";
        RefreshCommandStates();
        RefreshToolUi();
        RefreshViewStatus();
        RefreshInfoBar();
        RefreshPanels(ChangeKind.Structure);
        StopReadingAloud();
    }

    private async Task<bool> CloseTabAsync(DocumentTab tab)
    {
        tab.Viewer.EndEditors();
        if (tab.Session.IsDirty)
        {
            DocTabs.SelectedItem = tab.Item;
            var answer = Ui.YesNoCancel($"Save the changes to \"{tab.Session.DisplayName}\"?");
            if (answer is null) return false;
            if (answer == true && !await SaveAsync(tab, saveAs: false)) return false;
        }
        _tabs.Remove(tab);
        DocTabs.Items.Remove(tab.Item);
        tab.Session.Dispose();
        OnActiveDocumentChanged();
        return true;
    }

    private bool _closingConfirmed;

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        AppSettings.Current.NavigationWidth = NavColumn.ActualWidth;
        AppSettings.Current.Save();
        if (_closingConfirmed || _tabs.All(t => !t.Session.IsDirty)) return;
        e.Cancel = true;
        foreach (var tab in _tabs.Where(t => t.Session.IsDirty).ToList())
        {
            if (!await CloseTabAsync(tab)) return;
        }
        _closingConfirmed = true;
        Close();
    }

    // ------------------------------------------------------------------ state refresh

    private void RefreshCommandStates()
    {
        var hasDocument = Active is not null;
        var editable = hasDocument && !Session!.IsRestricted;
        foreach (var button in _needsDocument) button.IsEnabled = hasDocument;
        foreach (var button in _needsEditable) button.IsEnabled = editable;
        if (_undoButton is not null) _undoButton.IsEnabled = Session?.CanUndo == true;
        if (_redoButton is not null) _redoButton.IsEnabled = Session?.CanRedo == true;
        if (_undoButton is not null) _undoButton.ToolTip = Session?.CanUndo == true ? $"Undo {Session.UndoDescription} (Ctrl+Z)" : "Undo (Ctrl+Z)";
        if (_redoButton is not null) _redoButton.ToolTip = Session?.CanRedo == true ? $"Redo {Session.RedoDescription} (Ctrl+Y)" : "Redo (Ctrl+Y)";
        if (Active is { } tab) Title = $"{(tab.Session.IsDirty ? "● " : "")}{tab.Session.DisplayName} — PDF4Free";
    }

    private void RefreshViewStatus()
    {
        var viewer = Viewer;
        if (viewer is null || Session is null)
        {
            PageText.Text = "";
            ZoomText.Text = "";
            PageSizeText.Text = "";
            if (_pageBox is not null) _pageBox.Text = "";
            if (_pageCountText is not null) _pageCountText.Text = "";
            return;
        }
        var page = Math.Clamp(viewer.CurrentPage, 0, Math.Max(0, Session.PageCount - 1));
        PageText.Text = $"Page {page + 1} of {Session.PageCount}";
        ZoomText.Text = $"{viewer.Zoom * 100:0}%";
        var (w, h) = Session.Document.GetPageSize(page);
        PageSizeText.Text = $"{w / 72:0.##} × {h / 72:0.##} in";
        if (_pageBox is not null && !_pageBox.IsKeyboardFocused) _pageBox.Text = (page + 1).ToString();
        if (_pageCountText is not null) _pageCountText.Text = $"/ {Session.PageCount}";
        SyncThumbnailSelection(page);
    }

    private void RefreshInfoBar()
    {
        InfoAction.Visibility = Visibility.Collapsed;
        InfoAction.Click -= OnInfoUnlock;
        var session = Session;
        if (session is null)
        {
            InfoBar.Visibility = Visibility.Collapsed;
        }
        else if (session.IsRestricted)
        {
            InfoText.Text = "This document is protected: it can be read" + (session.Document.Security.CanPrint ? " and printed" : "") + ", but not changed. Enter the permissions password to edit it.";
            InfoAction.Content = "Unlock…";
            InfoAction.Visibility = Visibility.Visible;
            InfoAction.Click += OnInfoUnlock;
            InfoBar.Visibility = Visibility.Visible;
        }
        else if (session.SecurityRemoved)
        {
            InfoText.Text = "This file was password-protected. You are working on an unlocked copy: saving it writes the file WITHOUT a password. Use Protect > Encrypt to set one again.";
            InfoBar.Visibility = Visibility.Visible;
        }
        else if (session.Document.HasXfaForm)
        {
            InfoText.Text = "This document contains an XFA (LiveCycle) form. PDF4Free can display its static pages but cannot fill XFA forms.";
            InfoBar.Visibility = Visibility.Visible;
        }
        else
        {
            InfoBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnInfoUnlock(object sender, RoutedEventArgs e) => UnlockCommand();

    // ------------------------------------------------------------------ keyboard shortcuts

    private void RegisterShortcuts()
    {
        void Bind(Key key, ModifierKeys modifiers, Action action)
        {
            var command = new RoutedCommand();
            CommandBindings.Add(new CommandBinding(command, (_, _) => action()));
            InputBindings.Add(new KeyBinding(command, key, modifiers));
        }
        Bind(Key.O, ModifierKeys.Control, OpenCommand);
        Bind(Key.S, ModifierKeys.Control, () => _ = SaveCommandAsync(false));
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Shift, () => _ = SaveCommandAsync(true));
        Bind(Key.P, ModifierKeys.Control, PrintCommand);
        Bind(Key.W, ModifierKeys.Control, () => { if (Active is { } tab) _ = CloseTabAsync(tab); });
        Bind(Key.Z, ModifierKeys.Control, UndoCommand);
        Bind(Key.Y, ModifierKeys.Control, RedoCommand);
        Bind(Key.F, ModifierKeys.Control, FindCommand);
        Bind(Key.F3, ModifierKeys.None, () => StepSearch(+1));
        Bind(Key.F3, ModifierKeys.Shift, () => StepSearch(-1));
        Bind(Key.OemPlus, ModifierKeys.Control, () => Viewer?.ZoomIn());
        Bind(Key.Add, ModifierKeys.Control, () => Viewer?.ZoomIn());
        Bind(Key.OemMinus, ModifierKeys.Control, () => Viewer?.ZoomOut());
        Bind(Key.Subtract, ModifierKeys.Control, () => Viewer?.ZoomOut());
        Bind(Key.D0, ModifierKeys.Control, () => Viewer?.SetFit(FitMode.Page));
        Bind(Key.D1, ModifierKeys.Control, () => Viewer?.SetZoom(1));
        Bind(Key.D2, ModifierKeys.Control, () => Viewer?.SetFit(FitMode.Width));
        Bind(Key.D, ModifierKeys.Control, PropertiesCommand);
        Bind(Key.N, ModifierKeys.Control, CreateBlankCommand);
    }
}
