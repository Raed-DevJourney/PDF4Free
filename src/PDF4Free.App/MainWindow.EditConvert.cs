using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using PDF4Free.App.Dialogs;
using PDF4Free.App.Services;
using PDF4Free.App.Views;
using PDF4Free.Core;
using PDF4Free.Core.Conversion;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.App;

/// <summary>The Edit, Convert and Forms tabs: page design, export, OCR, compression and form data.</summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------ pictures

    /// <summary>Reads a picture file and arms the Place Image tool with it.</summary>
    private void ArmPicture(byte[] image, bool asStamp, string status)
    {
        if (Viewer is null) return;
        BitmapSource decoded;
        try
        {
            decoded = BitmapDecoder.Create(new MemoryStream(image), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            Ui.Warn("That file is not a picture PDF4Free can read.");
            return;
        }
        ShowViewer();
        Viewer.Options.Image = image;
        Viewer.Options.ImageAspect = (double)decoded.PixelWidth / Math.Max(1, decoded.PixelHeight);
        Viewer.Options.ImageAsStamp = asStamp;
        Viewer.Tool = ViewerTool.PlaceImage;
        RefreshToolUi();
        SetStatus(status);
    }

    private void AddImageCommand()
    {
        var path = Ui.PickOpen(Ui.ImageFilter, "Add image");
        if (path is not null) ArmPicture(File.ReadAllBytes(path), asStamp: false, "Click on the page to place the picture, or drag to set its size.");
    }

    private void PictureStampCommand()
    {
        var path = Ui.PickOpen(Ui.ImageFilter, "Picture stamp");
        if (path is not null) ArmPicture(File.ReadAllBytes(path), asStamp: true, "Click on the page to place the picture stamp. It stays a movable comment.");
    }

    // ------------------------------------------------------------------ page design

    private async Task WatermarkAsync()
    {
        if (Session is null) return;
        var dialog = new FormDialog("Watermark", this, "Add");
        var text = dialog.AddText("Text", "CONFIDENTIAL");
        var image = dialog.AddFile("…or a picture", Ui.ImageFilter);
        var font = dialog.AddFont("Font", "Arial");
        var size = dialog.AddNumber("Font size (pt)", 60, 6, 400);
        var color = dialog.AddColor("Colour", 0xD32F2F);
        var opacity = dialog.AddSlider("Opacity", 0.25, 0.05, 1);
        var angle = dialog.AddNumber("Angle (degrees)", 45, -180, 180);
        var scale = dialog.AddSlider("Picture width (share of page)", 0.5, 0.1, 1);
        var layer = dialog.AddChoice("Position", ["In front of the page content", "Behind the page content"], 0);
        var tile = dialog.AddCheck("Repeat across the page", false);
        var pages = dialog.AddPageRange("Pages", Session.PageCount);
        dialog.Validate(() => image.Value.Length > 0 && !File.Exists(image.Value) ? "The picture file does not exist." : null);
        dialog.Validate(() => image.Value.Length == 0 && string.IsNullOrWhiteSpace(text.Value) ? "Enter the watermark text or choose a picture." : null);
        if (!dialog.Ask()) return;
        var options = new WatermarkOptions
        {
            Text = text.Value,
            Image = image.Value.Length > 0 ? await File.ReadAllBytesAsync(image.Value) : null,
            Style = new TextStyle(font.Value, size.Value, Bold: true, Color: color.Value),
            Opacity = opacity.Value,
            RotationDegrees = angle.Value,
            ImageScale = scale.Value,
            Tile = tile.Value,
            Layer = layer.Value == 0 ? WatermarkLayer.InFront : WatermarkLayer.Behind,
            Pages = pages.Value,
        };
        await Do("Add watermark", b => ContentService.AddWatermark(b, options));
    }

    private async Task HeaderFooterAsync()
    {
        if (Session is null) return;
        var dialog = new FormDialog("Header & footer", this, "Add");
        dialog.AddNote("Placeholders you can use in any box:  " + HeaderFooterOptions.Placeholders);
        var headerLeft = dialog.AddText("Header, left", "");
        var headerCenter = dialog.AddText("Header, centre", "");
        var headerRight = dialog.AddText("Header, right", "");
        var footerLeft = dialog.AddText("Footer, left", "");
        var footerCenter = dialog.AddText("Footer, centre", "Page {page} of {pages}");
        var footerRight = dialog.AddText("Footer, right", "");
        var font = dialog.AddFont("Font", "Arial");
        var size = dialog.AddNumber("Font size (pt)", 10, 5, 48);
        var color = dialog.AddColor("Colour", 0x000000);
        var margin = dialog.AddNumber("Distance from the page edge (pt)", 36, 6, 200);
        var first = dialog.AddNumber("First page number", 1, 0, 100000);
        var pages = dialog.AddPageRange("Pages", Session.PageCount);
        if (!dialog.Ask()) return;
        var options = new HeaderFooterOptions
        {
            HeaderLeft = headerLeft.Value, HeaderCenter = headerCenter.Value, HeaderRight = headerRight.Value,
            FooterLeft = footerLeft.Value, FooterCenter = footerCenter.Value, FooterRight = footerRight.Value,
            Style = new TextStyle(font.Value, size.Value, Color: color.Value),
            MarginTop = margin.Value, MarginBottom = margin.Value,
            StartPageNumber = (int)first.Value,
            FileName = Session.DisplayName,
            Pages = pages.Value,
        };
        await Do("Add header and footer", b => ContentService.AddHeaderFooter(b, options));
    }

    private async Task BatesAsync()
    {
        if (Session is null) return;
        var dialog = new FormDialog("Bates numbering", this, "Add");
        var prefix = dialog.AddText("Prefix", "DOC-");
        var start = dialog.AddNumber("Start at", 1, 0, 999999999);
        var digits = dialog.AddNumber("Number of digits", 6, 1, 12);
        var suffix = dialog.AddText("Suffix", "");
        var position = dialog.AddChoice("Position", ["Bottom right", "Bottom centre", "Bottom left", "Top right", "Top centre", "Top left"], 0);
        var size = dialog.AddNumber("Font size (pt)", 10, 5, 36);
        var color = dialog.AddColor("Colour", 0x000000);
        var pages = dialog.AddPageRange("Pages", Session.PageCount);
        if (!dialog.Ask()) return;
        string At(int slot) => position.Value == slot ? "{bates}" : "";
        var options = new HeaderFooterOptions
        {
            FooterRight = At(0), FooterCenter = At(1), FooterLeft = At(2), HeaderRight = At(3), HeaderCenter = At(4), HeaderLeft = At(5),
            Style = new TextStyle("Arial", size.Value, Color: color.Value),
            BatesPrefix = prefix.Value, BatesSuffix = suffix.Value, BatesDigits = (int)digits.Value, BatesStart = (int)start.Value,
            Pages = pages.Value,
        };
        var last = options.BatesPrefix + (options.BatesStart + pages.Value.Count - 1).ToString("D" + options.BatesDigits, CultureInfo.InvariantCulture) + options.BatesSuffix;
        if (await Do("Add Bates numbers", b => ContentService.AddHeaderFooter(b, options))) SetStatus($"Bates numbers added. The last number used is {last}.");
    }

    private async Task BackgroundAsync()
    {
        if (Session is null) return;
        var dialog = new FormDialog("Page background", this, "Apply");
        dialog.AddNote("The colour is painted behind the page content. It does not show through scanned pages, which are opaque pictures.");
        var color = dialog.AddColor("Colour", 0xFFF8E1);
        var pages = dialog.AddPageRange("Pages", Session.PageCount);
        if (!dialog.Ask()) return;
        var chosen = color.Value;
        var range = pages.Value;
        await Do("Add background", b => ContentService.AddBackground(b, chosen, range));
    }

    private async Task RemoveCropAsync()
    {
        if (Session is null) return;
        var all = Enumerable.Range(0, Session.PageCount).ToList();
        await Do("Remove crop", b => PageService.RemoveCrop(b, all), ChangeKind.Structure);
    }

    private async Task FlattenAsync()
    {
        if (Session is null) return;
        if (!Ui.Confirm("Flattening makes all comments and form fields a permanent part of the pages. They can no longer be edited or filled in afterwards.\n\nFlatten the whole document?")) return;
        await Do("Flatten", b => PdfiumOps.Flatten(b));
    }

    // ------------------------------------------------------------------ export

    private (List<int> Pages, string Path)? AskExport(string title, string filter, string extension, Action<FormDialog>? extra = null)
    {
        if (Session is null) return null;
        var dialog = new FormDialog(title, this, "Export");
        var pages = dialog.AddPageRange("Pages", Session.PageCount);
        extra?.Invoke(dialog);
        if (!dialog.Ask()) return null;
        var path = Ui.PickSave(filter, Path.GetFileNameWithoutExtension(Session.DisplayName) + extension);
        return path is null ? null : (pages.Value, path);
    }

    private async Task ExportToFileAsync(string description, string path, Action<PdfiumDocument> export)
    {
        var session = Session!;
        if (await RunBusyAsync(description, () => Task.Run(() => export(session.Document))) && Ui.Confirm($"Saved {Path.GetFileName(path)}.\n\nOpen it now?"))
        {
            Ui.ShellOpen(path);
        }
    }

    private bool EnsureMayExtract()
    {
        if (Session is null) return false;
        if (Session.Document.Security.CanCopy) return true;
        Ui.Warn("The author of this document has not allowed its content to be copied or extracted.");
        return false;
    }

    private async Task ExportWordAsync()
    {
        if (!EnsureMayExtract()) return;
        Field<int>? mode = null;
        var request = AskExport("Export to Word", "Word document (*.docx)|*.docx", ".docx", d =>
        {
            mode = d.AddChoice("Layout", ["Editable text (reflowed paragraphs)", "Exact appearance (each page as a picture)"], 0);
            d.AddNote("Editable text keeps wording, order, font sizes, bold and italics; it does not rebuild tables, columns or pictures.");
        });
        if (request is not { } r) return;
        var layout = mode!.Value == 0 ? WordExportMode.EditableText : WordExportMode.PageImages;
        await ExportToFileAsync("Export to Word", r.Path, doc => ExportService.ToWord(doc, r.Pages, r.Path, layout));
    }

    private async Task ExportExcelAsync()
    {
        if (!EnsureMayExtract()) return;
        var request = AskExport("Export to Excel", "Excel workbook (*.xlsx)|*.xlsx", ".xlsx",
            d => d.AddNote("Each page becomes a worksheet. Text separated by wide gaps is placed in separate columns, which works best for simple tables."));
        if (request is not { } r) return;
        await ExportToFileAsync("Export to Excel", r.Path, doc => ExportService.ToExcel(doc, r.Pages, r.Path));
    }

    private async Task ExportPowerPointAsync()
    {
        var request = AskExport("Export to PowerPoint", "PowerPoint presentation (*.pptx)|*.pptx", ".pptx",
            d => d.AddNote("Each page becomes a slide that shows a picture of the page."));
        if (request is not { } r) return;
        await ExportToFileAsync("Export to PowerPoint", r.Path, doc => ExportService.ToPowerPoint(doc, r.Pages, r.Path));
    }

    private async Task ExportHtmlAsync()
    {
        if (!EnsureMayExtract()) return;
        Field<bool>? pictures = null;
        var request = AskExport("Export to HTML", "Web page (*.html)|*.html", ".html", d => pictures = d.AddCheck("Include a picture of each page", false));
        if (request is not { } r) return;
        var title = Path.GetFileNameWithoutExtension(Session!.DisplayName);
        var withPictures = pictures!.Value;
        await ExportToFileAsync("Export to HTML", r.Path, doc => ExportService.ToHtml(doc, r.Pages, r.Path, withPictures, title));
    }

    private async Task ExportTextAsync()
    {
        if (!EnsureMayExtract()) return;
        var request = AskExport("Export text", "Text file (*.txt)|*.txt", ".txt");
        if (request is not { } r) return;
        await ExportToFileAsync("Export text", r.Path, doc => File.WriteAllText(r.Path, ExportService.ToText(doc, r.Pages), Encoding.UTF8));
    }

    private async Task ExportImagesAsync()
    {
        if (Session is null) return;
        var session = Session;
        var dialog = new FormDialog("Export pages as images", this, "Export");
        var pages = dialog.AddPageRange("Pages", session.PageCount);
        var format = dialog.AddChoice("Format", ["PNG", "JPEG", "TIFF (one multi-page file)", "BMP"], 0);
        var dpi = dialog.AddChoice("Resolution", ["72 dpi (screen)", "150 dpi", "300 dpi (print)", "600 dpi"], 1);
        var quality = dialog.AddSlider("JPEG quality", 90, 30, 100);
        var folder = dialog.AddFolder("Save into", session.FilePath is { } p ? Path.GetDirectoryName(p) ?? "" : "");
        if (!dialog.Ask()) return;
        var chosenFormat = format.Value switch { 1 => ImageExportFormat.Jpeg, 2 => ImageExportFormat.Tiff, 3 => ImageExportFormat.Bmp, _ => ImageExportFormat.Png };
        var resolution = new[] { 72.0, 150, 300, 600 }[dpi.Value];
        var target = folder.Value;
        var baseName = Path.GetFileNameWithoutExtension(session.DisplayName);
        var range = pages.Value;
        var jpegQuality = (int)quality.Value;
        var count = 0;
        if (await RunBusyAsync("Export images", () => Task.Run(() => count = ExportService.ToImages(session.Document, range, target, baseName, chosenFormat, resolution, jpegQuality).Count))
            && Ui.Confirm($"Created {count} file(s) in\n{target}\n\nOpen that folder?"))
        {
            Ui.ShellOpen(target);
        }
    }

    private async Task ExtractImagesAsync()
    {
        if (!EnsureMayExtract()) return;
        var session = Session!;
        var folder = Ui.PickFolder("Save the document's pictures into");
        if (folder is null) return;
        var baseName = Path.GetFileNameWithoutExtension(session.DisplayName);
        var all = Enumerable.Range(0, session.PageCount).ToList();
        var count = 0;
        if (!await RunBusyAsync("Extract pictures", () => Task.Run(() => count = ExportService.ExtractImages(session.Document, all, folder, baseName)))) return;
        if (count == 0) Ui.Info("No pictures were found in this document.");
        else if (Ui.Confirm($"Saved {count} picture(s) to\n{folder}\n\nOpen that folder?")) Ui.ShellOpen(folder);
    }

    // ------------------------------------------------------------------ OCR and compression

    private async Task OcrAsync()
    {
        if (Session is null || Active is not { } tab) return;
        var languages = OcrService.Languages();
        if (languages.Count == 0)
        {
            Ui.Warn("Windows has no text recognition language installed.\n\nAdd one under Settings > Time & language > Language & region, then try again.");
            return;
        }
        var dialog = new FormDialog("Recognize text (OCR)", this, "Recognize");
        dialog.AddNote("Adds an invisible text layer over scanned pages so that they can be searched, selected and copied. The page pictures are not changed.");
        var pages = dialog.AddPageRange("Pages", Session.PageCount);
        var preferred = Math.Max(0, languages.ToList().FindIndex(l => l.Tag == AppSettings.Current.OcrLanguage));
        var language = dialog.AddChoice("Language", [.. languages.Select(l => $"{l.DisplayName} ({l.Tag})")], preferred);
        var force = dialog.AddCheck("Also process pages that already contain text", false);
        if (!dialog.Ask()) return;
        var tag = languages[language.Value].Tag;
        var range = pages.Value;
        var everything = force.Value;
        var source = tab.Session.Bytes;
        (byte[] Result, int Pages, int Words) outcome = (source, 0, 0);
        var progress = new Progress<(int Done, int Total)>(p => SetStatus($"Recognizing text… page {p.Done} of {p.Total}"));
        if (!await RunBusyAsync("Recognize text", async () => outcome = await Task.Run(() => OcrService.AddTextLayerAsync(source, range, tag, everything, progress)))) return;
        if (outcome.Pages == 0)
        {
            Ui.Info("No pages needed text recognition: they already contain text, or nothing readable was found.\n\nTick \"Also process pages that already contain text\" to force it.");
            return;
        }
        if (!ReferenceEquals(tab.Session.Bytes, source))
        {
            Ui.Warn("The document was changed while text recognition was running, so the result was not applied. Please run it again.");
            return;
        }
        var recognised = outcome.Result;
        await RunEditAsync(tab, "Recognize text", _ => recognised, ChangeKind.Content);
        SetStatus($"Recognized {outcome.Words} words on {outcome.Pages} page(s). The text is now searchable.");
    }

    private async Task OcrCopyAsync()
    {
        if (Session is null || Viewer is null) return;
        if (!EnsureMayExtract()) return;
        var session = Session;
        var page = Viewer.CurrentPage;
        var language = AppSettings.Current.OcrLanguage;
        var text = "";
        if (!await RunBusyAsync("Recognize page text", async () => text = await Task.Run(() => OcrService.ReadPageTextAsync(session.Document, page, language)))) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            Ui.Info("No readable text was found on this page.");
            return;
        }
        Clipboard.SetText(text);
        SetStatus($"Copied the recognized text of page {page + 1} ({text.Length} characters).");
    }

    private async Task CompressAsync()
    {
        if (Session is null || Active is not { } tab) return;
        var dialog = new FormDialog("Compress PDF", this, "Compress");
        dialog.AddNote($"Current size: {FormatSize(tab.Session.Bytes.Length)}. Pictures are downsampled and recompressed; text and vector graphics stay sharp.");
        var level = dialog.AddChoice("Picture quality", ["High quality (larger file)", "Balanced", "Smallest file (lower picture quality)"], 1);
        var metadata = dialog.AddCheck("Also remove document metadata", false);
        if (!dialog.Ask()) return;
        var options = (level.Value switch { 0 => OptimizeOptions.HighQuality, 2 => OptimizeOptions.SmallestFile, _ => OptimizeOptions.Balanced }) with { RemoveMetadata = metadata.Value };
        OptimizeReport? report = null;
        if (!await RunEditAsync(tab, "Compress", b => OptimizeService.Optimize(b, options, out report), ChangeKind.Content) || report is null) return;
        if (report.BytesAfter >= report.BytesBefore) Ui.Info("This document is already compact: compressing it would not make it smaller, so it was left unchanged.");
        else Ui.Info($"Size reduced from {FormatSize(report.BytesBefore)} to {FormatSize(report.BytesAfter)} ({report.SavedPercent:0}% smaller).\n{report.ImagesRecompressed} of {report.ImagesSeen} pictures were recompressed.\n\nSave the document to keep the smaller version, or press Undo to go back.");
    }

    // ------------------------------------------------------------------ form data

    private async Task ResetFormAsync()
    {
        if (Session is null || Viewer is null || Viewer.Fields.Count == 0) return;
        if (!Ui.Confirm("Clear the values of all form fields?")) return;
        await Do("Reset form", FormService.Reset);
    }

    private void ExportFormDataCommand()
    {
        if (Session is null || Viewer is null) return;
        var rows = FormValues(Viewer.Fields);
        if (rows.Count == 0)
        {
            Ui.Info("This document has no form fields.");
            return;
        }
        var path = Ui.PickSave("CSV file (*.csv)|*.csv", Path.GetFileNameWithoutExtension(Session.DisplayName) + " - form data.csv");
        if (path is null) return;
        var csv = new StringBuilder("Field,Value\r\n");
        foreach (var (name, value) in rows) csv.Append(CsvCell(name)).Append(',').Append(CsvCell(value)).Append("\r\n");
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
        SetStatus($"Saved {rows.Count} field values to {Path.GetFileName(path)}.");
    }

    /// <summary>One value per field name; a radio group reports the value of its selected button.</summary>
    private static List<(string Name, string Value)> FormValues(IReadOnlyList<FormFieldInfo> fields)
    {
        var values = new Dictionary<string, string>();
        foreach (var field in fields)
        {
            if (field.Type is FormFieldType.Button or FormFieldType.Signature or FormFieldType.Unknown) continue;
            var value = field.Type switch
            {
                FormFieldType.CheckBox => field.Checked ? "true" : "false",
                FormFieldType.Radio => field.Value == "Off" ? "" : field.Value,
                _ => field.Value,
            };
            values[field.Name] = value;
        }
        return [.. values.Select(v => (v.Key, v.Value))];
    }

    private static string CsvCell(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private async Task ImportFormDataAsync()
    {
        if (Session is null) return;
        var path = Ui.PickOpen("CSV file (*.csv)|*.csv|All files|*.*", "Import form data");
        if (path is null) return;
        var values = new Dictionary<string, string>();
        foreach (var record in ParseCsv(await File.ReadAllTextAsync(path)).Skip(1))
        {
            if (record.Count >= 2 && record[0].Length > 0) values[record[0]] = record[1];
        }
        if (values.Count == 0)
        {
            Ui.Warn("No values were found. The file needs two columns: the field name and its value, below a header row.");
            return;
        }
        var filled = 0;
        if (await Do("Import form data", b => FormService.FillByName(b, values, out filled))) SetStatus($"Filled {filled} field(s) from {Path.GetFileName(path)}.");
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',')
            {
                record.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(cell.ToString());
                cell.Clear();
                if (record.Any(v => v.Length > 0)) records.Add(record);
                record = [];
            }
            else cell.Append(c);
        }
        record.Add(cell.ToString());
        if (record.Any(v => v.Length > 0)) records.Add(record);
        return records;
    }
}
