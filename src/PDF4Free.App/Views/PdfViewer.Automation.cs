#if DEBUG
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PDF4Free.Core.Model;

namespace PDF4Free.App.Views;

/// <summary>
/// Debug-build test hooks: they drive the same pointer code the mouse does, with positions given in view points
/// of a page, so the interactive tools can be exercised without taking over the real mouse.
/// </summary>
public sealed partial class PdfViewer
{
    private Point SurfaceOf(int page, Point view) => new(_layout[page].X + view.X * Scale, _layout[page].Y + view.Y * Scale);

    public async Task AutomationDragAsync(int page, Point from, Point to, int clicks)
    {
        if (page < 0 || page >= _layout.Length) throw new ArgumentOutOfRangeException(nameof(page));
        BringIntoView(page, TryGeometry(page)!.ToPage(new Rect(from, to)));
        UpdateLayout();
        _synthetic = true;
        try
        {
            await PointerDownAsync(SurfaceOf(page, from), clicks);
            if (from != to)
            {
                for (var i = 1; i <= 8; i++)
                {
                    PointerMove(SurfaceOf(page, new Point(from.X + (to.X - from.X) * i / 8, from.Y + (to.Y - from.Y) * i / 8)));
                }
            }
            await PointerUpAsync();
        }
        finally
        {
            _synthetic = false;
        }
    }

    /// <summary>Puts text into the inline editor that is open (new text, edited text, or a form field) and accepts it.</summary>
    public bool AutomationType(string text)
    {
        foreach (var page in _pages.Values)
        {
            foreach (var child in page.Editors.Children)
            {
                if (child is TextBox box)
                {
                    box.Text = text;
                    EndEditors();
                    return true;
                }
                if (child is ComboBox combo)
                {
                    combo.SelectedItem = text;
                    EndEditors();
                    return true;
                }
                if (child is DateFieldEditor date)
                {
                    date.Text = text;
                    EndEditors();
                    return true;
                }
            }
        }
        return false;
    }

    private DateFieldEditor? OpenDateEditor() => _pages.Values.SelectMany(p => p.Editors.Children.OfType<DateFieldEditor>()).FirstOrDefault();

    /// <summary>Chooses a date and time in the date editor that is open, the way its calendar and time boxes do, and accepts it.</summary>
    public bool AutomationPick(DateTime value)
    {
        if (OpenDateEditor() is not { } editor) return false;
        editor.Pick(value);
        EndEditors();
        return true;
    }

    /// <summary>Opens the calendar of the date editor that is open, so a test can use the real picker.</summary>
    public bool AutomationOpenPicker()
    {
        if (OpenDateEditor() is not { } editor) return false;
        editor.OpenPicker();
        return true;
    }

    public bool AutomationClickDay(DateTime day) => OpenDateEditor()?.AutomationClickDay(day) == true;

    public bool AutomationSetTime(int hour, int minute) => OpenDateEditor()?.AutomationSetTime(hour, minute) == true;

    /// <summary>
    /// Sends a key to whatever has the keyboard focus, as the keyboard would. While another program has the focus
    /// nothing here has it, and the key goes to the open date editor instead.
    /// </summary>
    public bool AutomationKey(System.Windows.Input.Key key)
    {
        var target = System.Windows.Input.Keyboard.FocusedElement as UIElement ?? OpenDateEditor()?.KeyTarget;
        if (target is null || PresentationSource.FromVisual(target) is not { } source) return false;
        var down = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(down);
        if (down.Handled) return true;
        target.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        return true;
    }

    private string? _lastStatus;

    /// <summary>The latest status-bar message; recording starts with the first call.</summary>
    private string LastStatus
    {
        get
        {
            if (_lastStatus is not null) return _lastStatus;
            Status += message => _lastStatus = message;
            return _lastStatus = "";
        }
    }

    /// <summary>Whether the window is active, where the keyboard focus is, and what the open date editor shows.</summary>
    public string AutomationFocus()
    {
        var editor = OpenDateEditor();
        var focused = System.Windows.Input.Keyboard.FocusedElement;
        var where = focused is null ? "none" : focused.GetType().Name + (focused is DependencyObject d && editor is not null && IsInside(d, editor) ? "(in editor)" : "");
        return $"active={Window.GetWindow(this)?.IsActive} foreground={ForegroundProcess()} focus={where} editor={(editor is null ? "none" : $"'{editor.Text}'")} picker={editor?.IsPickerOpen == true} status='{LastStatus}'";
    }

    /// <summary>Puts text into the open date editor without accepting it, as typing does.</summary>
    public bool AutomationWrite(string text)
    {
        if (OpenDateEditor() is not { } editor) return false;
        editor.Text = text;
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <summary>The program whose window has the user's attention; a test can tell from it that someone else took the focus away.</summary>
    private static string ForegroundProcess()
    {
        _ = GetWindowThreadProcessId(GetForegroundWindow(), out var id);
        try
        {
            return id == Environment.ProcessId ? "self" : System.Diagnostics.Process.GetProcessById((int)id).ProcessName;
        }
        catch (ArgumentException)
        {
            return "unknown";
        }
    }

    /// <summary>Where a piece of text sits on a page, in view points ("x y width height"), or empty when it is not there.</summary>
    public string AutomationFindText(int page, string needle)
    {
        var text = Session!.Document.GetText(page);
        var index = text.Text.IndexOf(needle, StringComparison.Ordinal);
        if (index < 0) return "";
        var rect = text.GetRangeRects(index, needle.Length).Aggregate((a, b) => a.Union(b));
        var view = TryGeometry(page)!.ToView(rect);
        return string.Create(CultureInfo.InvariantCulture, $"{view.X:0.#} {view.Y:0.#} {view.Width:0.#} {view.Height:0.#}");
    }

    /// <summary>Completes when the form fields of the current document version have been read.</summary>
    public async Task AutomationFieldsReadyAsync()
    {
        await _fieldsReload;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Every form field as the viewer currently knows it, as JSON, with its rectangle in view points.</summary>
    public string AutomationFieldsJson() => System.Text.Json.JsonSerializer.Serialize(_fields.Select(f =>
    {
        var view = TryGeometry(f.PageIndex)!.ToView(f.Rect);
        return new
        {
            name = f.Name, type = f.Type.ToString(), value = f.Value, isChecked = f.Checked, onState = f.OnState, page = f.PageIndex,
            dateFormat = f.DateFormat?.Pattern ?? "",
            x = Math.Round(view.X, 1), y = Math.Round(view.Y, 1), width = Math.Round(view.Width, 1), height = Math.Round(view.Height, 1),
        };
    }));

    public string AutomationDescribe(int page)
    {
        string R(PdfRect r)
        {
            var v = TryGeometry(page)!.ToView(r);
            return string.Create(CultureInfo.InvariantCulture, $"{v.X:0},{v.Y:0},{v.Width:0},{v.Height:0}");
        }
        var annotations = string.Join(" ; ", AnnotationsOf(page).Select(a => $"{a.Index}:{a.Subtype}@{R(a.Rect)}"));
        var objects = string.Join(" ; ", ObjectsOf(page).Take(40).Select(o => $"{o.Index}:{o.Kind}@{R(o.Bounds)}" + (o.Text.Length > 0 ? $"'{o.Text.Trim()}'" : "")));
        var fields = string.Join(" ; ", _fields.Where(f => f.PageIndex == page).Select(f => $"{f.Name}:{f.Type}={f.Value}{(f.Checked ? "[x]" : "")}@{R(f.Rect)}"));
        return $"tool={_tool} selection='{SelectedText}' annotations=[{annotations}] objects=[{objects}] fields=[{fields}]";
    }
}
#endif
