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
            }
        }
        return false;
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
