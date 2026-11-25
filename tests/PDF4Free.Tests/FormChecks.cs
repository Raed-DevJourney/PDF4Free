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
}
