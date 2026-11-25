using System.Text;
using System.Text.Json;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Tests;

/// <summary>
/// Reads a saved PDF form with the engine alone (no application involved) and writes what it finds as JSON:
/// every field with its stored value, plus the text each page shows once the form is flattened. The flattened text
/// is what a reader displays, so it proves the field appearances were written, not just the values.
///
/// Usage: PDF4Free.Tests dump-form &lt;file.pdf&gt; &lt;result.json&gt;
/// </summary>
public static class FormDump
{
    public static int Run(string pdfPath, string jsonPath)
    {
        var bytes = File.ReadAllBytes(pdfPath);
        var fields = FormService.ReadFields(bytes);
        var staticText = new List<string>();
        var flattenedText = new List<string>();
        using (var original = PdfiumDocument.Load(bytes))
        {
            for (var p = 0; p < original.PageCount; p++) staticText.Add(original.GetText(p).Text);
        }
        using (var flattened = PdfiumDocument.Load(PdfiumOps.Flatten(bytes)))
        {
            for (var p = 0; p < flattened.PageCount; p++) flattenedText.Add(flattened.GetText(p).Text);
        }
        var result = new
        {
            file = Path.GetFileName(pdfPath),
            bytes = bytes.Length,
            pages = staticText.Count,
            fields = fields.Select(f => new
            {
                name = f.Name,
                type = f.Type.ToString(),
                value = f.Value,
                isChecked = f.Checked,
                onState = f.OnState,
                options = f.Options.Select(o => o.Display).ToArray(),
                multiline = f.Multiline,
                page = f.PageIndex,
            }),
            staticText,
            flattenedText,
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"{fields.Count} fields read from {Path.GetFileName(pdfPath)}");
        return 0;
    }
}
