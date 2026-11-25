using System.Windows;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Tests;

public sealed partial class TestRunner
{
    public static byte[] FormSample()
    {
        var bytes = Samples.Basic(1);
        using var doc = PdfiumDocument.Load(bytes);
        var g = doc.GetGeometry(0);
        PdfRect V(double x, double y, double w, double h) => g.ToPage(new Rect(x, y, w, h));
        bytes = FormService.AddField(bytes, 0, V(72, 430, 220, 22), FormFieldType.Text, "FullName");
        bytes = FormService.AddField(bytes, 0, V(72, 465, 220, 70), FormFieldType.Text, "Notes");
        bytes = FormService.AddField(bytes, 0, V(320, 430, 16, 16), FormFieldType.CheckBox, "Agree");
        bytes = FormService.AddField(bytes, 0, V(320, 460, 16, 16), FormFieldType.Radio, "Plan", ["Basic"]);
        bytes = FormService.AddField(bytes, 0, V(350, 460, 16, 16), FormFieldType.Radio, "Plan", ["Pro"]);
        bytes = FormService.AddField(bytes, 0, V(320, 490, 160, 22), FormFieldType.ComboBox, "Country", ["Lebanon", "France", "Canada"]);
        bytes = FormService.AddField(bytes, 0, V(72, 550, 200, 50), FormFieldType.Signature, "Signature1");
        return bytes;
    }

    [Check]
    public void Form_fields_are_created_read_filled_and_deleted()
    {
        var bytes = FormSample();
        var fields = FormService.ReadFields(bytes);
        AssertEqual(7, fields.Count, "field count");
        AssertEqual("FullName,Notes,Agree,Plan,Plan,Country,Signature1", string.Join(",", fields.Select(f => f.Name)), "names");
        AssertEqual(FormFieldType.Radio, fields[3].Type, "radio type");
        AssertEqual("Pro", fields[4].OnState, "radio state");
        Assert(fields[1].Multiline, "tall text field is multiline");
        AssertEqual(3, fields[5].Options.Count, "combo options");

        bytes = FormService.SetValue(bytes, 0, fields[0].AnnotIndex, "Sam Example");
        bytes = FormService.SetValue(bytes, 0, fields[1].AnnotIndex, "First line of notes that is long enough to wrap around inside the box.\nSecond line.");
        bytes = FormService.SetValue(bytes, 0, fields[2].AnnotIndex, "true");
        bytes = FormService.SetValue(bytes, 0, fields[4].AnnotIndex, "true");
        bytes = FormService.SetValue(bytes, 0, fields[5].AnnotIndex, "France");
        File.WriteAllBytes(PathFor("form-filled.pdf"), bytes);

        var filled = FormService.ReadFields(bytes);
        AssertEqual("Sam Example", filled[0].Value, "text value");
        Assert(filled[2].Checked, "check box checked");
        Assert(!filled[3].Checked && filled[4].Checked, "radio selection");
        AssertEqual("Pro", filled[3].Value, "radio group value");
        AssertEqual("France", filled[5].Value, "combo value");
        using (var doc = PdfiumDocument.Load(bytes)) SavePng(doc.RenderBitmap(0, 110), "form-filled.png");

        var byName = FormService.FillByName(FormSample(), new Dictionary<string, string> { ["FullName"] = "مرحبا", ["Plan"] = "Basic", ["Agree"] = "true" }, out var count);
        AssertEqual(3, count, "filled by name");
        var named = FormService.ReadFields(byName);
        Assert(named[3].Checked && !named[4].Checked && named[2].Checked, "by-name states");
        using (var doc = PdfiumDocument.Load(byName)) SavePng(doc.RenderBitmap(0, 110), "form-arabic.png");

        var reset = FormService.ReadFields(FormService.Reset(bytes));
        Assert(reset.All(f => f.Value is "" or "Off") && reset.All(f => !f.Checked), "reset clears everything");

        var deleted = FormService.DeleteField(bytes, 0, fields[4].AnnotIndex);
        AssertEqual(6, FormService.ReadFields(deleted).Count, "delete field");

        var flat = PdfiumOps.Flatten(bytes);
        AssertEqual(0, FormService.ReadFields(flat).Count, "flatten removes fields");
        using var flatDoc = PdfiumDocument.Load(flat);
        Assert(flatDoc.GetText(0).Text.Contains("Sam Example"), "flattened value is page text");
    }

    [Check]
    public void Date_formats_write_and_read_dates_the_way_acrobat_patterns_say()
    {
        var moment = new DateTime(2026, 3, 4, 15, 5, 9);
        string Shown(string pattern) => new DateFieldFormat(pattern).Format(moment);
        AssertEqual("04/03/2026", Shown("dd/mm/yyyy"), "day first");
        AssertEqual("03/04/2026", Shown("mm/dd/yyyy"), "month first");
        AssertEqual("2026-03-04 15:05:09", Shown("yyyy-mm-dd HH:MM:ss"), "ISO with seconds");
        AssertEqual("4 Mar 2026 3:05 pm", Shown("d mmm yyyy h:MM tt"), "short month, 12-hour clock");
        AssertEqual("Wednesday, March 4, 2026", Shown("dddd, mmmm d, yyyy"), "names written out");
        AssertEqual("15:05", Shown("HH:MM"), "time only");

        var dayFirst = new DateFieldFormat("dd/mm/yyyy");
        Assert(dayFirst is { HasDate: true, HasTime: false }, "date-only pattern");
        Assert(dayFirst.TryParse("04/03/2026", out var exact) && exact == moment.Date, "own pattern");
        Assert(dayFirst.TryParse("4/3/2026", out var loose) && loose == moment.Date, "own pattern without leading zeros keeps day before month");
        Assert(dayFirst.TryParse("2026-03-04", out var iso) && iso == moment.Date, "ISO date");
        Assert(dayFirst.TryParse("4 March 2026", out var named) && named == moment.Date, "month written out");
        Assert(!dayFirst.TryParse("31/02/2026", out _), "a day that does not exist is rejected");
        Assert(!dayFirst.TryParse("next week", out _), "words are rejected");
        AssertEqual("next week", dayFirst.Normalize("next week"), "text that is not a date is left alone");

        var twelveHour = new DateFieldFormat("d mmm yyyy h:MM tt");
        Assert(twelveHour is { HasDate: true, HasTime: true, TwelveHour: true, HasSeconds: false }, "date and time pattern");
        Assert(twelveHour.TryParse("4 Mar 2026 3:05 PM", out var afternoon) && afternoon == new DateTime(2026, 3, 4, 15, 5, 0), "PM in either case");
        var dayFirstTime = new DateFieldFormat("dd/mm/yyyy HH:MM");
        Assert(dayFirstTime.TryParse("4/3/2026 9:07", out var morning) && morning == new DateTime(2026, 3, 4, 9, 7, 0), "day before month also when a time follows");
        AssertEqual("4 Mar 2026 3:05 pm", twelveHour.Normalize("2026-03-04T15:05"), "ISO date and time rewritten in the field's pattern");

        AssertEqual("dd/mm/yyyy", DateFieldFormat.FromScript("AFDate_FormatEx(\"dd/mm/yyyy\");")?.Pattern, "explicit format script");
        AssertEqual("yyyy-mm-dd", DateFieldFormat.FromScript("AFDate_KeystrokeEx( 'yyyy-mm-dd' )")?.Pattern, "keystroke script, single quotes");
        AssertEqual("dd-mmm-yy", DateFieldFormat.FromScript("AFDate_Format(6);")?.Pattern, "numbered date format");
        AssertEqual("h:MM tt", DateFieldFormat.FromScript("AFTime_Format(1);")?.Pattern, "numbered time format");
        Assert(DateFieldFormat.FromScript("AFNumber_Format(2, 0, 0, 0, \"\", true);") is null, "a number format is not a date");
        foreach (var pattern in DateFieldFormat.DatePatterns.Concat(DateFieldFormat.DateTimePatterns))
        {
            var format = new DateFieldFormat(pattern);
            Assert(format.TryParse(format.Format(moment), out var back) && format.Format(back) == format.Format(moment), $"offered pattern {pattern} reads what it writes");
        }
    }

    [Check]
    public void Date_fields_are_text_fields_with_acrobat_scripts_and_keep_dates_in_their_format()
    {
        var bytes = Samples.Basic(1);
        PdfRect V(double x, double y, double w, double h)
        {
            using var doc = PdfiumDocument.Load(bytes);
            return doc.GetGeometry(0).ToPage(new Rect(x, y, w, h));
        }
        bytes = FormService.AddField(bytes, 0, V(72, 430, 160, 22), FormFieldType.Text, "OrderDate", dateFormat: new DateFieldFormat("dd/mm/yyyy"));
        bytes = FormService.AddField(bytes, 0, V(72, 465, 220, 22), FormFieldType.Text, "DeliverBy", dateFormat: new DateFieldFormat("d mmm yyyy h:MM tt"));
        bytes = FormService.AddField(bytes, 0, V(72, 500, 160, 22), FormFieldType.Text, "Reference");
        bytes = FormService.AddField(bytes, 0, V(72, 535, 160, 60), FormFieldType.Text, "TallDate", dateFormat: new DateFieldFormat("yyyy-mm-dd"));

        var fields = FormService.ReadFields(bytes);
        Assert(fields.All(f => f.Type == FormFieldType.Text), "date fields are text fields");
        AssertEqual("dd/mm/yyyy|d mmm yyyy h:MM tt||yyyy-mm-dd", string.Join("|", fields.Select(f => f.DateFormat?.Pattern)), "patterns read back");
        Assert(fields[0].IsDate && !fields[2].IsDate, "only fields with a date script are dates");
        Assert(!fields[3].Multiline, "a tall date field stays single-line");

        // A second reader (PDFium) finds the scripts where readers that run form scripts look for them.
        using (var pdfium = PdfiumDocument.Load(bytes))
        {
            AssertEqual("AFDate_KeystrokeEx(\"dd/mm/yyyy\");", pdfium.GetFieldScript(0, fields[0].AnnotIndex, 12), "keystroke script seen by PDFium");
            AssertEqual("AFDate_FormatEx(\"dd/mm/yyyy\");", pdfium.GetFieldScript(0, fields[0].AnnotIndex, 13), "format script seen by PDFium");
            AssertEqual("AFDate_FormatEx(\"d mmm yyyy h:MM tt\");", pdfium.GetFieldScript(0, fields[1].AnnotIndex, 13), "date and time script seen by PDFium");
            AssertEqual("", pdfium.GetFieldScript(0, fields[2].AnnotIndex, 13), "a plain text field has no script");
        }

        bytes = FormService.SetValue(bytes, 0, fields[0].AnnotIndex, "2026-11-25");
        bytes = FormService.SetValue(bytes, 0, fields[1].AnnotIndex, "26 Nov 2026 2:30 PM");
        bytes = FormService.SetValue(bytes, 0, fields[2].AnnotIndex, "2026-11-25");
        var filled = FormService.ReadFields(bytes);
        AssertEqual("25/11/2026", filled[0].Value, "date stored in the field's pattern");
        AssertEqual("26 Nov 2026 2:30 pm", filled[1].Value, "date and time stored in the field's pattern");
        AssertEqual("2026-11-25", filled[2].Value, "a plain text field keeps what was typed");
        File.WriteAllBytes(PathFor("form-dates.pdf"), bytes);

        var byName = FormService.ReadFields(FormService.FillByName(bytes, new Dictionary<string, string> { ["OrderDate"] = "1/2/2027", ["DeliverBy"] = "soon" }, out _));
        AssertEqual("01/02/2027", byName[0].Value, "imported date rewritten in the field's pattern");
        AssertEqual("soon", byName[1].Value, "imported text that is not a date is kept as it is");

        using var flat = PdfiumDocument.Load(PdfiumOps.Flatten(bytes));
        var shown = flat.GetText(0).Text;
        Assert(shown.Contains("25/11/2026") && shown.Contains("26 Nov 2026 2:30 pm"), "the dates are drawn on the page");

        var sanitized = FormService.ReadFields(DocumentService.Sanitize(bytes, new SanitizeOptions()));
        Assert(sanitized.Count == 0 || sanitized.All(f => !f.IsDate), "removing scripts turns date fields back into plain text fields");
    }
}
