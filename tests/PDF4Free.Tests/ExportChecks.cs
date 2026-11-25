using System.IO.Compression;
using System.Xml.Linq;
using PDF4Free.Core;
using PDF4Free.Core.Conversion;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Tests;

public sealed partial class TestRunner
{
    private static void AssertWellFormedPackage(string path, params string[] requiredParts)
    {
        using var zip = ZipFile.OpenRead(path);
        foreach (var part in requiredParts) Assert(zip.GetEntry(part) is not null, $"{Path.GetFileName(path)} contains {part}");
        foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".xml", StringComparison.Ordinal) || e.Name.EndsWith(".rels", StringComparison.Ordinal)))
        {
            using var stream = entry.Open();
            XDocument.Load(stream);
        }
    }

    [Check]
    public void Export_to_images_text_word_excel_powerpoint_and_html()
    {
        using var doc = PdfiumDocument.Load(Samples.Basic(2));
        int[] all = [0, 1];
        var files = ExportService.ToImages(doc, all, PathFor("export"), "page", ImageExportFormat.Jpeg, 72);
        AssertEqual(2, files.Count, "one JPEG per page");
        Assert(new FileInfo(files[0]).Length > 2000, "JPEG has content");
        AssertEqual(1, ExportService.ToImages(doc, all, PathFor("export"), "pages", ImageExportFormat.Tiff, 50).Count, "one multi-page TIFF");
        Assert(ExportService.ToText(doc, all).Contains("quick brown fox"), "text export");

        var docx = PathFor("export.docx");
        ExportService.ToWord(doc, all, docx, WordExportMode.EditableText);
        AssertWellFormedPackage(docx, "word/document.xml", "[Content_Types].xml", "_rels/.rels");
        using (var back = PdfiumDocument.Load(DocxConverter.Convert(docx)))
        {
            AssertEqual(2, back.PageCount, "Word export keeps page breaks");
            Assert(back.GetText(0).Text.Contains("quick brown fox") && back.GetText(1).Text.Contains("Page 2 heading"), "Word export round-trips its text");
        }
        var docxImages = PathFor("export-images.docx");
        ExportService.ToWord(doc, all, docxImages, WordExportMode.PageImages);
        AssertWellFormedPackage(docxImages, "word/document.xml", "word/media/page1.jpeg", "word/_rels/document.xml.rels");

        var xlsx = PathFor("export.xlsx");
        ExportService.ToExcel(doc, all, xlsx);
        AssertWellFormedPackage(xlsx, "xl/workbook.xml", "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml", "xl/_rels/workbook.xml.rels");

        var pptx = PathFor("export.pptx");
        ExportService.ToPowerPoint(doc, all, pptx, 72);
        AssertWellFormedPackage(pptx, "ppt/presentation.xml", "ppt/slides/slide2.xml", "ppt/media/image2.jpeg", "ppt/theme/theme1.xml", "ppt/slideLayouts/slideLayout1.xml");

        var html = PathFor("export.html");
        ExportService.ToHtml(doc, all, html, includePageImages: true, "Sample");
        var markup = File.ReadAllText(html);
        Assert(markup.Contains("<h1>Page 1 heading</h1>") && markup.Contains("quick brown fox") && markup.Contains("data:image/jpeg;base64,"), "HTML export has headings, text and page images");

        foreach (var app in Enum.GetValues<OfficeApp>()) Console.WriteLine($"      Microsoft {app} installed: {OfficeAutomation.IsAvailable(app)}");
    }

    [Check]
    public void Office_opens_the_exported_files_when_it_is_installed()
    {
        foreach (var (file, app, needle) in new[]
                 {
                     ("export.docx", OfficeApp.Word, "quick brown fox"),
                     ("export-images.docx", OfficeApp.Word, ""),
                     ("export.xlsx", OfficeApp.Excel, "quick brown fox"),
                     ("export.pptx", OfficeApp.PowerPoint, ""),
                 })
        {
            if (!OfficeAutomation.IsAvailable(app) || !File.Exists(PathFor(file)))
            {
                Console.WriteLine($"      {file}: not verified (Microsoft {app} not installed)");
                continue;
            }
            using var pdf = PdfiumDocument.Load(OfficeAutomation.Convert(PathFor(file), app));
            Console.WriteLine($"      {file}: Microsoft {app} opened it and produced {pdf.PageCount} page(s)");
            Assert(pdf.PageCount >= 2, $"{file} has both pages");
            if (file == "export-images.docx") AssertEqual(2, pdf.PageCount, "page-image export has exactly one page per page");
            if (needle.Length > 0) Assert(pdf.GetText(0).Text.Contains(needle), $"{file} text survived");
        }
    }

    [Check]
    public void Session_tracks_edits_undo_redo_and_saving()
    {
        var path = PathFor("session.pdf");
        File.WriteAllBytes(path, Samples.Basic(3));
        using var session = PdfSession.Open(path, _ => null);
        var changes = new List<ChangeKind>();
        session.Changed += changes.Add;
        Assert(!session.IsDirty && !session.CanUndo, "fresh session is clean");
        session.Apply("Delete page", b => PageService.Delete(b, [1]), ChangeKind.Structure);
        session.Apply("Rotate", b => PageService.Rotate(b, [0], 90), ChangeKind.Structure);
        AssertEqual(2, session.PageCount, "page deleted");
        Assert(session.IsDirty && session.CanUndo && session.UndoDescription == "Rotate", "undo state");
        session.Undo();
        AssertEqual(0, session.Document.GetGeometry(0).Rotation, "rotation undone");
        session.Undo();
        AssertEqual(3, session.PageCount, "delete undone");
        Assert(!session.CanUndo && session.CanRedo, "redo available");
        session.Redo();
        AssertEqual(2, session.PageCount, "delete redone");
        session.Save();
        Assert(!session.IsDirty, "saved");
        using (var reloaded = PdfiumDocument.Load(File.ReadAllBytes(path))) AssertEqual(2, reloaded.PageCount, "file on disk updated");
        AssertEqual(5, changes.Count, "change notifications (two edits, two undos, one redo)");

        var locked = PathFor("session-locked.pdf");
        File.WriteAllBytes(locked, DocumentService.Encrypt(Samples.Basic(1), new EncryptionOptions("", "owner", AllowModify: false)));
        using var restricted = PdfSession.Open(locked, _ => null);
        Assert(restricted.IsRestricted, "restricted session");
        try
        {
            restricted.Apply("Rotate", b => PageService.Rotate(b, [0], 90));
            Assert(false, "restricted documents refuse edits");
        }
        catch (InvalidOperationException)
        {
        }
        Assert(!restricted.TryUnlock("wrong"), "wrong unlock password");
        Assert(restricted.TryUnlock("owner") && !restricted.IsRestricted && restricted.SecurityRemoved, "unlocked with the permissions password");
        restricted.Apply("Rotate", b => PageService.Rotate(b, [0], 90), ChangeKind.Structure);
        AssertEqual(1, restricted.Document.GetGeometry(0).Rotation, "editable after unlock");

        var asked = 0;
        File.WriteAllBytes(locked, DocumentService.Encrypt(Samples.Basic(1), new EncryptionOptions("open", "owner")));
        using var opened = PdfSession.Open(locked, wrong => asked++ == 0 ? "bad" : "owner");
        AssertEqual(2, asked, "asked again after a wrong password");
        Assert(!opened.IsRestricted && opened.SecurityRemoved, "owner password unlocks on open");
    }
}
