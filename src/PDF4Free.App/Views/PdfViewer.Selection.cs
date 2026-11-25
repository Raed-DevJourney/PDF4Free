using System.Windows;
using System.Windows.Media;
using PDF4Free.App.Services;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;

namespace PDF4Free.App.Views;

/// <summary>Selection state, per-page caches and the overlay painting of <see cref="PdfViewer"/>.</summary>
public sealed partial class PdfViewer
{
    private readonly Dictionary<int, List<AnnotationInfo>> _annotations = [];
    private readonly Dictionary<int, List<PageObjectInfo>> _objects = [];
    private List<FormFieldInfo> _fields = [];
    private ViewerTool _tool = ViewerTool.Select;

    private (int Page, int Anchor, int Caret)? _textSelection;
    private (int Page, int Index)? _selectedAnnotation;
    private (int Page, int Index)? _selectedObject;
    private (int Page, int Index)? _hoverObject;
    private IReadOnlyList<SearchHit> _hits = [];
    private int _currentHit = -1;
    private (int Page, Point From, Point To)? _measurement;

    private static readonly Brush SelectionBrush = Frozen(Color.FromArgb(90, 0, 110, 230));
    private static readonly Brush HitBrush = Frozen(Color.FromArgb(110, 255, 214, 0));
    private static readonly Brush CurrentHitBrush = Frozen(Color.FromArgb(150, 255, 128, 0));
    private static readonly Brush FieldBrush = Frozen(Color.FromArgb(38, 30, 120, 255));
    private static readonly Pen FramePen = FrozenPen(Color.FromRgb(0, 110, 230), 1.4, dashed: true);
    private static readonly Pen HoverPen = FrozenPen(Color.FromArgb(160, 0, 110, 230), 1, dashed: false);
    private static readonly Pen MeasurePen = FrozenPen(Color.FromRgb(216, 27, 96), 1.5, dashed: false);
    private static readonly Brush HandleBrush = Frozen(Color.FromRgb(0, 110, 230));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double width, bool dashed)
    {
        var pen = new Pen(new SolidColorBrush(color), width);
        if (dashed) pen.DashStyle = new DashStyle([4, 3], 0);
        pen.Freeze();
        return pen;
    }

    private static Color ToColor(uint rgb, byte alpha = 255) => Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ------------------------------------------------------------------ tool

    public ViewerTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            EndEditors();
            _tool = value;
            _hoverObject = null;
            if (value != ViewerTool.EditObjects) _selectedObject = null;
            if (value != ViewerTool.Measure) _measurement = null;
            Cursor = null;
            InvalidateOverlays();
            Say(value.Hint());
            ToolChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------------ caches

    private List<AnnotationInfo> AnnotationsOf(int page)
    {
        if (_annotations.TryGetValue(page, out var cached)) return cached;
        try
        {
            return _annotations[page] = Session!.Document.GetAnnotations(page);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ArgumentOutOfRangeException)
        {
            return [];
        }
    }

    private List<PageObjectInfo> ObjectsOf(int page)
    {
        if (_objects.TryGetValue(page, out var cached)) return cached;
        try
        {
            return _objects[page] = Session!.Document.GetPageObjects(page);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ArgumentOutOfRangeException)
        {
            return [];
        }
    }

    public IReadOnlyList<FormFieldInfo> Fields => _fields;

    private async Task ReloadFormFieldsAsync()
    {
        var session = Session;
        if (session is null || session.IsRestricted)
        {
            _fields = [];
            return;
        }
        var version = _version;
        var bytes = session.Bytes;
        List<FormFieldInfo> fields;
        try
        {
            fields = await Task.Run(() => HasWidgets(session) ? FormService.ReadFields(bytes) : []);
        }
        catch (Exception)
        {
            fields = []; // A form we cannot parse simply is not fillable here; viewing is unaffected.
        }
        if (version != _version) return;
        _fields = fields;
        InvalidateOverlays();
        FieldsChanged?.Invoke();
    }

    /// <summary>Raised after the list of form fields was re-read.</summary>
    public event Action? FieldsChanged;

    private static bool HasWidgets(PDF4Free.Core.PdfSession session)
    {
        try
        {
            var doc = session.Document;
            for (var p = 0; p < doc.PageCount; p++)
            {
                if (doc.GetAnnotations(p).Any(a => a.Subtype == "Widget")) return true;
            }
        }
        catch (ObjectDisposedException)
        {
            // Replaced by a newer version; that version triggers its own reload.
        }
        return false;
    }

    // ------------------------------------------------------------------ selection API

    public bool HasTextSelection => SelectedRange() is not null;

    /// <summary>True when a single-character selection is intentional (double-click on a one-letter word, Select All).</summary>
    private bool _wordSelected;

    private (int Page, int Start, int Length)? SelectedRange()
    {
        if (_textSelection is not { } s) return null;
        var start = Math.Min(s.Anchor, s.Caret);
        var length = Math.Abs(s.Anchor - s.Caret) + 1;
        if (s.Anchor == s.Caret && !_wordSelected) return null;
        return (s.Page, start, length);
    }

    public string SelectedText
    {
        get
        {
            if (Session is null || SelectedRange() is not { } range) return "";
            return Session.Document.GetText(range.Page).Substring(range.Start, range.Length);
        }
    }

    public AnnotationInfo? SelectedAnnotation =>
        _selectedAnnotation is { } s ? AnnotationsOf(s.Page).FirstOrDefault(a => a.Index == s.Index) : null;

    public bool HasSelectedObject => _selectedObject is not null;

    public void ClearSelection(bool silent = false)
    {
        var had = _textSelection is not null || _selectedAnnotation is not null || _selectedObject is not null;
        _textSelection = null;
        _wordSelected = false;
        _selectedAnnotation = null;
        _selectedObject = null;
        if (!had) return;
        InvalidateOverlays();
        if (!silent) SelectionChanged?.Invoke();
    }

    public void SelectAllOnPage()
    {
        if (Session is null) return;
        var text = Session.Document.GetText(CurrentPage);
        if (text.Length == 0) return;
        _textSelection = (CurrentPage, 0, text.Length - 1);
        _wordSelected = true;
        InvalidateOverlays();
        SelectionChanged?.Invoke();
    }

    public void CopySelection()
    {
        if (Session is null || SelectedText is not { Length: > 0 } text) return;
        if (!Session.Document.Security.CanCopy)
        {
            Say("This document does not allow copying text.");
            return;
        }
        Clipboard.SetText(text);
        Say($"Copied {text.Length} characters.");
    }

    public void SelectAnnotation(int page, int index)
    {
        ClearSelection(silent: true);
        _selectedAnnotation = (page, index);
        if (AnnotationsOf(page).FirstOrDefault(a => a.Index == index) is { } annotation) BringIntoView(page, annotation.Rect);
        InvalidateOverlays();
        SelectionChanged?.Invoke();
    }

    public void SetSearchHits(IReadOnlyList<SearchHit> hits, int current)
    {
        _hits = hits;
        _currentHit = current;
        if (current >= 0 && current < hits.Count) BringIntoView(hits[current].PageIndex, hits[current].Rects[0]);
        InvalidateOverlays();
    }

    // ------------------------------------------------------------------ overlay

    private Rect ToDip(Rect view) => new(view.X * Scale, view.Y * Scale, view.Width * Scale, view.Height * Scale);

    private Point ToDip(Point view) => new(view.X * Scale, view.Y * Scale);

    private void PaintOverlay(DrawingContext dc, int pageIndex)
    {
        if (Session is null || TryGeometry(pageIndex) is not { } geometry) return;
        PaintFields(dc, pageIndex, geometry);
        PaintSearchHits(dc, pageIndex, geometry);
        PaintTextSelection(dc, pageIndex, geometry);
        PaintSelectedAnnotation(dc, pageIndex, geometry);
        PaintObjects(dc, pageIndex, geometry);
        PaintDrag(dc, pageIndex);
        PaintMeasurement(dc, pageIndex);
    }

    private void PaintFields(DrawingContext dc, int pageIndex, PageGeometry geometry)
    {
        if (_tool != ViewerTool.Select && !_tool.IsFormDesign() || !AppSettings.Current.HighlightFormFields) return;
        foreach (var field in _fields)
        {
            if (field.PageIndex != pageIndex || field.ReadOnly || field.Type is FormFieldType.Button or FormFieldType.Unknown) continue;
            dc.DrawRectangle(FieldBrush, null, ToDip(geometry.ToView(field.Rect)));
        }
    }

    private void PaintSearchHits(DrawingContext dc, int pageIndex, PageGeometry geometry)
    {
        for (var i = 0; i < _hits.Count; i++)
        {
            if (_hits[i].PageIndex != pageIndex) continue;
            foreach (var rect in _hits[i].Rects) dc.DrawRectangle(i == _currentHit ? CurrentHitBrush : HitBrush, null, ToDip(geometry.ToView(rect)));
        }
    }

    private void PaintTextSelection(DrawingContext dc, int pageIndex, PageGeometry geometry)
    {
        if (SelectedRange() is not { } range || range.Page != pageIndex) return;
        var brush = _tool switch
        {
            ViewerTool.Highlight => Frozen(ToColor(Options.HighlightColor, 120)),
            ViewerTool.RedactText => Frozen(Color.FromArgb(120, 229, 57, 53)),
            ViewerTool.Underline or ViewerTool.StrikeOut or ViewerTool.Squiggly => Frozen(ToColor(Options.Color, 70)),
            _ => SelectionBrush,
        };
        foreach (var rect in Session!.Document.GetText(pageIndex).GetRangeRects(range.Start, range.Length))
        {
            dc.DrawRectangle(brush, null, ToDip(geometry.ToView(rect)));
        }
    }

    private void PaintSelectedAnnotation(DrawingContext dc, int pageIndex, PageGeometry geometry)
    {
        if (_selectedAnnotation is not { } selected || selected.Page != pageIndex || SelectedAnnotation is not { } annotation) return;
        var rect = ToDip(geometry.ToView(annotation.Rect));
        if (_drag == DragKind.MoveAnnotation) rect.Offset((_dragNow.X - _dragStart.X) * Scale, (_dragNow.Y - _dragStart.Y) * Scale);
        rect.Inflate(2, 2);
        dc.DrawRectangle(null, FramePen, rect);
    }

    private void PaintObjects(DrawingContext dc, int pageIndex, PageGeometry geometry)
    {
        if (_tool != ViewerTool.EditObjects) return;
        if (_hoverObject is { } hover && hover.Page == pageIndex && _hoverObject != _selectedObject && ObjectsOf(pageIndex).FirstOrDefault(o => o.Index == hover.Index) is { } hovered)
        {
            dc.DrawRectangle(null, HoverPen, ToDip(geometry.ToView(hovered.Bounds)));
        }
        if (_selectedObject is not { } selected || selected.Page != pageIndex || ObjectsOf(pageIndex).FirstOrDefault(o => o.Index == selected.Index) is not { } obj) return;
        var rect = ToDip(geometry.ToView(obj.Bounds));
        if (_drag == DragKind.MoveObject) rect.Offset((_dragNow.X - _dragStart.X) * Scale, (_dragNow.Y - _dragStart.Y) * Scale);
        if (_drag == DragKind.ResizeObject)
        {
            var factor = ResizeFactor(geometry.ToView(obj.Bounds));
            rect = new Rect(rect.X, rect.Y, rect.Width * factor, rect.Height * factor);
        }
        dc.DrawRectangle(null, FramePen, rect);
        dc.DrawRectangle(HandleBrush, null, new Rect(rect.Right - 4, rect.Bottom - 4, 8, 8));
    }

    private void PaintDrag(DrawingContext dc, int pageIndex)
    {
        if (_dragPage != pageIndex) return;
        switch (_drag)
        {
            case DragKind.Rectangle:
                var rect = ToDip(new Rect(_dragStart, _dragNow));
                if (_tool == ViewerTool.Ellipse) dc.DrawEllipse(null, ToolPen(), new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
                else if (_tool == ViewerTool.Rectangle) dc.DrawRectangle(Options.Fill is { } f ? Frozen(ToColor(f, 120)) : null, ToolPen(), rect);
                else if (_tool is ViewerTool.RedactArea) dc.DrawRectangle(Frozen(Color.FromArgb(110, 229, 57, 53)), null, rect);
                else if (_tool is ViewerTool.Whiteout) dc.DrawRectangle(Brushes.White, FramePen, rect);
                else dc.DrawRectangle(Frozen(Color.FromArgb(28, 0, 110, 230)), FramePen, rect);
                break;
            case DragKind.Line:
                dc.DrawLine(_tool == ViewerTool.Measure ? MeasurePen : ToolPen(), ToDip(_dragStart), ToDip(_dragNow));
                break;
            case DragKind.Ink when _ink.Count > 1:
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(ToDip(_ink[0]), false, false);
                    context.PolyLineTo([.. _ink.Skip(1).Select(ToDip)], true, true);
                }
                var pen = ToolPen();
                dc.DrawGeometry(null, pen, geometry);
                break;
        }
    }

    private Pen ToolPen()
    {
        var pen = new Pen(new SolidColorBrush(ToColor(Options.Color, (byte)Math.Round(Options.Opacity * 255))), Math.Max(1, Options.LineWidth * Scale))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    private void PaintMeasurement(DrawingContext dc, int pageIndex)
    {
        if (_measurement is not { } m || m.Page != pageIndex) return;
        var from = ToDip(m.From);
        var to = ToDip(m.To);
        dc.DrawLine(MeasurePen, from, to);
        dc.DrawEllipse(MeasurePen.Brush, null, from, 3, 3);
        dc.DrawEllipse(MeasurePen.Brush, null, to, 3, 3);
        var label = new FormattedText(DescribeDistance(m.From, m.To), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, Brushes.White, _dpi);
        var at = new Point((from.X + to.X) / 2 + 8, (from.Y + to.Y) / 2 - 22);
        dc.DrawRoundedRectangle(MeasurePen.Brush, null, new Rect(at.X - 5, at.Y - 2, label.Width + 10, label.Height + 4), 3, 3);
        dc.DrawText(label, at);
    }

    private static string DescribeDistance(Point from, Point to)
    {
        var points = (to - from).Length;
        return $"{points / 72:0.00} in  ·  {points / 72 * 2.54:0.00} cm  ·  {points:0.#} pt";
    }
}
