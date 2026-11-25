using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PDF4Free.App.Dialogs;
using PDF4Free.App.Services;
using PDF4Free.App.Views;
using PDF4Free.Core.Editing;

namespace PDF4Free.App;

/// <summary>The ribbon (one tab per task area) and the options bar of the active tool.</summary>
public partial class MainWindow
{
    private enum Need { Nothing, Document, Editable }

    private Button? _undoButton;
    private Button? _redoButton;
    private TextBox? _pageBox;
    private TextBlock? _pageCountText;
    private ToggleButton? _organizeButton;
    private ToggleButton? _readAloudButton;

    /// <summary>Runs a command and shows any failure instead of losing it.</summary>
    private async void Fire(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
        }
        catch (Exception ex)
        {
            Ui.Warn(ex.Message);
        }
    }

    // ------------------------------------------------------------------ building blocks

    private StackPanel RibbonTab(string header)
    {
        // The bottom margin keeps the group captions clear of the (overlaid) horizontal scroll bar on narrow windows.
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 12) };
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel,
        };
        Ribbon.Items.Add(new TabItem { Header = header, Content = scroll });
        return panel;
    }

    private void Group(StackPanel tab, string caption, params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        foreach (var item in items) row.Children.Add(item);
        var label = new TextBlock { Text = caption, Style = (Style)FindResource("GroupCaption") };
        var column = new DockPanel { Margin = new Thickness(2, 0, 2, 0) };
        DockPanel.SetDock(label, Dock.Bottom);
        column.Children.Add(label);
        column.Children.Add(row);
        tab.Children.Add(column);
        var divider = new Border { Width = 1, Margin = new Thickness(3, 6, 3, 6) };
        divider.SetResourceReference(Border.BackgroundProperty, "ControlStrokeColorDefaultBrush");
        tab.Children.Add(divider);
    }

    private StackPanel ButtonFace(string icon, string label)
    {
        var face = new StackPanel { Orientation = Orientation.Vertical };
        face.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 3) });
        face.Children.Add(new TextBlock { Text = label, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 74, LineHeight = 13 });
        return face;
    }

    private void Dress(ButtonBase button, string icon, string label, string tip, Need need)
    {
        button.Content = ButtonFace(icon, label);
        button.ToolTip = tip;
        // The face is an icon plus text, so give screen readers (and UI automation) the plain label.
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        System.Windows.Automation.AutomationProperties.SetHelpText(button, tip);
        button.Padding = new Thickness(4, 3, 4, 3);
        button.Margin = new Thickness(0);
        button.MinWidth = 48;
        button.Height = 62;
        button.VerticalContentAlignment = VerticalAlignment.Top;
        button.BorderThickness = new Thickness(0);
        button.Background = Brushes.Transparent;
        button.Focusable = false;
        if (need != Need.Nothing) _needsDocument.Add(button);
        if (need == Need.Editable) _needsEditable.Add(button);
    }

    private Button Cmd(string icon, string label, Action action, string tip, Need need = Need.Document)
    {
        var button = new Button();
        Dress(button, icon, label, tip, need);
        button.Click += (_, _) => action();
        return button;
    }

    private Button Cmd(string icon, string label, Func<Task> action, string tip, Need need = Need.Document) =>
        Cmd(icon, label, () => Fire(action), tip, need);

    private bool _syncingToggles;

    /// <summary>
    /// Reacts to a toggle button being switched by the user. It listens to the state change rather than to clicks,
    /// so keyboard, screen readers and UI automation work exactly like the mouse.
    /// </summary>
    private void OnToggled(ToggleButton button, Action<bool> handler)
    {
        button.Checked += (_, _) =>
        {
            PaintToggle(button);
            if (!_syncingToggles) handler(true);
        };
        button.Unchecked += (_, _) =>
        {
            PaintToggle(button);
            if (!_syncingToggles) handler(false);
        };
    }

    private static readonly Brush PressedFill = new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0x78, 0xD4));
    private static readonly Brush PressedEdge = new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x78, 0xD4));

    /// <summary>The ribbon buttons are flat, so a pressed toggle gets its own tint to show which tool is active.</summary>
    private static void PaintToggle(ToggleButton button)
    {
        var on = button.IsChecked == true;
        button.Background = on ? PressedFill : Brushes.Transparent;
        button.BorderBrush = on ? PressedEdge : Brushes.Transparent;
        button.BorderThickness = new Thickness(on ? 1 : 0);
    }

    /// <summary>Shows a toggle button as on or off without that counting as the user switching it.</summary>
    private void SetToggle(ToggleButton? button, bool value)
    {
        if (button is null || button.IsChecked == value) return;
        _syncingToggles = true;
        button.IsChecked = value;
        _syncingToggles = false;
    }

    /// <summary>A button that selects a viewer tool and stays pressed while that tool is active.</summary>
    private ToggleButton ToolButton(string icon, string label, ViewerTool tool, string tip, Need need = Need.Editable)
    {
        var button = new ToggleButton();
        Dress(button, icon, label, tip, need);
        OnToggled(button, on =>
        {
            if (Viewer is null)
            {
                SetToggle(button, false);
                return;
            }
            ShowViewer();
            if (on) Viewer.Tool = tool;
            else if (Viewer.Tool == tool) Viewer.Tool = ViewerTool.Select;
            RefreshToolUi();
        });
        _toolButtons[tool] = button;
        return button;
    }

    /// <summary>A button that opens a menu of related commands.</summary>
    private Button MenuButton(string icon, string label, string tip, Need need, params (string Header, Action Action)[] entries)
    {
        var button = new Button();
        Dress(button, icon, label + " ▾", tip, need);
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var (header, action) in entries)
        {
            if (header == "-")
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        button.Click += (_, _) => menu.IsOpen = true;
        return button;
    }

    // ------------------------------------------------------------------ the tabs

    private void BuildRibbon()
    {
        BuildHomeTab();
        BuildEditTab();
        BuildCommentTab();
        BuildOrganizeTab();
        BuildConvertTab();
        BuildFormsTab();
        BuildProtectTab();
        BuildSignTab();
        BuildToolsTab();
        Ribbon.SelectedIndex = 0;
    }

    private void BuildHomeTab()
    {
        var tab = RibbonTab("Home");
        Group(tab, "File",
            Cmd(Icons.Open, "Open", OpenCommand, "Open a PDF (Ctrl+O)", Need.Nothing),
            Cmd(Icons.Save, "Save", () => SaveCommandAsync(false), "Save (Ctrl+S)"),
            Cmd(Icons.SaveAs, "Save As", () => SaveCommandAsync(true), "Save under a new name (Ctrl+Shift+S)"),
            Cmd(Icons.Print, "Print", PrintCommand, "Print (Ctrl+P)"),
            MenuButton(Icons.Add, "Create", "Create a new PDF", Need.Nothing,
                ("From files (images, text, Office documents, web pages)…", CreateFromFilesCommand),
                ("Blank document…", CreateBlankCommand),
                ("From the clipboard picture", CreateFromClipboardCommand),
                ("From a scanner…", CreateFromScannerCommand),
                ("From a web address…", () => Fire(CreateFromWebAsync)),
                ("-", () => { }),
                ("Combine files into one PDF…", CombineCommand)));

        _undoButton = Cmd(Icons.Undo, "Undo", UndoCommand, "Undo (Ctrl+Z)");
        _redoButton = Cmd(Icons.Redo, "Redo", RedoCommand, "Redo (Ctrl+Y)");
        Group(tab, "History", _undoButton, _redoButton);

        _pageBox = new TextBox { Width = 52, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Type a page number and press Enter" };
        _pageBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Viewer is null) return;
            if (int.TryParse(_pageBox.Text, out var number)) Viewer.GoToPage(number - 1);
            Viewer.Focus();
        };
        _pageCountText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), Opacity = 0.75 };
        var pageRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 10, 4, 0) };
        pageRow.Children.Add(_pageBox);
        pageRow.Children.Add(_pageCountText);
        Group(tab, "Pages",
            Cmd(Icons.Left, "Previous", () => Viewer?.GoToPage(Viewer.CurrentPage - 1), "Previous page (Left arrow)"),
            pageRow,
            Cmd(Icons.Right, "Next", () => Viewer?.GoToPage(Viewer.CurrentPage + 1), "Next page (Right arrow)"));

        Group(tab, "Zoom",
            Cmd(Icons.ZoomOut, "Zoom Out", () => Viewer?.ZoomOut(), "Zoom out (Ctrl+-)"),
            Cmd(Icons.ZoomIn, "Zoom In", () => Viewer?.ZoomIn(), "Zoom in (Ctrl++)"),
            Cmd(Icons.FullScreen, "Fit Width", () => Viewer?.SetFit(FitMode.Width), "Fit the page width (Ctrl+2)"),
            Cmd(Icons.FitPage, "Fit Page", () => Viewer?.SetFit(FitMode.Page), "Show the whole page (Ctrl+0)"),
            Cmd(Icons.Page, "100%", () => Viewer?.SetZoom(1), "Actual size (Ctrl+1)"));

        Group(tab, "Tools",
            ToolButton(Icons.Pointer, "Select", ViewerTool.Select, "Select text, follow links, fill forms", Need.Document),
            ToolButton(Icons.Hand, "Hand", ViewerTool.Hand, "Drag to scroll", Need.Document),
            ToolButton(Icons.Camera, "Snapshot", ViewerTool.Snapshot, "Copy an area as a picture", Need.Document),
            ToolButton(Icons.Ruler, "Measure", ViewerTool.Measure, "Measure a distance", Need.Document),
            Cmd(Icons.Search, "Find", FindCommand, "Find text (Ctrl+F)"));

        _readAloudButton = new ToggleButton();
        Dress(_readAloudButton, Icons.Volume, "Read Aloud", "Read the current page aloud", Need.Document);
        OnToggled(_readAloudButton, _ => ToggleReadAloud());
        Group(tab, "Document",
            _readAloudButton,
            Cmd(Icons.Info, "Properties", PropertiesCommand, "Title, author, security, fonts (Ctrl+D)"));
    }

    private void BuildEditTab()
    {
        var tab = RibbonTab("Edit");
        Group(tab, "Content",
            ToolButton(Icons.Edit, "Edit", ViewerTool.EditObjects, "Select, move, resize or delete text, pictures and shapes; double-click text to change it"),
            ToolButton(Icons.Text, "Add Text", ViewerTool.AddText, "Type new text on the page"),
            Cmd(Icons.Picture, "Add Image", AddImageCommand, "Place a picture on the page", Need.Editable),
            ToolButton(Icons.Link, "Link", ViewerTool.AddLink, "Add a link to a web address or a page"),
            ToolButton(Icons.Eraser, "Whiteout", ViewerTool.Whiteout, "Cover an area with white"));
        Group(tab, "Page design",
            Cmd(Icons.Sticker, "Watermark", () => WatermarkAsync(), "Add a text or picture watermark", Need.Editable),
            Cmd(Icons.Document, "Header & Footer", () => HeaderFooterAsync(), "Add headers, footers and page numbers", Need.Editable),
            Cmd(Icons.Calculator, "Bates Numbers", () => BatesAsync(), "Stamp sequential Bates numbers", Need.Editable),
            Cmd(Icons.Color, "Background", () => BackgroundAsync(), "Fill the page background with a colour", Need.Editable));
        Group(tab, "Page area",
            ToolButton(Icons.Crop, "Crop", ViewerTool.Crop, "Drag the area of the page to keep"),
            Cmd(Icons.Refresh, "Remove Crop", () => RemoveCropAsync(), "Show the full page again", Need.Editable));
        Group(tab, "Finalise",
            Cmd(Icons.Merge, "Flatten", () => FlattenAsync(), "Burn comments and form fields into the page", Need.Editable));
    }

    private void BuildCommentTab()
    {
        var tab = RibbonTab("Comment");
        Group(tab, "Text markup",
            ToolButton(Icons.Highlight, "Highlight", ViewerTool.Highlight, "Highlight text"),
            ToolButton(Icons.Underline, "Underline", ViewerTool.Underline, "Underline text"),
            ToolButton(Icons.Strikethrough, "Strikethrough", ViewerTool.StrikeOut, "Strike through text"),
            ToolButton(Icons.Font, "Squiggly", ViewerTool.Squiggly, "Squiggly underline"));
        Group(tab, "Notes",
            ToolButton(Icons.Comment, "Sticky Note", ViewerTool.Note, "Add a note"),
            ToolButton(Icons.Rename, "Text Box", ViewerTool.TextBox, "Add a text box comment"));
        Group(tab, "Drawing",
            ToolButton(Icons.Pen, "Pen", ViewerTool.Ink, "Draw freehand"),
            ToolButton(Icons.Stop, "Rectangle", ViewerTool.Rectangle, "Draw a rectangle"),
            ToolButton(Icons.Circle, "Ellipse", ViewerTool.Ellipse, "Draw an ellipse"),
            ToolButton(Icons.Minus, "Line", ViewerTool.Line, "Draw a line"),
            ToolButton(Icons.Upload, "Arrow", ViewerTool.Arrow, "Draw an arrow"));
        Group(tab, "Stamps",
            ToolButton(Icons.Stamp, "Stamp", ViewerTool.Stamp, "Place a stamp such as APPROVED or DRAFT"),
            Cmd(Icons.Picture, "Picture Stamp", PictureStampCommand, "Use a picture as a stamp", Need.Editable));
        Group(tab, "Manage",
            Cmd(Icons.List, "Comment List", () => ShowNavigation(_commentsTab), "Show all comments"),
            Cmd(Icons.Download, "Summary", ExportCommentsCommand, "Save a text summary of all comments"),
            Cmd(Icons.Delete, "Delete All", () => DeleteAllCommentsAsync(), "Remove every comment", Need.Editable));
    }

    private void BuildOrganizeTab()
    {
        var tab = RibbonTab("Organize");
        _organizeButton = new ToggleButton();
        Dress(_organizeButton, Icons.Grid, "Page Grid", "Show all pages as thumbnails: select several, drag to reorder", Need.Document);
        OnToggled(_organizeButton, _ => ToggleOrganizer());
        Group(tab, "View", _organizeButton);
        Group(tab, "Selected pages",
            Cmd(Icons.Undo, "Rotate Left", () => RotatePagesAsync(-90), "Rotate 90° counter-clockwise", Need.Editable),
            Cmd(Icons.RotateRight, "Rotate Right", () => RotatePagesAsync(90), "Rotate 90° clockwise", Need.Editable),
            Cmd(Icons.Delete, "Delete", () => DeletePagesAsync(), "Delete the selected pages", Need.Editable),
            Cmd(Icons.Copy, "Duplicate", () => DuplicatePagesAsync(), "Duplicate the selected pages", Need.Editable),
            Cmd(Icons.Download, "Extract", ExtractPagesCommand, "Save the selected pages as a new PDF"),
            Cmd(Icons.Up, "Move Earlier", () => MovePagesAsync(-1), "Move the selected pages one position earlier", Need.Editable),
            Cmd(Icons.Down, "Move Later", () => MovePagesAsync(+1), "Move the selected pages one position later", Need.Editable));
        Group(tab, "Insert",
            MenuButton(Icons.Add, "Insert", "Insert pages", Need.Editable,
                ("Pages from a file…", () => Fire(InsertFromFileAsync)),
                ("Blank page", () => Fire(InsertBlankAsync)),
                ("Picture from the clipboard", () => Fire(InsertClipboardAsync))),
            Cmd(Icons.Refresh, "Replace", () => ReplacePageAsync(), "Replace the current page with a page from another PDF", Need.Editable));
        Group(tab, "Document",
            Cmd(Icons.Scissors, "Split", SplitCommand, "Split into several PDF files"),
            Cmd(Icons.Merge, "Combine", CombineCommand, "Combine several files into one PDF", Need.Nothing),
            Cmd(Icons.RotateRight, "Reverse", () => ReversePagesAsync(), "Reverse the page order", Need.Editable));
    }

    private void BuildConvertTab()
    {
        var tab = RibbonTab("Convert");
        Group(tab, "Export PDF to",
            Cmd(Icons.Document, "Word", () => ExportWordAsync(), "Export to a Word document (.docx)"),
            Cmd(Icons.Table, "Excel", () => ExportExcelAsync(), "Export to an Excel workbook (.xlsx)"),
            Cmd(Icons.Slides, "PowerPoint", () => ExportPowerPointAsync(), "Export to a PowerPoint presentation (.pptx)"),
            Cmd(Icons.Picture, "Images", () => ExportImagesAsync(), "Export pages as PNG, JPEG, TIFF or BMP"),
            Cmd(Icons.Text, "Text", () => ExportTextAsync(), "Export the text"),
            Cmd(Icons.Globe, "HTML", () => ExportHtmlAsync(), "Export to a web page"),
            Cmd(Icons.Library, "Pictures", () => ExtractImagesAsync(), "Save every picture used in the document"));
        Group(tab, "Create PDF",
            Cmd(Icons.Add, "From Files", CreateFromFilesCommand, "Convert images, text, Office documents or web pages to PDF", Need.Nothing),
            Cmd(Icons.Scan, "Scanner", CreateFromScannerCommand, "Scan paper to PDF", Need.Nothing),
            Cmd(Icons.Globe, "Web Page", () => CreateFromWebAsync(), "Capture a web page as PDF", Need.Nothing));
        Group(tab, "Scan & OCR",
            Cmd(Icons.Scan, "Recognize Text", () => OcrAsync(), "Make scanned pages searchable and selectable (OCR)", Need.Editable),
            Cmd(Icons.Copy, "Copy Page Text", () => OcrCopyAsync(), "Recognise the text of the current page and copy it"));
        Group(tab, "Optimise",
            Cmd(Icons.Compress, "Compress", () => CompressAsync(), "Reduce the file size", Need.Editable));
    }

    private void BuildFormsTab()
    {
        var tab = RibbonTab("Forms");
        Group(tab, "Add field",
            ToolButton(Icons.Rename, "Text Field", ViewerTool.FieldText, "Add a text field"),
            ToolButton(Icons.Calendar, "Date", ViewerTool.FieldDate, "Add a date field with a calendar"),
            ToolButton(Icons.DateTime, "Date & Time", ViewerTool.FieldDateTime, "Add a field for a date and a time"),
            ToolButton(Icons.Check, "Check Box", ViewerTool.FieldCheck, "Add a check box"),
            ToolButton(Icons.Circle, "Radio Button", ViewerTool.FieldRadio, "Add a radio button to a group"),
            ToolButton(Icons.Down, "Dropdown", ViewerTool.FieldCombo, "Add a dropdown list"),
            ToolButton(Icons.List, "List Box", ViewerTool.FieldList, "Add a list box"),
            ToolButton(Icons.Pen, "Signature", ViewerTool.FieldSignature, "Add a signature field"));
        Group(tab, "Fill",
            Cmd(Icons.Forms, "Field List", () => ShowNavigation(_fieldsTab), "List all form fields"),
            Cmd(Icons.View, "Highlight", ToggleFieldHighlight, "Show or hide the tint over fillable fields"),
            Cmd(Icons.Refresh, "Reset", () => ResetFormAsync(), "Clear all field values", Need.Editable));
        Group(tab, "Data",
            Cmd(Icons.Download, "Export Data", ExportFormDataCommand, "Save the field values as a CSV file"),
            Cmd(Icons.Upload, "Import Data", () => ImportFormDataAsync(), "Fill the form from a CSV file", Need.Editable),
            Cmd(Icons.Merge, "Flatten", () => FlattenAsync(), "Make the filled-in values a permanent part of the page", Need.Editable));
    }

    private void BuildProtectTab()
    {
        var tab = RibbonTab("Protect");
        Group(tab, "Security",
            Cmd(Icons.Lock, "Encrypt", () => EncryptAsync(), "Protect with passwords and permissions", Need.Editable),
            Cmd(Icons.Unlock, "Unlock", UnlockCommand, "Enter the permissions password to allow editing"));
        Group(tab, "Redact",
            ToolButton(Icons.Stop, "Mark Area", ViewerTool.RedactArea, "Mark an area for redaction"),
            ToolButton(Icons.Highlight, "Mark Text", ViewerTool.RedactText, "Mark text for redaction"),
            Cmd(Icons.Search, "Find & Mark", () => FindAndRedactAsync(), "Search for text or patterns (e-mails, card numbers…) and mark every match", Need.Editable),
            Cmd(Icons.Check, "Apply", () => ApplyRedactionsAsync(), "Permanently remove everything under the marks", Need.Editable),
            Cmd(Icons.Close, "Clear Marks", () => ClearRedactionsAsync(), "Remove the redaction marks without redacting", Need.Editable));
        Group(tab, "Hidden information",
            Cmd(Icons.Shield, "Sanitize", () => SanitizeAsync(), "Remove metadata, scripts, attachments, comments and other hidden data", Need.Editable));
    }

    private void BuildSignTab()
    {
        var tab = RibbonTab("Sign");
        Group(tab, "Fill & Sign",
            Cmd(Icons.Pen, "Add Signature", AddSignatureCommand, "Draw, type or import your signature and place it", Need.Editable),
            Cmd(Icons.Text, "Add Text", () => { if (Viewer is not null) { ShowViewer(); Viewer.Tool = ViewerTool.AddText; } }, "Type on the page (for forms without fields)", Need.Editable),
            Cmd(Icons.Check, "Add Date", AddDateCommand, "Place today's date", Need.Editable));
        Group(tab, "Certificates",
            Cmd(Icons.Certificate, "Sign with Certificate", StartCertificateSignCommand, "Digitally sign with a certificate (digital ID)", Need.Editable),
            Cmd(Icons.Shield, "Validate", () => ShowNavigation(_signaturesTab), "Check the digital signatures in this document"),
            Cmd(Icons.Add, "Create Digital ID", CreateDigitalIdCommand, "Create a self-signed certificate file (.pfx)", Need.Nothing));
    }

    private void BuildToolsTab()
    {
        var tab = RibbonTab("Tools");
        Group(tab, "Review",
            Cmd(Icons.Compare, "Compare Files", () => CompareAsync(), "Find the differences between two PDF files", Need.Nothing));
        Group(tab, "Automate",
            Cmd(Icons.Batch, "Batch Process", () => BatchAsync(), "Apply one action to many files", Need.Nothing));
        Group(tab, "Document",
            Cmd(Icons.Attach, "Attach File", () => AttachFileAsync(), "Embed a file in this PDF", Need.Editable),
            Cmd(Icons.Bookmark, "Add Bookmark", () => AddBookmarkAsync(), "Bookmark the current page", Need.Editable));
        Group(tab, "Application",
            Cmd(Icons.Settings, "Preferences", PreferencesCommand, "Author name and OCR language", Need.Nothing),
            Cmd(Icons.Help, "About", AboutCommand, "About PDF4Free", Need.Nothing));
    }

    // ------------------------------------------------------------------ tool options bar

    private static readonly string[] StampTexts =
        ["APPROVED", "NOT APPROVED", "DRAFT", "FINAL", "CONFIDENTIAL", "COMPLETED", "FOR COMMENT", "FOR PUBLIC RELEASE", "VOID", "EXPIRED", "RECEIVED", "REVIEWED", "SIGN HERE", "TOP SECRET"];

    private void RefreshToolUi()
    {
        var viewer = Viewer;
        var tool = viewer?.Tool ?? ViewerTool.Select;
        foreach (var (key, button) in _toolButtons) SetToggle(button, viewer is not null && key == tool && !(Active?.Organizing ?? false));
        SetToggle(_organizeButton, Active?.Organizing ?? false);
        ToolHint.Text = viewer is null ? "Open a document to get started." : tool.Hint();
        OptionsPanel.Children.Clear();
        if (viewer is null) return;
        var options = viewer.Options;
        switch (tool)
        {
            case ViewerTool.Highlight:
                OptionsPanel.Children.Add(Swatches("Colour", options.HighlightColor, c => options.HighlightColor = c, [0xFFD400, 0xA5F04A, 0x7FDBFF, 0xFFA6D5, 0xFFB74D]));
                break;
            case ViewerTool.Underline or ViewerTool.StrikeOut or ViewerTool.Squiggly:
                OptionsPanel.Children.Add(Swatches("Colour", options.Color, c => options.Color = c));
                break;
            case ViewerTool.Note:
                OptionsPanel.Children.Add(Swatches("Note colour", options.HighlightColor, c => options.HighlightColor = c, [0xFFD400, 0xA5F04A, 0x7FDBFF, 0xFFA6D5, 0xFFB74D]));
                break;
            case ViewerTool.Ink or ViewerTool.Line or ViewerTool.Arrow:
                OptionsPanel.Children.Add(Swatches("Colour", options.Color, c => options.Color = c));
                OptionsPanel.Children.Add(Choice("Thickness", [0.5, 1, 1.5, 2, 3, 4, 6, 8, 12], options.LineWidth, v => options.LineWidth = v, v => $"{v:0.#} pt"));
                OptionsPanel.Children.Add(Choice("Opacity", [1, 0.75, 0.5, 0.25], options.Opacity, v => options.Opacity = v, v => $"{v * 100:0}%"));
                break;
            case ViewerTool.Rectangle or ViewerTool.Ellipse:
                OptionsPanel.Children.Add(Swatches("Outline", options.Color, c => options.Color = c));
                OptionsPanel.Children.Add(FillPicker(options));
                OptionsPanel.Children.Add(Choice("Thickness", [0.5, 1, 1.5, 2, 3, 4, 6, 8], options.LineWidth, v => options.LineWidth = v, v => $"{v:0.#} pt"));
                OptionsPanel.Children.Add(Choice("Opacity", [1, 0.75, 0.5, 0.25], options.Opacity, v => options.Opacity = v, v => $"{v * 100:0}%"));
                break;
            case ViewerTool.TextBox:
                OptionsPanel.Children.Add(Swatches("Text", options.Color, c => options.Color = c));
                OptionsPanel.Children.Add(FillPicker(options));
                OptionsPanel.Children.Add(Choice("Size", [8, 9, 10, 11, 12, 14, 16, 18, 24], options.FontSize, v => options.FontSize = v, v => $"{v:0.#} pt"));
                break;
            case ViewerTool.AddText:
                AddFontOptions(options);
                break;
            case ViewerTool.Stamp:
                AddStampOptions(options);
                break;
        }
    }

    private static TextBlock OptionLabel(string text) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.8 };

    private UIElement Swatches(string label, uint current, Action<uint> set, uint[]? palette = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
        panel.Children.Add(OptionLabel(label));
        foreach (var rgb in palette ?? [.. FormDialog.Palette.Select(p => p.Rgb)])
        {
            var selected = rgb == current;
            var swatch = new Border
            {
                Width = 20, Height = 20, Margin = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)),
                BorderBrush = selected ? Brushes.DodgerBlue : Brushes.Gray, BorderThickness = new Thickness(selected ? 3 : 1),
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                set(rgb);
                RefreshToolUi();
            };
            panel.Children.Add(swatch);
        }
        return panel;
    }

    private UIElement FillPicker(ToolOptions options)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
        var check = new CheckBox { Content = "Fill", IsChecked = options.Fill is not null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        void Apply()
        {
            options.Fill = check.IsChecked == true ? 0xFFF59D : null;
            RefreshToolUi();
        }
        check.Checked += (_, _) => Apply();
        check.Unchecked += (_, _) => Apply();
        panel.Children.Add(check);
        if (options.Fill is { } fill) panel.Children.Add(Swatches("", fill, c => options.Fill = c, [0xFFF59D, 0xBBDEFB, 0xC8E6C9, 0xFFCDD2, 0xE1BEE7, 0xFFFFFF, 0x000000]));
        return panel;
    }

    private static UIElement Choice(string label, double[] values, double current, Action<double> set, Func<double, string> format)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
        panel.Children.Add(OptionLabel(label));
        var combo = new ComboBox { MinWidth = 84, ItemsSource = values.Select(format).ToList(), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(combo, label);
        var index = Array.FindIndex(values, v => Math.Abs(v - current) < 0.001);
        combo.SelectedIndex = index < 0 ? 0 : index;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) set(values[combo.SelectedIndex]);
        };
        panel.Children.Add(combo);
        return panel;
    }

    private void AddFontOptions(ToolOptions options)
    {
        var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).Where(n => !n.Contains('#')).OrderBy(n => n).ToList();
        var family = new ComboBox { Width = 180, ItemsSource = fonts, SelectedItem = options.FontFamily, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        family.SelectionChanged += (_, _) => options.FontFamily = family.SelectedItem as string ?? "Arial";
        System.Windows.Automation.AutomationProperties.SetName(family, "Font");
        OptionsPanel.Children.Add(OptionLabel("Font"));
        OptionsPanel.Children.Add(family);
        OptionsPanel.Children.Add(Choice("Size", [6, 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72], options.FontSize, v => options.FontSize = v, v => $"{v:0.#} pt"));
        var bold = new ToggleButton { Content = "B", FontWeight = FontWeights.Bold, Width = 32, IsChecked = options.Bold, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Bold" };
        bold.Checked += (_, _) => options.Bold = true;
        bold.Unchecked += (_, _) => options.Bold = false;
        System.Windows.Automation.AutomationProperties.SetName(bold, "Bold");
        var italic = new ToggleButton { Content = "I", FontStyle = FontStyles.Italic, Width = 32, IsChecked = options.Italic, Margin = new Thickness(0, 0, 16, 0), ToolTip = "Italic" };
        italic.Checked += (_, _) => options.Italic = true;
        italic.Unchecked += (_, _) => options.Italic = false;
        System.Windows.Automation.AutomationProperties.SetName(italic, "Italic");
        OptionsPanel.Children.Add(bold);
        OptionsPanel.Children.Add(italic);
        OptionsPanel.Children.Add(Swatches("Colour", options.TextColor, c => options.TextColor = c));
    }

    private void AddStampOptions(ToolOptions options)
    {
        OptionsPanel.Children.Add(OptionLabel("Stamp"));
        var text = new ComboBox { Width = 190, IsEditable = true, ItemsSource = StampTexts, Text = options.StampText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.LostFocus += (_, _) => options.StampText = string.IsNullOrWhiteSpace(text.Text) ? "APPROVED" : text.Text.Trim().ToUpperInvariant();
        text.SelectionChanged += (_, _) =>
        {
            if (text.SelectedItem is string chosen) options.StampText = chosen;
        };
        OptionsPanel.Children.Add(text);
        OptionsPanel.Children.Add(Swatches("Colour", options.StampColor, c => options.StampColor = c, [0x2E7D32, 0xC62828, 0x1565C0, 0xEF6C00, 0x6A1B9A, 0x000000]));
        var dated = new CheckBox { Content = "Add my name and the date", IsChecked = options.StampWithDate, VerticalAlignment = VerticalAlignment.Center };
        dated.Checked += (_, _) => options.StampWithDate = true;
        dated.Unchecked += (_, _) => options.StampWithDate = false;
        OptionsPanel.Children.Add(dated);
    }

    private void ToggleFieldHighlight()
    {
        AppSettings.Current.HighlightFormFields = !AppSettings.Current.HighlightFormFields;
        AppSettings.Current.Save();
        if (Viewer is { } viewer)
        {
            // Changing the tool to itself does nothing, so nudge the overlay through a selection reset.
            viewer.ClearSelection();
            viewer.SetSearchHits(_searchHits, _searchIndex);
        }
        SetStatus(AppSettings.Current.HighlightFormFields ? "Form fields are highlighted." : "Form field highlighting is off.");
    }
}
