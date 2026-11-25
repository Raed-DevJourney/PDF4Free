using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PDF4Free.App.Dialogs;
using PDF4Free.App.Services;
using PDF4Free.Core;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.App.Views;

/// <summary>Mouse and keyboard handling of <see cref="PdfViewer"/>: what each tool does.</summary>
public sealed partial class PdfViewer
{
    private enum DragKind { None, Pan, Text, Rectangle, Line, Ink, MoveAnnotation, MoveObject, ResizeObject }

    private DragKind _drag;
    private int _dragPage = -1;
    private Point _dragStart;
    private Point _dragNow;
    private readonly List<Point> _ink = [];
    private Point _panMouse;
    private Point _panOffset;
    private LinkInfo? _pressedLink;

    private void AttachInput()
    {
        _surface.MouseLeftButtonDown += OnMouseDown;
        _surface.MouseMove += OnMouseMove;
        _surface.MouseLeftButtonUp += OnMouseUp;
        _surface.MouseRightButtonUp += OnRightClick;
        _surface.LostMouseCapture += (_, _) => CancelDrag();
        PreviewMouseWheel += OnWheel;
        KeyDown += OnKeyDown;
    }

    // ------------------------------------------------------------------ locating the mouse

    /// <summary>Finds the page under a surface position and the position on it in view points.</summary>
    private bool Locate(Point surface, out int page, out Point view)
    {
        page = -1;
        view = default;
        if (_layout.Length == 0) return false;
        var index = PageNear(surface.Y);
        if (!_layout[index].Contains(surface)) return false;
        page = index;
        view = new Point((surface.X - _layout[index].X) / Scale, (surface.Y - _layout[index].Y) / Scale);
        return true;
    }

    /// <summary>The position on the page being dragged on, clamped to the page.</summary>
    private Point LocateOnDragPage(Point surface)
    {
        var rect = _layout[_dragPage];
        return new Point(Math.Clamp(surface.X - rect.X, 0, rect.Width) / Scale, Math.Clamp(surface.Y - rect.Y, 0, rect.Height) / Scale);
    }

    private PdfPoint ToPagePoint(int page, Point view) => TryGeometry(page)!.ToPage(view);

    // ------------------------------------------------------------------ mouse down

    private void OnMouseDown(object sender, MouseButtonEventArgs e) => Observe(PointerDownAsync(e.GetPosition(_surface), e.ClickCount));

    /// <summary>Awaits a pointer action so that a failure is reported in the status bar instead of vanishing.</summary>
    private async void Observe(Task action)
    {
        try
        {
            await action;
        }
        catch (Exception ex)
        {
            CancelDrag();
            Say("That did not work: " + ex.Message);
        }
    }

    /// <summary>The left button went down at a position on the page surface. Everything a tool does starts here.</summary>
    private async Task PointerDownAsync(Point surface, int clicks)
    {
        if (Session is null) return;
        Focus();
        EndEditors();
        if (!Locate(surface, out var page, out var view) || TryGeometry(page) is null)
        {
            ClearSelection();
            if (_tool == ViewerTool.Hand) BeginPan(surface);
            return;
        }
        _dragPage = page;
        _dragStart = _dragNow = view;
        _pressedLink = null;

        if (_tool == ViewerTool.Hand)
        {
            BeginPan(surface);
            return;
        }
        if (_tool == ViewerTool.Select)
        {
            BeginSelect(clicks, page, view);
            return;
        }
        if (Session.IsRestricted && _tool is not (ViewerTool.Snapshot or ViewerTool.Measure))
        {
            Say("This document is protected against changes. Use Protect > Unlock to enter the permissions password.");
            return;
        }
        if (_tool.IsTextMarkup())
        {
            BeginTextDrag(page, view, tolerance: 2000);
        }
        else if (_tool.IsRectangleDrag())
        {
            BeginDrag(DragKind.Rectangle);
        }
        else if (_tool.IsLineDrag())
        {
            _measurement = null;
            BeginDrag(DragKind.Line);
        }
        else if (_tool == ViewerTool.Ink)
        {
            _ink.Clear();
            _ink.Add(view);
            BeginDrag(DragKind.Ink);
        }
        else if (_tool == ViewerTool.Note)
        {
            await AddNoteAsync(page, view);
        }
        else if (_tool == ViewerTool.AddText)
        {
            BeginAddText(page, view);
        }
        else if (_tool == ViewerTool.EditObjects)
        {
            BeginEditObject(clicks, page, view);
        }
    }

    /// <summary>True while a debug-build test drives the pointer methods directly; the real mouse is then left alone.</summary>
    private bool _synthetic = false;

    private void BeginDrag(DragKind kind)
    {
        _drag = kind;
        if (!_synthetic) _surface.CaptureMouse();
        InvalidateOverlay(_dragPage);
    }

    /// <summary>A surface position expressed relative to the visible viewport.</summary>
    private Point InViewport(Point surface) => new(surface.X - _scroll.HorizontalOffset, surface.Y - _scroll.VerticalOffset);

    private void BeginPan(Point surface)
    {
        _drag = DragKind.Pan;
        _panMouse = InViewport(surface);
        _panOffset = new Point(_scroll.HorizontalOffset, _scroll.VerticalOffset);
        _surface.CaptureMouse();
        Cursor = Cursors.ScrollAll;
    }

    private void BeginSelect(int clicks, int page, Point view)
    {
        var point = ToPagePoint(page, view);
        if (FieldAt(page, point) is { } field && !Session!.IsRestricted)
        {
            ClearSelection();
            ActivateField(field);
            return;
        }
        if (AnnotationAt(page, point) is { } annotation)
        {
            if (clicks == 2)
            {
                _ = EditAnnotationTextAsync(annotation);
                return;
            }
            _textSelection = null;
            _selectedAnnotation = (page, annotation.Index);
            InvalidateOverlays();
            SelectionChanged?.Invoke();
            if (!Session!.IsRestricted) BeginDrag(DragKind.MoveAnnotation);
            return;
        }
        _selectedAnnotation = null;
        var text = Session!.Document.GetText(page);
        var index = text.IndexAt(point, 4);
        if (index >= 0)
        {
            if (clicks >= 2)
            {
                var (start, length) = text.WordAt(index);
                _textSelection = (page, start, start + Math.Max(0, length - 1));
                _wordSelected = true;
                InvalidateOverlays();
                SelectionChanged?.Invoke();
                return;
            }
            BeginTextDrag(page, view, tolerance: 4);
            return;
        }
        _pressedLink = Session.Document.GetLinks(page).FirstOrDefault(l => l.Rect.Contains(point));
        ClearSelection();
    }

    private void BeginTextDrag(int page, Point view, double tolerance)
    {
        var index = Session!.Document.GetText(page).IndexAt(ToPagePoint(page, view), tolerance);
        _selectedAnnotation = null;
        _wordSelected = false;
        _textSelection = index < 0 ? null : (page, index, index);
        if (index >= 0) BeginDrag(DragKind.Text);
        else InvalidateOverlays();
    }

    // ------------------------------------------------------------------ mouse move

    private void OnMouseMove(object sender, MouseEventArgs e) => PointerMove(e.GetPosition(_surface));

    private void PointerMove(Point surface)
    {
        if (Session is null) return;
        if (_drag == DragKind.None)
        {
            UpdateHover(surface);
            return;
        }
        if (_drag == DragKind.Pan)
        {
            var now = InViewport(surface);
            _scroll.ScrollToHorizontalOffset(_panOffset.X - (now.X - _panMouse.X));
            _scroll.ScrollToVerticalOffset(_panOffset.Y - (now.Y - _panMouse.Y));
            return;
        }
        _dragNow = LocateOnDragPage(surface);
        if (_drag == DragKind.Text && _textSelection is { } selection)
        {
            var index = Session.Document.GetText(_dragPage).IndexAt(ToPagePoint(_dragPage, _dragNow), 4000);
            if (index >= 0) _textSelection = (selection.Page, selection.Anchor, index);
        }
        else if (_drag == DragKind.Ink)
        {
            if ((_ink[^1] - _dragNow).Length > 0.7) _ink.Add(_dragNow);
        }
        else if (_drag == DragKind.Line && _tool == ViewerTool.Measure)
        {
            Say(DescribeDistance(_dragStart, _dragNow));
        }
        InvalidateOverlay(_dragPage);
    }

    private void UpdateHover(Point surface)
    {
        if (!Locate(surface, out var page, out var view) || TryGeometry(page) is null)
        {
            Cursor = _tool == ViewerTool.Hand ? Cursors.Hand : null;
            return;
        }
        var point = ToPagePoint(page, view);
        switch (_tool)
        {
            case ViewerTool.Hand:
                Cursor = Cursors.Hand;
                break;
            case ViewerTool.Select:
                if (FieldAt(page, point) is { ReadOnly: false }) Cursor = Cursors.Hand;
                else if (AnnotationAt(page, point) is not null) Cursor = Cursors.SizeAll;
                else if (Session!.Document.GetLinks(page).Any(l => l.Rect.Contains(point))) Cursor = Cursors.Hand;
                else Cursor = Session.Document.GetText(page).IndexAt(point, 3) >= 0 ? Cursors.IBeam : null;
                break;
            case ViewerTool.EditObjects:
                var hit = ObjectAt(page, point);
                var hover = hit is null ? ((int, int)?)null : (page, hit.Index);
                if (hover != _hoverObject)
                {
                    _hoverObject = hover;
                    InvalidateOverlays();
                }
                Cursor = OverResizeHandle(page, view) ? Cursors.SizeNWSE : hit is null ? null : Cursors.SizeAll;
                break;
            case var tool when tool.IsTextMarkup():
                Cursor = Cursors.IBeam;
                break;
            case ViewerTool.AddText:
                Cursor = Cursors.IBeam;
                break;
            case ViewerTool.Ink:
                Cursor = Cursors.Pen;
                break;
            default:
                Cursor = Cursors.Cross;
                break;
        }
    }

    // ------------------------------------------------------------------ mouse up

    private void OnMouseUp(object sender, MouseButtonEventArgs e) => Observe(PointerUpAsync());

    /// <summary>The left button was released: finish whatever drag was in progress.</summary>
    private async Task PointerUpAsync()
    {
        if (Session is null) return;
        var kind = _drag;
        _drag = DragKind.None;
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        var page = _dragPage;
        switch (kind)
        {
            case DragKind.Pan:
                Cursor = Cursors.Hand;
                break;
            case DragKind.Text:
                await FinishTextDragAsync();
                break;
            case DragKind.Rectangle:
                await FinishRectangleAsync(page, new Rect(_dragStart, _dragNow));
                break;
            case DragKind.Line:
                await FinishLineAsync(page);
                break;
            case DragKind.Ink:
                await FinishInkAsync(page);
                break;
            case DragKind.MoveAnnotation:
                await FinishMoveAnnotationAsync();
                break;
            case DragKind.MoveObject:
                await FinishMoveObjectAsync();
                break;
            case DragKind.ResizeObject:
                await FinishResizeObjectAsync();
                break;
            case DragKind.None when _pressedLink is { } link:
                FollowLink(link);
                break;
        }
        _pressedLink = null;
        InvalidateOverlays();
    }

    private void CancelDrag()
    {
        if (_drag == DragKind.None) return;
        _drag = DragKind.None;
        _ink.Clear();
        InvalidateOverlays();
    }

    private void FollowLink(LinkInfo link)
    {
        if (link.PageIndex >= 0)
        {
            GoToPage(link.PageIndex, link.Top);
            return;
        }
        if (string.IsNullOrEmpty(link.Uri)) return;
        var isWeb = Uri.TryCreate(link.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";
        if (!isWeb)
        {
            Ui.Warn("This link points to something other than a web or e-mail address and was not opened:\n" + link.Uri);
            return;
        }
        if (Ui.Confirm("Open this link in your browser?\n\n" + link.Uri, "Open link")) Ui.ShellOpen(link.Uri);
    }

    private async Task FinishTextDragAsync()
    {
        if (SelectedRange() is not { } range)
        {
            _textSelection = null;
            return;
        }
        if (_tool == ViewerTool.Select)
        {
            SelectionChanged?.Invoke();
            return;
        }
        var rects = Session!.Document.GetText(range.Page).GetRangeRects(range.Start, range.Length);
        var tool = _tool;
        _textSelection = null;
        if (rects.Count == 0) return;
        if (tool == ViewerTool.RedactText)
        {
            await Commit("Mark for redaction", b => AnnotationService.AddRedactionMarks(b, range.Page, rects, AppSettings.Current.Author));
            return;
        }
        var kind = tool switch
        {
            ViewerTool.Underline => TextMarkupKind.Underline,
            ViewerTool.StrikeOut => TextMarkupKind.StrikeOut,
            ViewerTool.Squiggly => TextMarkupKind.Squiggly,
            _ => TextMarkupKind.Highlight,
        };
        await ApplyMarkupAsync(kind, range.Page, rects);
    }

    private Task<bool> ApplyMarkupAsync(TextMarkupKind kind, int page, IReadOnlyList<PdfRect> rects)
    {
        var style = CommentStyle(kind == TextMarkupKind.Highlight ? Options.HighlightColor : Options.Color);
        return Commit(kind.ToString(), b => AnnotationService.AddTextMarkup(b, page, kind, rects, style));
    }

    /// <summary>Marks up the current text selection (used by the ribbon and the context menu).</summary>
    public async Task MarkupSelectionAsync(TextMarkupKind kind)
    {
        if (Session is null || SelectedRange() is not { } range) return;
        var rects = Session.Document.GetText(range.Page).GetRangeRects(range.Start, range.Length);
        if (rects.Count > 0) await ApplyMarkupAsync(kind, range.Page, rects);
    }

    public async Task RedactSelectionAsync()
    {
        if (Session is null || SelectedRange() is not { } range) return;
        var rects = Session.Document.GetText(range.Page).GetRangeRects(range.Start, range.Length);
        if (rects.Count > 0) await Commit("Mark for redaction", b => AnnotationService.AddRedactionMarks(b, range.Page, rects, AppSettings.Current.Author));
    }

    private async Task FinishRectangleAsync(int page, Rect view)
    {
        var geometry = TryGeometry(page);
        if (geometry is null) return;
        var tiny = view.Width < 4 || view.Height < 4;
        switch (_tool)
        {
            case ViewerTool.Snapshot when !tiny:
                CopySnapshot(page, view);
                break;
            case ViewerTool.Rectangle or ViewerTool.Ellipse when !tiny:
                var shape = _tool == ViewerTool.Rectangle ? ShapeKind.Rectangle : ShapeKind.Ellipse;
                await Commit(shape.ToString(), b => AnnotationService.AddShape(b, page, shape, geometry.ToPage(view), CommentStyle(fill: Options.Fill)));
                break;
            case ViewerTool.TextBox:
                await AddTextBoxAsync(page, tiny ? new Rect(view.X, view.Y, 180, 60) : view, geometry);
                break;
            case ViewerTool.Stamp:
                await AddStampAsync(page, tiny ? new Rect(view.X - 80, view.Y - 25, 160, 50) : view, geometry);
                break;
            case ViewerTool.PlaceImage:
                await PlaceImageAsync(page, view, tiny, geometry);
                break;
            case ViewerTool.Whiteout when !tiny:
                await Commit("Whiteout", b => ContentService.FillRectangle(b, page, view, 0xFFFFFF));
                break;
            case ViewerTool.RedactArea when !tiny:
                await Commit("Mark for redaction", b => AnnotationService.AddRedactionMarks(b, page, [geometry.ToPage(view)], AppSettings.Current.Author));
                break;
            case ViewerTool.Crop when !tiny:
                await CropAsync(page, view);
                break;
            case ViewerTool.AddLink when !tiny:
                await AddLinkAsync(page, geometry.ToPage(view));
                break;
            case ViewerTool.CertificateArea when !tiny:
                CertificateAreaChosen?.Invoke(page, geometry.ToPage(view));
                break;
            case var tool when tool.IsFormDesign():
                await AddFieldAsync(tool, page, view, tiny, geometry);
                break;
        }
    }

    private void CopySnapshot(int page, Rect view)
    {
        const double pixelsPerPoint = 3;
        var geometry = TryGeometry(page)!;
        var fullWidth = (int)Math.Round(geometry.ViewWidth * pixelsPerPoint);
        var fullHeight = (int)Math.Round(geometry.ViewHeight * pixelsPerPoint);
        var x = (int)Math.Round(view.X * pixelsPerPoint);
        var y = (int)Math.Round(view.Y * pixelsPerPoint);
        var width = Math.Max(1, (int)Math.Round(view.Width * pixelsPerPoint));
        var height = Math.Max(1, (int)Math.Round(view.Height * pixelsPerPoint));
        var pixels = Session!.Document.RenderRegion(page, fullWidth, fullHeight, x, y, width, height, PageRenderOptions.Default);
        Clipboard.SetImage(PdfiumDocument.ToBitmap(pixels, width, height, 96));
        Say($"Copied a {width} × {height} pixel picture to the clipboard.");
    }

    private async Task AddNoteAsync(int page, Point view)
    {
        var text = Ui.Prompt("Sticky note", "Note", "", multiline: true);
        if (string.IsNullOrWhiteSpace(text)) return;
        var at = ToPagePoint(page, view);
        await Commit("Sticky note", b => AnnotationService.AddNote(b, page, at, text, CommentStyle(Options.HighlightColor)));
    }

    private async Task AddTextBoxAsync(int page, Rect view, PageGeometry geometry)
    {
        var text = Ui.Prompt("Text box", "Text", "", multiline: true);
        if (string.IsNullOrWhiteSpace(text)) return;
        var style = new AnnotStyle(Options.Color, 1, 1, Options.Fill, Options.FontSize, AppSettings.Current.Author);
        await Commit("Text box", b => AnnotationService.AddFreeText(b, page, geometry.ToPage(view), text, style));
    }

    private async Task AddStampAsync(int page, Rect view, PageGeometry geometry)
    {
        var subtitle = Options.StampWithDate ? $"{AppSettings.Current.Author}, {DateTime.Now:yyyy-MM-dd HH:mm}" : "";
        var style = new AnnotStyle(Options.StampColor, Options.Opacity, 2, null, 12, AppSettings.Current.Author);
        await Commit("Stamp", b => AnnotationService.AddStamp(b, page, geometry.ToPage(view), Options.StampText, subtitle, style));
    }

    private async Task PlaceImageAsync(int page, Rect view, bool tiny, PageGeometry geometry)
    {
        if (Options.Image is not { } image) return;
        if (tiny)
        {
            var width = Math.Min(180, geometry.ViewWidth / 3);
            view = new Rect(view.X, view.Y, width, width / Math.Max(0.05, Options.ImageAspect));
        }
        else
        {
            // Keep the picture's proportions inside the dragged box.
            var height = Math.Min(view.Height, view.Width / Math.Max(0.05, Options.ImageAspect));
            view = new Rect(view.X, view.Y, height * Options.ImageAspect, height);
        }
        bool done;
        if (Options.ImageAsStamp)
        {
            BitmapSource bitmap = PageCanvasDecode(image);
            done = await Commit("Picture stamp", b => AnnotationService.AddImageStamp(b, page, geometry.ToPage(view), bitmap, CommentStyle()));
        }
        else
        {
            done = await Commit("Place picture", b => ContentService.AddImage(b, page, view, image));
        }
        if (done) Tool = ViewerTool.Select;
    }

    private static BitmapSource PageCanvasDecode(byte[] image)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(image), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private async Task CropAsync(int page, Rect view)
    {
        var dialog = new FormDialog("Crop pages", Window.GetWindow(this));
        dialog.AddNote("Cropping hides everything outside the area you dragged. The hidden content stays in the file and can be restored with Remove Crop.");
        var scope = dialog.AddChoice("Apply to", ["This page", "All pages"], 0);
        if (!dialog.Ask()) return;
        IEnumerable<int> pages = scope.Value == 0 ? [page] : Enumerable.Range(0, PageCount);
        await Commit("Crop", b => PageService.Crop(b, pages, view), ChangeKind.Structure);
    }

    private async Task AddLinkAsync(int page, PdfRect rect)
    {
        var dialog = new FormDialog("Add link", Window.GetWindow(this));
        var kind = dialog.AddChoice("Link to", ["A web address", "A page in this document"], 0);
        var url = dialog.AddText("Web address", "https://");
        var target = dialog.AddNumber("Page number", CurrentPage + 1, 1, PageCount);
        if (!dialog.Ask()) return;
        if (kind.Value == 0)
        {
            if (!Uri.TryCreate(url.Value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
            {
                Ui.Warn("Enter a full web address such as https://example.com.");
                return;
            }
            await Commit("Add link", b => AnnotationService.AddLink(b, page, rect, url.Value));
        }
        else
        {
            var index = (int)target.Value - 1;
            await Commit("Add link", b => AnnotationService.AddLink(b, page, rect, null, index));
        }
    }

    private async Task AddFieldAsync(ViewerTool tool, int page, Rect view, bool tiny, PageGeometry geometry)
    {
        var type = tool switch
        {
            ViewerTool.FieldCheck => FormFieldType.CheckBox,
            ViewerTool.FieldRadio => FormFieldType.Radio,
            ViewerTool.FieldCombo => FormFieldType.ComboBox,
            ViewerTool.FieldList => FormFieldType.ListBox,
            ViewerTool.FieldSignature => FormFieldType.Signature,
            _ => FormFieldType.Text,
        };
        if (tiny)
        {
            view = type is FormFieldType.CheckBox or FormFieldType.Radio ? new Rect(view.X, view.Y, 14, 14) : new Rect(view.X, view.Y, 180, 22);
        }
        var patterns = tool switch
        {
            ViewerTool.FieldDate => DateFieldFormat.DatePatterns,
            ViewerTool.FieldDateTime => DateFieldFormat.DateTimePatterns,
            _ => null,
        };
        var dialog = new FormDialog("New form field", Window.GetWindow(this));
        var suggested = type == FormFieldType.Radio ? "Group1" : $"{(patterns is null ? type.ToString() : "Date")}{_fields.Count + 1}";
        var name = dialog.AddText(type == FormFieldType.Radio ? "Group name" : "Field name", suggested);
        Field<string>? choices = null;
        if (type is FormFieldType.ComboBox or FormFieldType.ListBox) choices = dialog.AddText("Choices (one per line)", "Option 1\nOption 2", multiline: true);
        if (type == FormFieldType.Radio) choices = dialog.AddText("This button's value", $"Choice{_fields.Count(f => f.Type == FormFieldType.Radio) + 1}");
        var pattern = patterns is null ? null : AddDateFormatChoice(dialog, patterns);
        dialog.Validate(() => string.IsNullOrWhiteSpace(name.Value) ? "Enter a name." : null);
        if (!dialog.Ask()) return;
        var options = choices?.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var rect = geometry.ToPage(view);
        var fieldName = name.Value.Trim();
        var dateFormat = pattern is null ? null : new DateFieldFormat(patterns![pattern.Value]);
        await Commit("Add form field", b => FormService.AddField(b, page, rect, type, fieldName, options, dateFormat));
    }

    /// <summary>The "Format" row of a new date field: each pattern with an example, starting on the one closest to the regional settings.</summary>
    private static Field<int> AddDateFormatChoice(FormDialog dialog, IReadOnlyList<string> patterns)
    {
        var regional = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
        var leading = regional.StartsWith('M') ? "mm/" : regional.StartsWith('y') ? "yyyy" : "dd/";
        var preferred = Math.Max(0, patterns.ToList().FindIndex(p => p.StartsWith(leading, StringComparison.Ordinal)));
        return dialog.AddChoice("Format", patterns.Select(p => $"{new DateFieldFormat(p).Example}   ({p})").ToList(), preferred);
    }

    private async Task FinishLineAsync(int page)
    {
        if ((_dragNow - _dragStart).Length < 3) return;
        if (_tool == ViewerTool.Measure)
        {
            _measurement = (page, _dragStart, _dragNow);
            Say("Distance: " + DescribeDistance(_dragStart, _dragNow));
            return;
        }
        var from = ToPagePoint(page, _dragStart);
        var to = ToPagePoint(page, _dragNow);
        var arrow = _tool == ViewerTool.Arrow;
        await Commit(arrow ? "Arrow" : "Line", b => AnnotationService.AddLine(b, page, from, to, arrow, CommentStyle()));
    }

    private async Task FinishInkAsync(int page)
    {
        if (_ink.Count == 0) return;
        var stroke = _ink.Select(p => ToPagePoint(page, p)).ToList();
        _ink.Clear();
        await Commit("Drawing", b => AnnotationService.AddInk(b, page, [stroke], CommentStyle()));
    }

    // ------------------------------------------------------------------ comments

    private AnnotationInfo? AnnotationAt(int page, PdfPoint point)
    {
        var all = AnnotationsOf(page);
        for (var i = all.Count - 1; i >= 0; i--)
        {
            var a = all[i];
            if (a.IsMarkup && !a.Hidden && a.Rect.Inflate(1.5).Contains(point)) return a;
        }
        return null;
    }

    private async Task FinishMoveAnnotationAsync()
    {
        if (_selectedAnnotation is not { } selected || (_dragNow - _dragStart).Length < 2) return;
        var from = ToPagePoint(selected.Page, _dragStart);
        var to = ToPagePoint(selected.Page, _dragNow);
        var kept = selected;
        if (await Commit("Move comment", b => AnnotationService.Move(b, kept.Page, kept.Index, to.X - from.X, to.Y - from.Y)))
        {
            _selectedAnnotation = kept;
            InvalidateOverlays();
        }
    }

    private async Task EditAnnotationTextAsync(AnnotationInfo annotation)
    {
        if (Session is null || Session.IsRestricted) return;
        var text = Ui.Prompt($"{annotation.DisplayType} — page {annotation.PageIndex + 1}", "Text", annotation.Contents, multiline: true);
        if (text is null || text == annotation.Contents) return;
        await Commit("Edit comment", b => AnnotationService.SetContents(b, annotation.PageIndex, annotation.Index, text));
    }

    public Task EditSelectedAnnotationAsync() => SelectedAnnotation is { } annotation ? EditAnnotationTextAsync(annotation) : Task.CompletedTask;

    /// <summary>Deletes the selected comment or content object.</summary>
    public async Task DeleteSelectionAsync()
    {
        if (_selectedAnnotation is { } annotation)
        {
            await Commit("Delete comment", b => AnnotationService.Delete(b, annotation.Page, annotation.Index));
        }
        else if (_selectedObject is { } obj)
        {
            await Commit("Delete object", b => PdfiumOps.RemoveObjects(b, obj.Page, [obj.Index]));
        }
    }

    // ------------------------------------------------------------------ keyboard, wheel, context menu

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Session is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        SetZoom(_zoom * factor, e.GetPosition(_scroll));
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Session is null || e.OriginalSource is TextBox or ComboBox) return;
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Delete or Key.Back when _selectedAnnotation is not null || _selectedObject is not null:
                await DeleteSelectionAsync();
                break;
            case Key.Escape:
                CancelDrag();
                ClearSelection();
                _measurement = null;
                if (_tool != ViewerTool.Select) Tool = ViewerTool.Select;
                break;
            case Key.C when control:
                CopySelection();
                break;
            case Key.A when control:
                SelectAllOnPage();
                break;
            case Key.PageDown or Key.Space:
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + _scroll.ViewportHeight * 0.9);
                break;
            case Key.PageUp:
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - _scroll.ViewportHeight * 0.9);
                break;
            case Key.Down:
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 60);
                break;
            case Key.Up:
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 60);
                break;
            case Key.Right when !control:
                GoToPage(CurrentPage + 1);
                break;
            case Key.Left when !control:
                GoToPage(CurrentPage - 1);
                break;
            case Key.Home:
                GoToPage(0);
                break;
            case Key.End:
                GoToPage(PageCount - 1);
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Session is null || !Locate(e.GetPosition(_surface), out var page, out var view) || TryGeometry(page) is null) return;
        var point = ToPagePoint(page, view);
        var menu = new ContextMenu();
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        var editable = !Session.IsRestricted;
        if (AnnotationAt(page, point) is { } annotation)
        {
            _selectedAnnotation = (page, annotation.Index);
            InvalidateOverlays();
            Add($"Edit {annotation.DisplayType.ToLowerInvariant()} text…", () => _ = EditAnnotationTextAsync(annotation), editable);
            Add("Delete", () => _ = DeleteSelectionAsync(), editable);
            menu.Items.Add(new Separator());
        }
        if (FieldAt(page, point) is { } field)
        {
            Add($"Delete field \"{field.Name}\"", () => _ = Commit("Delete form field", b => FormService.DeleteField(b, field.PageIndex, field.AnnotIndex)), editable);
            menu.Items.Add(new Separator());
        }
        var hasText = HasTextSelection;
        Add("Copy", CopySelection, hasText);
        Add("Highlight selection", () => _ = MarkupSelectionAsync(TextMarkupKind.Highlight), hasText && editable);
        Add("Underline selection", () => _ = MarkupSelectionAsync(TextMarkupKind.Underline), hasText && editable);
        Add("Strike through selection", () => _ = MarkupSelectionAsync(TextMarkupKind.StrikeOut), hasText && editable);
        Add("Mark selection for redaction", () => _ = RedactSelectionAsync(), hasText && editable);
        menu.Items.Add(new Separator());
        Add("Select all text on this page", SelectAllOnPage);
        Add("Add sticky note here…", () => _ = AddNoteAsync(page, view), editable);
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
