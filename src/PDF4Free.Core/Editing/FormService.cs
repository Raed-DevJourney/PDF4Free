using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PDF4Free.Core.Model;

namespace PDF4Free.Core.Editing;

public enum FormFieldType { Text, CheckBox, Radio, ComboBox, ListBox, Button, Signature, Unknown }

public sealed record FormOption(string Export, string Display);

public sealed record FormFieldInfo(
    int PageIndex,
    int AnnotIndex,
    string Name,
    FormFieldType Type,
    PdfRect Rect,
    string Value,
    bool Checked,
    string OnState,
    IReadOnlyList<FormOption> Options,
    bool ReadOnly,
    bool Multiline,
    bool Password,
    int MaxLength);

/// <summary>Reads, fills and creates AcroForm fields by working on the raw field dictionaries.</summary>
public static partial class FormService
{
    private const int FlagReadOnly = 1, FlagMultiline = 1 << 12, FlagPassword = 1 << 13, FlagNoToggleToOff = 1 << 14,
        FlagRadio = 1 << 15, FlagPushButton = 1 << 16, FlagCombo = 1 << 17, FlagComb = 1 << 24;

    // ------------------------------------------------------------------ reading

    public static List<FormFieldInfo> ReadFields(byte[] source)
    {
        using var doc = PdfIO.Open(source, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var fields = new List<FormFieldInfo>();
        for (var p = 0; p < doc.PageCount; p++)
        {
            var annots = PdfX.Annots(doc.Pages[p], false);
            for (var i = 0; i < annots.Elements.Count; i++)
            {
                if (PdfX.Resolve(annots.Elements[i]) is PdfDictionary widget && widget.Name("/Subtype") == "/Widget")
                {
                    fields.Add(Describe(p, i, widget));
                }
            }
        }
        return fields;
    }

    private static FormFieldInfo Describe(int pageIndex, int annotIndex, PdfDictionary widget)
    {
        var flags = (int)PdfX.Number(Inherited(widget, "/Ff"));
        var type = TypeOf(Name(Inherited(widget, "/FT")), flags);
        var onState = OnStateOf(widget);
        var appearanceState = widget.Name("/AS").TrimStart('/');
        return new FormFieldInfo(
            pageIndex,
            annotIndex,
            FullName(widget),
            type,
            widget.Rect("/Rect") ?? default,
            ValueText(Inherited(widget, "/V")),
            appearanceState.Length > 0 && appearanceState != "Off",
            onState,
            ReadOptions(Inherited(widget, "/Opt")),
            (flags & FlagReadOnly) != 0,
            (flags & FlagMultiline) != 0,
            (flags & FlagPassword) != 0,
            (int)PdfX.Number(Inherited(widget, "/MaxLen")));
    }

    private static FormFieldType TypeOf(string fieldType, int flags) => fieldType switch
    {
        "/Tx" => FormFieldType.Text,
        "/Btn" when (flags & FlagPushButton) != 0 => FormFieldType.Button,
        "/Btn" when (flags & FlagRadio) != 0 => FormFieldType.Radio,
        "/Btn" => FormFieldType.CheckBox,
        "/Ch" when (flags & FlagCombo) != 0 => FormFieldType.ComboBox,
        "/Ch" => FormFieldType.ListBox,
        "/Sig" => FormFieldType.Signature,
        _ => FormFieldType.Unknown,
    };

    private static string Name(PdfItem? item) => PdfX.Resolve(item) is PdfName n ? n.Value : "";

    private static string ValueText(PdfItem? item) => PdfX.Resolve(item) switch
    {
        PdfString s => s.Value,
        PdfName n => n.Value.TrimStart('/'),
        PdfArray { Elements.Count: > 0 } a => ValueText(a.Elements[0]),
        _ => "",
    };

    private static List<FormOption> ReadOptions(PdfItem? item)
    {
        var options = new List<FormOption>();
        if (PdfX.Resolve(item) is not PdfArray array) return options;
        foreach (var element in array.Elements)
        {
            switch (PdfX.Resolve(element))
            {
                case PdfArray { Elements.Count: >= 2 } pair:
                    options.Add(new FormOption(ValueText(pair.Elements[0]), ValueText(pair.Elements[1])));
                    break;
                case { } single:
                    var text = ValueText(single);
                    options.Add(new FormOption(text, text));
                    break;
            }
        }
        return options;
    }

    private static string OnStateOf(PdfDictionary widget)
    {
        var normal = widget.Dict("/AP")?.Dict("/N");
        if (normal is null) return "Yes";
        foreach (var key in normal.Elements.Keys)
        {
            if (key != "/Off") return key.TrimStart('/');
        }
        return "Yes";
    }

    /// <summary>Looks a key up on the dictionary and then on its /Parent chain (field attributes are inheritable).</summary>
    private static PdfItem? Inherited(PdfDictionary start, string key)
    {
        var current = start;
        for (var depth = 0; current is not null && depth < 32; depth++)
        {
            if (current.Elements.ContainsKey(key)) return current.Elements[key];
            current = current.Dict("/Parent");
        }
        return null;
    }

    /// <summary>The dictionary that owns the field's value: the nearest one in the chain that carries /FT.</summary>
    private static PdfDictionary FieldOf(PdfDictionary widget)
    {
        var current = widget;
        for (var depth = 0; current is not null && depth < 32; depth++)
        {
            if (current.Elements.ContainsKey("/FT")) return current;
            current = current.Dict("/Parent");
        }
        return widget;
    }

    private static string FullName(PdfDictionary widget)
    {
        var parts = new List<string>();
        var current = widget;
        for (var depth = 0; current is not null && depth < 32; depth++)
        {
            if (current.Elements.ContainsKey("/T")) parts.Insert(0, current.Text("/T"));
            current = current.Dict("/Parent");
        }
        return string.Join('.', parts);
    }

    // ------------------------------------------------------------------ filling

    /// <summary>
    /// Sets one field. For check boxes and radio buttons pass "true"/"false"; for choices pass the export value.
    /// </summary>
    public static byte[] SetValue(byte[] source, int pageIndex, int annotIndex, string value) =>
        PdfIO.Modify(source, doc =>
        {
            var annots = PdfX.Annots(doc.Pages[pageIndex], false);
            if (annotIndex < 0 || annotIndex >= annots.Elements.Count) return;
            if (PdfX.Resolve(annots.Elements[annotIndex]) is PdfDictionary widget) Apply(doc, widget, value);
        });

    /// <summary>Fills fields by full name. Unknown names are ignored. Returns the number of fields set.</summary>
    public static byte[] FillByName(byte[] source, IReadOnlyDictionary<string, string> values, out int filled)
    {
        var count = 0;
        var result = PdfIO.Modify(source, doc =>
        {
            var done = new HashSet<string>();
            foreach (var page in doc.Pages)
            {
                foreach (var item in PdfX.Annots(page, false).Elements)
                {
                    if (PdfX.Resolve(item) is not PdfDictionary widget || widget.Name("/Subtype") != "/Widget") continue;
                    var name = FullName(widget);
                    if (!values.TryGetValue(name, out var value)) continue;
                    var type = TypeOf(Name(Inherited(widget, "/FT")), (int)PdfX.Number(Inherited(widget, "/Ff")));
                    if (type == FormFieldType.Radio)
                    {
                        if (OnStateOf(widget) != value) continue;
                        Apply(doc, widget, "true");
                    }
                    else if (type == FormFieldType.CheckBox)
                    {
                        Apply(doc, widget, value is "true" or "True" or "Yes" or "On" or "1" || value == OnStateOf(widget) ? "true" : "false");
                    }
                    else
                    {
                        if (!done.Add(name)) continue;
                        Apply(doc, widget, value);
                    }
                    count++;
                }
            }
        });
        filled = count;
        return result;
    }

    private static void Apply(PdfDocument doc, PdfDictionary widget, string value)
    {
        var field = FieldOf(widget);
        var flags = (int)PdfX.Number(Inherited(widget, "/Ff"));
        switch (TypeOf(Name(Inherited(widget, "/FT")), flags))
        {
            case FormFieldType.Text:
                field.Elements["/V"] = PdfX.UnicodeString(value);
                foreach (var w in WidgetsOf(field, widget)) BuildTextAppearance(doc, w, DisplayText(value, flags), flags);
                break;
            case FormFieldType.ComboBox or FormFieldType.ListBox:
                field.Elements["/V"] = PdfX.UnicodeString(value);
                field.Elements.Remove("/I");
                var options = ReadOptions(Inherited(widget, "/Opt"));
                var display = options.FirstOrDefault(o => o.Export == value)?.Display ?? value;
                foreach (var w in WidgetsOf(field, widget)) BuildTextAppearance(doc, w, display, flags & ~FlagMultiline & ~FlagComb);
                break;
            case FormFieldType.CheckBox:
                ApplyCheck(field, widget, value == "true");
                break;
            case FormFieldType.Radio:
                if (value == "true") ApplyRadio(field, widget);
                break;
        }
    }

    private static string DisplayText(string value, int flags) =>
        (flags & FlagPassword) != 0 ? new string('*', value.Length) : value;

    private static IEnumerable<PdfDictionary> WidgetsOf(PdfDictionary field, PdfDictionary fallback)
    {
        var kids = field.Array("/Kids");
        if (kids is null || kids.Elements.Count == 0)
        {
            yield return fallback;
            yield break;
        }
        foreach (var kid in kids.Elements)
        {
            if (PdfX.Resolve(kid) is PdfDictionary widget && widget.Name("/Subtype") == "/Widget") yield return widget;
        }
    }

    private static void ApplyCheck(PdfDictionary field, PdfDictionary widget, bool isChecked)
    {
        var state = isChecked ? OnStateOf(widget) : "Off";
        field.Elements.SetName("/V", "/" + state);
        foreach (var w in WidgetsOf(field, widget))
        {
            w.Elements.SetName("/AS", isChecked && OnStateOf(w) == state ? "/" + state : "/Off");
        }
    }

    private static void ApplyRadio(PdfDictionary field, PdfDictionary chosen)
    {
        field.Elements.SetName("/V", "/" + OnStateOf(chosen));
        foreach (var w in WidgetsOf(field, chosen))
        {
            w.Elements.SetName("/AS", ReferenceEquals(w, chosen) ? "/" + OnStateOf(w) : "/Off");
        }
    }

    // ------------------------------------------------------------------ appearance of text-like fields

    [GeneratedRegex(@"([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+rg")]
    private static partial Regex RgbOperator();

    [GeneratedRegex(@"(?:^|\s)([\d.]+)\s+g(?:\s|$)")]
    private static partial Regex GrayOperator();

    [GeneratedRegex(@"/\S+\s+([\d.]+)\s+Tf")]
    private static partial Regex FontOperator();

    private static (uint Color, double Size) ParseDefaultAppearance(string da)
    {
        static uint Channel(string s) => (uint)Math.Clamp(Math.Round(double.Parse(s, CultureInfo.InvariantCulture) * 255), 0, 255);
        uint color = 0;
        double size = 0;
        if (RgbOperator().Match(da) is { Success: true } rgb)
        {
            color = (Channel(rgb.Groups[1].Value) << 16) | (Channel(rgb.Groups[2].Value) << 8) | Channel(rgb.Groups[3].Value);
        }
        else if (GrayOperator().Match(da) is { Success: true } gray)
        {
            var level = Channel(gray.Groups[1].Value);
            color = (level << 16) | (level << 8) | level;
        }
        if (FontOperator().Match(da) is { Success: true } font) double.TryParse(font.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out size);
        return (color, size);
    }

    private static void BuildTextAppearance(PdfDocument doc, PdfDictionary widget, string text, int flags)
    {
        var rect = widget.Rect("/Rect") ?? default;
        if (rect.IsEmpty) return;
        var mk = widget.Dict("/MK");
        var rotation = (((mk?.Int("/R") ?? 0) / 90 % 4) + 4) % 4;
        var (w, h) = rotation is 1 or 3 ? (rect.Height, rect.Width) : (rect.Width, rect.Height);
        var da = ValueText(Inherited(widget, "/DA"));
        if (da.Length == 0) da = doc.Internals.Catalog.Dict("/AcroForm")?.Text("/DA") ?? "";
        var (color, size) = ParseDefaultAppearance(da);
        var border = mk?.Array("/BC") is { Elements.Count: > 0 } ? widget.Dict("/BS")?.Number("/W", 1) ?? 1 : 0;
        var multiline = (flags & FlagMultiline) != 0;
        var pad = border + 2;

        var content = new ContentBuilder();
        AppendBackground(content, mk, w, h, border);
        content.Raw("/Tx BMC").Raw("q").Rect(new PdfRect(pad - 1, border, w - pad + 1, h - border)).Raw("W n");
        if (size <= 0) size = AutoSize(text, w - 2 * pad, h - 2 * border, multiline);
        var lines = multiline ? TextMetrics.Wrap(text, size, w - 2 * pad) : [text.Replace('\n', ' ').Replace("\r", "")];
        var baseline = multiline
            ? h - border - 2 - size * TextMetrics.Ascent
            : (h - size * TextMetrics.CapHeight) / 2;
        var alignment = (int)PdfX.Number(Inherited(widget, "/Q"));
        var maxLength = (int)PdfX.Number(Inherited(widget, "/MaxLen"));
        var style = new TextStyle("Arial", size, Color: color);
        content.FillColor(color);
        foreach (var line in lines)
        {
            if ((flags & FlagComb) != 0 && maxLength > 0 && !multiline) AppendComb(content, line, style, w, baseline, maxLength);
            else AppendLine(content, line, style, AlignedX(line, style, alignment, w, pad), baseline);
            baseline -= size * 1.15;
        }
        content.Raw("Q").Raw("EMC");

        var resources = new PdfDictionary(doc);
        AnnotationService.AddHelvetica(doc, resources, "Helv", bold: false);
        SetNormalAppearance(doc, widget, w, h, content, resources, rotation);
    }

    private static double AutoSize(string text, double width, double height, bool multiline)
    {
        if (multiline) return 10;
        var size = Math.Clamp(height * 0.68, 4, 14);
        var natural = TextMetrics.Width(text, size);
        return natural > width && natural > 0 ? Math.Max(4, size * width / natural) : size;
    }

    private static void AppendBackground(ContentBuilder content, PdfDictionary? mk, double w, double h, double border)
    {
        if (mk?.Array("/BG") is { Elements.Count: > 0 } background)
        {
            content.Raw(ColorOperator(background.Numbers(), stroke: false)).Rect(new PdfRect(0, 0, w, h)).Raw("f");
        }
        if (border > 0 && mk?.Array("/BC") is { Elements.Count: > 0 } outline)
        {
            content.Raw(ColorOperator(outline.Numbers(), stroke: true)).Op("w", border)
                .Rect(new PdfRect(border / 2, border / 2, w - border / 2, h - border / 2)).Raw("S");
        }
    }

    private static string ColorOperator(double[] c, bool stroke)
    {
        var numbers = string.Join(' ', c.Select(ContentBuilder.F));
        return c.Length switch
        {
            1 => numbers + (stroke ? " G" : " g"),
            4 => numbers + (stroke ? " K" : " k"),
            _ => numbers + (stroke ? " RG" : " rg"),
        };
    }

    private static double AlignedX(string line, TextStyle style, int alignment, double width, double pad)
    {
        var textWidth = MeasureLine(line, style);
        return alignment switch
        {
            1 => (width - textWidth) / 2,
            2 => width - pad - textWidth,
            _ => pad,
        };
    }

    private static void AppendComb(ContentBuilder content, string line, TextStyle style, double width, double baseline, int cells)
    {
        var cell = width / cells;
        for (var i = 0; i < line.Length && i < cells; i++)
        {
            var glyph = line[i].ToString();
            AppendLine(content, glyph, style, i * cell + (cell - MeasureLine(glyph, style)) / 2, baseline);
        }
    }

    private static bool IsWinAnsi(string text) => PdfX.WinAnsi.GetString(PdfX.WinAnsi.GetBytes(text)) == text;

    private static bool IsWinAnsi(char c) => c < 0x80 || IsWinAnsi(c.ToString());

    private static double MeasureRun(string run, TextStyle style, bool latin) =>
        latin ? TextMetrics.Width(run, style.Size) : ComplexText.Measure(run, style);

    private static double MeasureLine(string line, TextStyle style)
    {
        if (IsWinAnsi(line)) return TextMetrics.Width(line, style.Size);
        if (ComplexText.StartsRightToLeft(line)) return ComplexText.Measure(line, style);
        return ComplexText.SplitRuns(line, IsWinAnsi).Sum(r => MeasureRun(r.Text, style, r.Simple));
    }

    /// <summary>
    /// Latin text uses Helvetica and stays real text. Anything Helvetica cannot show is drawn as glyph outlines, so it
    /// appears without an embedded font: run by run in a left-to-right line, or the whole line when it reads right to left.
    /// </summary>
    private static void AppendLine(ContentBuilder content, string line, TextStyle style, double x, double baseline)
    {
        if (line.Length == 0) return;
        if (IsWinAnsi(line))
        {
            content.TextLine("Helv", style.Size, x, baseline, line);
            return;
        }
        if (ComplexText.StartsRightToLeft(line))
        {
            ComplexText.AppendOutline(content, line, style, x, baseline);
            return;
        }
        foreach (var (run, latin) in ComplexText.SplitRuns(line, IsWinAnsi))
        {
            if (latin) content.TextLine("Helv", style.Size, x, baseline, run);
            else ComplexText.AppendOutline(content, run, style, x, baseline);
            x += MeasureRun(run, style, latin);
        }
    }

    private static void SetNormalAppearance(PdfDocument doc, PdfDictionary widget, double w, double h, ContentBuilder content, PdfDictionary resources, int rotation)
    {
        var form = PdfX.NewStream(doc, content.ToBytes());
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = PdfX.Reals(doc, 0, 0, w, h);
        form.Elements["/Resources"] = resources;
        double[]? matrix = rotation switch
        {
            1 => [0, 1, -1, 0, 0, 0],
            2 => [-1, 0, 0, -1, 0, 0],
            3 => [0, -1, 1, 0, 0, 0],
            _ => null,
        };
        if (matrix is not null) form.Elements["/Matrix"] = PdfX.Reals(doc, matrix);
        var appearance = widget.Dict("/AP") ?? new PdfDictionary(doc);
        appearance.Elements["/N"] = PdfX.Ref(form);
        widget.Elements["/AP"] = appearance;
    }

    // ------------------------------------------------------------------ creating and deleting fields

    /// <summary>Adds a field. For radio buttons, <paramref name="name"/> is the group and <paramref name="options"/>[0] the button's value.</summary>
    public static byte[] AddField(byte[] source, int pageIndex, PdfRect rect, FormFieldType type, string name, IReadOnlyList<string>? options = null) =>
        PdfIO.Modify(source, doc =>
        {
            var page = doc.Pages[pageIndex];
            var acroForm = EnsureAcroForm(doc);
            var fields = acroForm.Array("/Fields")!;
            var widget = PdfX.NewObject(doc);
            widget.Elements.SetName("/Type", "/Annot");
            widget.Elements.SetName("/Subtype", "/Widget");
            widget.Elements["/Rect"] = rect.ToArray(doc);
            widget.Elements.SetInteger("/F", 4);
            if (page.Reference is { } pageRef) widget.Elements["/P"] = pageRef;
            var mk = new PdfDictionary(doc);
            mk.Elements["/BC"] = PdfX.Reals(doc, 0.45, 0.45, 0.45);
            var rotation = PdfX.Geometry(page).Rotation;
            if (rotation != 0) mk.Elements.SetInteger("/R", rotation * 90);
            widget.Elements["/MK"] = mk;
            var border = new PdfDictionary(doc);
            border.Elements.SetReal("/W", 1);
            widget.Elements["/BS"] = border;
            PdfX.Annots(page, create: true).Elements.Add(PdfX.Ref(widget));

            var unique = type == FormFieldType.Radio ? name : UniqueName(fields, name);
            switch (type)
            {
                case FormFieldType.CheckBox:
                    MakeButton(doc, widget, rotation, "Yes", round: false);
                    widget.Elements.SetName("/FT", "/Btn");
                    widget.Elements.SetName("/V", "/Off");
                    widget.Elements["/T"] = PdfX.UnicodeString(unique);
                    fields.Elements.Add(PdfX.Ref(widget));
                    break;
                case FormFieldType.Radio:
                    AddRadio(doc, fields, widget, rotation, unique, options?.FirstOrDefault());
                    break;
                case FormFieldType.ComboBox or FormFieldType.ListBox:
                    MakeChoice(doc, widget, type, unique, options ?? []);
                    fields.Elements.Add(PdfX.Ref(widget));
                    break;
                case FormFieldType.Signature:
                    widget.Elements.SetName("/FT", "/Sig");
                    widget.Elements["/T"] = PdfX.UnicodeString(unique);
                    BuildTextAppearance(doc, widget, "", 0);
                    fields.Elements.Add(PdfX.Ref(widget));
                    break;
                default:
                    mk.Elements["/BG"] = PdfX.Reals(doc, 1, 1, 1);
                    widget.Elements.SetName("/FT", "/Tx");
                    widget.Elements["/T"] = PdfX.UnicodeString(unique);
                    widget.Elements.SetString("/DA", "/Helv 0 Tf 0 g");
                    if (rect.Height > 40 || (rotation is 1 or 3 && rect.Width > 40)) widget.Elements.SetInteger("/Ff", FlagMultiline);
                    BuildTextAppearance(doc, widget, "", widget.Int("/Ff"));
                    fields.Elements.Add(PdfX.Ref(widget));
                    break;
            }
        });

    private static PdfDictionary EnsureAcroForm(PdfDocument doc)
    {
        var catalog = doc.Internals.Catalog;
        var acroForm = catalog.Dict("/AcroForm");
        if (acroForm is null)
        {
            acroForm = PdfX.NewObject(doc);
            catalog.Elements["/AcroForm"] = PdfX.Ref(acroForm);
        }
        if (acroForm.Array("/Fields") is null) acroForm.Elements["/Fields"] = new PdfArray(doc);
        if (acroForm.Text("/DA").Length == 0) acroForm.Elements.SetString("/DA", "/Helv 0 Tf 0 g");
        var resources = acroForm.Dict("/DR");
        if (resources is null)
        {
            resources = new PdfDictionary(doc);
            acroForm.Elements["/DR"] = resources;
        }
        if (resources.Dict("/Font")?.Elements.ContainsKey("/Helv") != true) AnnotationService.AddHelvetica(doc, resources, "Helv", bold: false);
        return acroForm;
    }

    private static string UniqueName(PdfArray fields, string wanted)
    {
        var taken = new HashSet<string>();
        foreach (var item in fields.Elements)
        {
            if (PdfX.Resolve(item) is PdfDictionary f) taken.Add(f.Text("/T"));
        }
        var baseName = string.IsNullOrWhiteSpace(wanted) ? "Field" : wanted.Trim();
        if (!taken.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            if (!taken.Contains($"{baseName}_{i}")) return $"{baseName}_{i}";
        }
    }

    private static void MakeButton(PdfDocument doc, PdfDictionary widget, int rotation, string onState, bool round)
    {
        var rect = widget.Rect("/Rect") ?? default;
        var (w, h) = rotation is 1 or 3 ? (rect.Height, rect.Width) : (rect.Width, rect.Height);
        var box = new PdfRect(0.5, 0.5, w - 0.5, h - 0.5);
        ContentBuilder Frame()
        {
            var c = new ContentBuilder().Raw("1 g 0.45 G 1 w");
            if (round) c.Ellipse(box);
            else c.Rect(box);
            return c.Raw("B");
        }
        var off = Frame();
        var on = Frame().Raw("0 g 0 G");
        if (round) on.Ellipse(new PdfRect(w * 0.28, h * 0.28, w * 0.72, h * 0.72)).Raw("f");
        else on.Op("w", Math.Max(1.2, Math.Min(w, h) * 0.12)).Raw("1 J 1 j").Op("m", w * 0.22, h * 0.52).Op("l", w * 0.42, h * 0.26).Op("l", w * 0.78, h * 0.76).Raw("S");

        var normal = new PdfDictionary(doc);
        normal.Elements["/" + onState] = PdfX.Ref(ButtonForm(doc, on, w, h, rotation));
        normal.Elements["/Off"] = PdfX.Ref(ButtonForm(doc, off, w, h, rotation));
        var appearance = new PdfDictionary(doc);
        appearance.Elements["/N"] = normal;
        widget.Elements["/AP"] = appearance;
        widget.Elements.SetName("/AS", "/Off");
    }

    private static PdfDictionary ButtonForm(PdfDocument doc, ContentBuilder content, double w, double h, int rotation)
    {
        var form = PdfX.NewStream(doc, content.ToBytes());
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = PdfX.Reals(doc, 0, 0, w, h);
        form.Elements["/Resources"] = new PdfDictionary(doc);
        if (rotation == 1) form.Elements["/Matrix"] = PdfX.Reals(doc, 0, 1, -1, 0, 0, 0);
        if (rotation == 2) form.Elements["/Matrix"] = PdfX.Reals(doc, -1, 0, 0, -1, 0, 0);
        if (rotation == 3) form.Elements["/Matrix"] = PdfX.Reals(doc, 0, -1, 1, 0, 0, 0);
        return form;
    }

    private static void AddRadio(PdfDocument doc, PdfArray fields, PdfDictionary widget, int rotation, string group, string? value)
    {
        PdfDictionary? parent = null;
        foreach (var item in fields.Elements)
        {
            if (PdfX.Resolve(item) is PdfDictionary f && f.Text("/T") == group && f.Name("/FT") == "/Btn" && (f.Int("/Ff") & FlagRadio) != 0) parent = f;
        }
        if (parent is null)
        {
            parent = PdfX.NewObject(doc);
            parent.Elements.SetName("/FT", "/Btn");
            parent.Elements.SetInteger("/Ff", FlagRadio | FlagNoToggleToOff);
            parent.Elements["/T"] = PdfX.UnicodeString(UniqueName(fields, group));
            parent.Elements.SetName("/V", "/Off");
            parent.Elements["/Kids"] = new PdfArray(doc);
            fields.Elements.Add(PdfX.Ref(parent));
        }
        var kids = parent.Array("/Kids")!;
        var state = string.IsNullOrWhiteSpace(value) ? $"Choice{kids.Elements.Count + 1}" : Regex.Replace(value, @"[^A-Za-z0-9_]", "");
        if (state.Length == 0 || state == "Off") state = $"Choice{kids.Elements.Count + 1}";
        MakeButton(doc, widget, rotation, state, round: true);
        widget.Elements["/Parent"] = PdfX.Ref(parent);
        kids.Elements.Add(PdfX.Ref(widget));
    }

    private static void MakeChoice(PdfDocument doc, PdfDictionary widget, FormFieldType type, string name, IReadOnlyList<string> options)
    {
        widget.Dict("/MK")!.Elements["/BG"] = PdfX.Reals(doc, 1, 1, 1);
        widget.Elements.SetName("/FT", "/Ch");
        widget.Elements["/T"] = PdfX.UnicodeString(name);
        widget.Elements.SetString("/DA", "/Helv 0 Tf 0 g");
        if (type == FormFieldType.ComboBox) widget.Elements.SetInteger("/Ff", FlagCombo);
        var list = new PdfArray(doc);
        foreach (var option in options) list.Elements.Add(PdfX.UnicodeString(option));
        widget.Elements["/Opt"] = list;
        BuildTextAppearance(doc, widget, "", 0);
    }

    public static byte[] DeleteField(byte[] source, int pageIndex, int annotIndex) =>
        PdfIO.Modify(source, doc =>
        {
            var annots = PdfX.Annots(doc.Pages[pageIndex], false);
            if (annotIndex < 0 || annotIndex >= annots.Elements.Count) return;
            if (PdfX.Resolve(annots.Elements[annotIndex]) is not PdfDictionary widget) return;
            annots.Elements.RemoveAt(annotIndex);
            DetachWidget(doc, widget);
        });

    /// <summary>Removes a widget from the form's field tree (the caller removes it from the page).</summary>
    internal static void DetachWidget(PdfDocument doc, PdfDictionary widget)
    {
        var fields = doc.Internals.Catalog.Dict("/AcroForm")?.Array("/Fields");
        var parent = widget.Dict("/Parent");
        if (parent?.Array("/Kids") is { } kids)
        {
            RemoveReference(kids, widget);
            if (kids.Elements.Count == 0 && fields is not null) RemoveReference(fields, parent);
        }
        if (fields is not null) RemoveReference(fields, widget);
    }

    private static void RemoveReference(PdfArray array, PdfDictionary target)
    {
        for (var i = array.Elements.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(PdfX.Resolve(array.Elements[i]), target)) array.Elements.RemoveAt(i);
        }
    }

    /// <summary>Clears every field value.</summary>
    public static byte[] Reset(byte[] source)
    {
        var fields = ReadFields(source);
        return PdfIO.Modify(source, doc =>
        {
            foreach (var info in fields)
            {
                if (PdfX.Resolve(PdfX.Annots(doc.Pages[info.PageIndex], false).Elements[info.AnnotIndex]) is not PdfDictionary widget) continue;
                switch (info.Type)
                {
                    case FormFieldType.CheckBox:
                        ApplyCheck(FieldOf(widget), widget, false);
                        break;
                    case FormFieldType.Radio:
                        FieldOf(widget).Elements.SetName("/V", "/Off");
                        widget.Elements.SetName("/AS", "/Off");
                        break;
                    case FormFieldType.Text or FormFieldType.ComboBox or FormFieldType.ListBox:
                        Apply(doc, widget, "");
                        break;
                }
            }
        });
    }
}
