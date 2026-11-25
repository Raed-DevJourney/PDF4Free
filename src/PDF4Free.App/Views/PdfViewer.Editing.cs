using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.App.Views;

/// <summary>Content-object editing, inline text editors and form filling of <see cref="PdfViewer"/>.</summary>
public sealed partial class PdfViewer
{
    private Action<bool>? _endEditor;
    private Task _fieldsReload = Task.CompletedTask;

    // ------------------------------------------------------------------ content objects (Edit tool)

    private PageObjectInfo? ObjectAt(int page, PdfPoint point)
    {
        if (TryGeometry(page) is not { } geometry) return null;
        var pageArea = geometry.Box.Width * geometry.Box.Height;
        return ObjectsOf(page)
            .Where(o => o.Bounds.Inflate(1).Contains(point))
            .OrderBy(o => o.Bounds.Width * o.Bounds.Height > pageArea * 0.85 ? 1 : 0)
            .ThenBy(o => o.Bounds.Width * o.Bounds.Height)
            .FirstOrDefault();
    }

    private PageObjectInfo? SelectedObjectInfo(int page) =>
        _selectedObject is { } s && s.Page == page ? ObjectsOf(page).FirstOrDefault(o => o.Index == s.Index) : null;

    private bool OverResizeHandle(int page, Point view)
    {
        if (SelectedObjectInfo(page) is not { } obj || TryGeometry(page) is not { } geometry) return false;
        var bounds = geometry.ToView(obj.Bounds);
        var reach = 7 / Scale;
        return Math.Abs(view.X - bounds.Right) <= reach && Math.Abs(view.Y - bounds.Bottom) <= reach;
    }

    private double ResizeFactor(Rect viewBounds)
    {
        var width = Math.Max(4, viewBounds.Width + (_dragNow.X - _dragStart.X));
        return Math.Clamp(width / Math.Max(0.5, viewBounds.Width), 0.05, 20);
    }

    private void BeginEditObject(int clicks, int page, Point view)
    {
        if (OverResizeHandle(page, view))
        {
            BeginDrag(DragKind.ResizeObject);
            return;
        }
        var hit = ObjectAt(page, ToPagePoint(page, view));
        _selectedObject = hit is null ? null : (page, hit.Index);
        InvalidateOverlays();
        SelectionChanged?.Invoke();
        if (hit is null) return;
        if (clicks == 2 && hit.Kind == PageObjectKind.Text)
        {
            BeginEditText(page, hit);
            return;
        }
        BeginDrag(DragKind.MoveObject);
    }

    private async Task FinishMoveObjectAsync()
    {
        if (_selectedObject is not { } selected || (_dragNow - _dragStart).Length < 1.5) return;
        var from = ToPagePoint(selected.Page, _dragStart);
        var to = ToPagePoint(selected.Page, _dragNow);
        if (await Commit("Move object", b => PdfiumOps.TransformObject(b, selected.Page, selected.Index, 1, 0, 0, 1, to.X - from.X, to.Y - from.Y)))
        {
            _selectedObject = selected;
            InvalidateOverlays();
        }
    }

    private async Task FinishResizeObjectAsync()
    {
        if (_selectedObject is not { } selected || SelectedObjectInfo(selected.Page) is not { } obj || TryGeometry(selected.Page) is not { } geometry) return;
        var bounds = geometry.ToView(obj.Bounds);
        var factor = ResizeFactor(bounds);
        if (Math.Abs(factor - 1) < 0.01) return;
        var anchor = geometry.ToPage(bounds.TopLeft);
        if (await Commit("Resize object", b => PdfiumOps.TransformObject(b, selected.Page, selected.Index, factor, 0, 0, factor, anchor.X * (1 - factor), anchor.Y * (1 - factor))))
        {
            _selectedObject = selected;
            InvalidateOverlays();
        }
    }

    /// <summary>The text runs that sit on the same line as <paramref name="target"/> and touch it, left to right.</summary>
    private List<PageObjectInfo> LineGroup(int page, PageObjectInfo target, PageGeometry geometry)
    {
        var targetOrigin = geometry.ToView(target.Origin);
        var sameLine = ObjectsOf(page)
            .Where(o => o.Kind == PageObjectKind.Text && o.Text.Trim().Length > 0)
            .Where(o => Math.Abs(geometry.ToView(o.Origin).Y - targetOrigin.Y) < target.FontSize * 0.25 && Math.Abs(o.FontSize - target.FontSize) < target.FontSize * 0.15)
            .OrderBy(o => geometry.ToView(o.Bounds).Left)
            .ToList();
        var at = sameLine.FindIndex(o => o.Index == target.Index);
        if (at < 0) return [target];
        int first = at, last = at;
        var reach = target.FontSize * 1.2;
        while (first > 0 && geometry.ToView(sameLine[first].Bounds).Left - geometry.ToView(sameLine[first - 1].Bounds).Right < reach) first--;
        while (last < sameLine.Count - 1 && geometry.ToView(sameLine[last + 1].Bounds).Left - geometry.ToView(sameLine[last].Bounds).Right < reach) last++;
        return sameLine.GetRange(first, last - first + 1);
    }

    private static string JoinRuns(List<PageObjectInfo> runs, PageGeometry geometry)
    {
        var text = "";
        for (var i = 0; i < runs.Count; i++)
        {
            if (i > 0)
            {
                var gap = geometry.ToView(runs[i].Bounds).Left - geometry.ToView(runs[i - 1].Bounds).Right;
                var needsSpace = gap > runs[i].FontSize * 0.18 && !text.EndsWith(' ') && !runs[i].Text.StartsWith(' ');
                if (needsSpace) text += " ";
            }
            text += runs[i].Text;
        }
        return text.TrimEnd('\r', '\n');
    }

    private void BeginEditText(int page, PageObjectInfo target)
    {
        var geometry = TryGeometry(page)!;
        var runs = LineGroup(page, target, geometry);
        var original = JoinRuns(runs, geometry);
        var first = runs[0];
        var style = new TextStyle(InstalledFont(first.FontName), first.FontSize, first.Bold, first.Italic, first.ColorRgb);
        var bounds = runs.Select(r => geometry.ToView(r.Bounds)).Aggregate(Rect.Union);
        var baseline = geometry.ToView(first.Origin);
        var indexes = runs.Select(r => r.Index).ToList();
        _selectedObject = null;
        // The editor sits on the line so its first baseline matches the text being replaced.
        var top = baseline.Y - style.Size * 0.92;
        ShowTextEditor(page, new Rect(baseline.X, top, bounds.Width + 60, style.Size * 1.25), original, style, multiline: false, async edited =>
        {
            if (edited == original) return;
            await Commit("Edit text", b =>
            {
                var removed = PdfiumOps.RemoveObjects(b, page, indexes);
                return edited.Trim().Length == 0 ? removed : ContentService.AddText(removed, page, baseline, edited, style, originIsBaseline: true);
            });
        });
    }

    /// <summary>Maps a PDF font name ("ABCDEF+Calibri-Bold", "TimesNewRomanPSMT") to an installed font family.</summary>
    private static string InstalledFont(string pdfName)
    {
        var name = pdfName;
        foreach (var suffix in new[] { ",Bold", ",Italic", "-BoldItalic", "-Bold", "-Italic", "-Oblique", "-Regular", "PSMT", "PS", "MT", " Bold", " Italic", " Regular" })
        {
            name = name.Replace(suffix, "", StringComparison.OrdinalIgnoreCase);
        }
        name = name.Trim();
        var families = Fonts.SystemFontFamilies.Select(f => f.Source).ToList();
        var exact = families.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase))
                    ?? families.FirstOrDefault(f => string.Equals(f.Replace(" ", ""), name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        if (name.Contains("Times", StringComparison.OrdinalIgnoreCase) || name.Contains("Serif", StringComparison.OrdinalIgnoreCase)) return "Times New Roman";
        if (name.Contains("Courier", StringComparison.OrdinalIgnoreCase) || name.Contains("Mono", StringComparison.OrdinalIgnoreCase)) return "Courier New";
        return "Arial";
    }

    // ------------------------------------------------------------------ inline text editors

    private void BeginAddText(int page, Point view)
    {
        var style = new TextStyle(Options.FontFamily, Options.FontSize, Options.Bold, Options.Italic, Options.TextColor);
        var origin = new Point(view.X, view.Y - style.Size * 0.6);
        var preset = Options.PresetText ?? "";
        Options.PresetText = null;
        ShowTextEditor(page, new Rect(origin.X, origin.Y, 140, style.Size * 1.3), preset, style, multiline: true, async text =>
        {
            if (text.Trim().Length == 0) return;
            await Commit("Add text", b => ContentService.AddText(b, page, origin, text.TrimEnd(), style));
        });
    }

    private void ShowTextEditor(int page, Rect view, string text, TextStyle style, bool multiline, Func<string, Task> commit)
    {
        if (!_pages.TryGetValue(page, out var pageView)) return;
        var box = new TextBox
        {
            Style = null,
            Text = text,
            FontFamily = new FontFamily(style.FontFamily),
            FontSize = Math.Max(5, style.Size * Scale),
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(ToColor(style.Color)),
            Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)),
            CaretBrush = Brushes.Black,
            BorderBrush = HandleBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            AcceptsReturn = multiline,
            MinWidth = Math.Max(40, view.Width * Scale),
            MinHeight = view.Height * Scale,
        };
        Canvas.SetLeft(box, view.X * Scale - 3);
        Canvas.SetTop(box, view.Y * Scale - 1);
        HostEditor(pageView, box, accept => accept ? commit(box.Text) : Task.CompletedTask, multiline);
        box.CaretIndex = box.Text.Length;
    }

    /// <summary>Puts an editing control on the page and wires up "click away or Enter to accept, Esc to cancel".</summary>
    private void HostEditor(PageView pageView, Control editor, Func<bool, Task> finish, bool enterInsertsLine)
    {
        EndEditors();
        pageView.Editors.Children.Add(editor);
        var finished = false;
        async void End(bool accept)
        {
            if (finished) return;
            finished = true;
            _endEditor = null;
            pageView.Editors.Children.Remove(editor);
            await finish(accept);
        }
        _endEditor = End;
        editor.LostKeyboardFocus += (_, e) =>
        {
            // Focus moving into the editor's own popup (a combo box list) is not "leaving".
            if (e.NewFocus is DependencyObject target && IsInside(target, editor)) return;
            End(true);
        };
        editor.PreviewKeyDown += (_, e) =>
        {
            var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                End(false);
                Focus();
            }
            else if (e.Key == Key.Enter && (!enterInsertsLine || control))
            {
                e.Handled = true;
                End(true);
                Focus();
            }
        };
        Dispatcher.BeginInvoke(() => editor.Focus(), DispatcherPriority.Input);
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    /// <summary>Accepts whatever inline editor is open.</summary>
    public void EndEditors() => _endEditor?.Invoke(true);

    // ------------------------------------------------------------------ form filling

    private FormFieldInfo? FieldAt(int page, PdfPoint point) =>
        _fields.FirstOrDefault(f => f.PageIndex == page && f.Type is not (FormFieldType.Button or FormFieldType.Unknown) && f.Rect.Contains(point));

    private Task<bool> SetField(FormFieldInfo field, string value) =>
        Commit("Fill form", b => FormService.SetValue(b, field.PageIndex, field.AnnotIndex, value));

    private void ActivateField(FormFieldInfo field)
    {
        if (field.ReadOnly)
        {
            Say($"Field \"{field.Name}\" is read-only.");
            return;
        }
        switch (field.Type)
        {
            case FormFieldType.CheckBox:
                _ = SetField(field, field.Checked ? "false" : "true");
                break;
            case FormFieldType.Radio:
                if (!field.Checked) _ = SetField(field, "true");
                break;
            case FormFieldType.Text:
                ShowFieldTextEditor(field);
                break;
            case FormFieldType.ComboBox or FormFieldType.ListBox:
                ShowFieldChoiceEditor(field);
                break;
            case FormFieldType.Signature:
                Say("To sign here, use Sign > Sign with Certificate, or Sign > Add Signature for a handwritten signature.");
                break;
        }
    }

    private void ShowFieldTextEditor(FormFieldInfo field)
    {
        if (!_pages.TryGetValue(field.PageIndex, out var pageView) || TryGeometry(field.PageIndex) is not { } geometry) return;
        var rect = ToDip(geometry.ToView(field.Rect));
        var box = new TextBox
        {
            Style = null,
            Text = field.Value,
            Width = rect.Width,
            Height = rect.Height,
            FontFamily = new FontFamily("Arial"),
            FontSize = field.Multiline ? 10 * Scale : Math.Clamp(rect.Height * 0.62, 6, 14 * Scale),
            AcceptsReturn = field.Multiline,
            TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = field.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            BorderBrush = HandleBrush,
            BorderThickness = new Thickness(1.5),
            Background = Brushes.White,
            Foreground = Brushes.Black,
            Padding = new Thickness(1, 0, 1, 0),
        };
        if (field.MaxLength > 0) box.MaxLength = field.MaxLength;
        Canvas.SetLeft(box, rect.X);
        Canvas.SetTop(box, rect.Y);
        var moveOn = false;
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Tab) return;
            e.Handled = true;
            moveOn = true;
            Focus();
        };
        HostEditor(pageView, box, async accept =>
        {
            if (accept && box.Text != field.Value) await SetField(field, box.Text);
            if (accept && moveOn) await ActivateNextFieldAsync(field);
        }, field.Multiline);
        box.SelectAll();
    }

    private void ShowFieldChoiceEditor(FormFieldInfo field)
    {
        if (!_pages.TryGetValue(field.PageIndex, out var pageView) || TryGeometry(field.PageIndex) is not { } geometry) return;
        var rect = ToDip(geometry.ToView(field.Rect));
        var combo = new ComboBox
        {
            Style = null,
            Width = Math.Max(60, rect.Width),
            Height = Math.Max(20, Math.Min(rect.Height, 30)),
            ItemsSource = field.Options.Select(o => o.Display).ToList(),
            SelectedIndex = field.Options.ToList().FindIndex(o => o.Export == field.Value),
        };
        Canvas.SetLeft(combo, rect.X);
        Canvas.SetTop(combo, rect.Y);
        HostEditor(pageView, combo, async accept =>
        {
            if (!accept || combo.SelectedIndex < 0) return;
            var chosen = field.Options[combo.SelectedIndex].Export;
            if (chosen != field.Value) await SetField(field, chosen);
        }, enterInsertsLine: false);
        combo.DropDownClosed += (_, _) => Focus();
        Dispatcher.BeginInvoke(() => combo.IsDropDownOpen = true, DispatcherPriority.Input);
    }

    private async Task ActivateNextFieldAsync(FormFieldInfo current)
    {
        await _fieldsReload;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        var ordered = _fields.Where(f => !f.ReadOnly && f.Type is FormFieldType.Text or FormFieldType.ComboBox).ToList();
        var next = ordered.FirstOrDefault(f => f.PageIndex > current.PageIndex || (f.PageIndex == current.PageIndex && f.AnnotIndex > current.AnnotIndex)) ?? ordered.FirstOrDefault();
        if (next is null || (next.PageIndex == current.PageIndex && next.AnnotIndex == current.AnnotIndex)) return;
        BringIntoView(next.PageIndex, next.Rect);
        ActivateField(next);
    }
}
