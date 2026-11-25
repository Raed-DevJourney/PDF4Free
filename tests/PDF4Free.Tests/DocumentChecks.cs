using System.Windows;
using PDF4Free.Core.Conversion;
using PDF4Free.Core.Editing;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Tests;

public sealed partial class TestRunner
{
    [Check]
    public void Passwords_and_permissions_are_enforced()
    {
        var protectedBytes = DocumentService.Encrypt(Samples.Basic(2), new EncryptionOptions("open-me", "owner-me", AllowPrint: true, AllowCopy: false, AllowModify: false, AllowAnnotate: false, AllowFillForms: false));
        File.WriteAllBytes(PathFor("protected.pdf"), protectedBytes);
        try
        {
            PdfiumDocument.Load(protectedBytes).Dispose();
            Assert(false, "should need a password");
        }
        catch (PdfPasswordRequiredException ex)
        {
            Assert(!ex.WrongPassword, "first attempt is not 'wrong password'");
        }
        try
        {
            PdfiumDocument.Load(protectedBytes, "nope").Dispose();
            Assert(false, "wrong password should fail");
        }
        catch (PdfPasswordRequiredException ex)
        {
            Assert(ex.WrongPassword, "wrong password flagged");
        }
        using (var user = PdfiumDocument.Load(protectedBytes, "open-me"))
        {
            Assert(user.Security.IsEncrypted && !user.Security.OwnerAccess, "user access only");
            Assert(user.Security.CanPrint && !user.Security.CanCopy && !user.Security.CanModify, "permission flags");
            Assert(user.Security.Revision >= 5, $"AES-256 revision ({user.Security.Revision})");
            Assert(user.GetText(0).Text.Contains("quick brown fox"), "content readable with password");
        }
        try
        {
            PdfiumOps.RemoveSecurity(protectedBytes, "open-me");
            Assert(false, "user password must not unlock editing");
        }
        catch (UnauthorizedAccessException)
        {
        }
        var unlocked = PdfiumOps.RemoveSecurity(protectedBytes, "owner-me");
        using (var open = PdfiumDocument.Load(unlocked))
        {
            Assert(!open.Security.IsEncrypted, "security removed with the owner password");
        }
        // Permissions-only protection: opens without a password but stays restricted.
        var restricted = DocumentService.Encrypt(Samples.Basic(1), new EncryptionOptions("", "owner", AllowCopy: false, Aes256: false));
        using var r = PdfiumDocument.Load(restricted);
        Assert(r.Security.IsEncrypted && !r.Security.OwnerAccess && !r.Security.CanCopy && r.Security.CanPrint, "permissions-only document");
    }

    [Check]
    public void Properties_bookmarks_and_fonts()
    {
        var bytes = Samples.Basic(3);
        bytes = DocumentService.WriteInfo(bytes, new DocumentInfo("New title", "Sam Writer", "Subject here", "alpha, beta", "", "", "", ""));
        bytes = DocumentService.AddBookmark(bytes, [], "Chapter 1", 0);
        bytes = DocumentService.AddBookmark(bytes, [], "Chapter 2", 2);
        bytes = DocumentService.AddBookmark(bytes, [0], "Section 1.1", 1);
        using (var doc = PdfiumDocument.Load(bytes))
        {
            var info = DocumentService.ReadInfo(doc);
            AssertEqual("New title", info.Title, "title");
            AssertEqual("alpha, beta", info.Keywords, "keywords");
            Assert(info.Created.StartsWith("20"), $"created date formatted ({info.Created})");
            var marks = doc.GetBookmarks();
            AssertEqual(2, marks.Count, "top-level bookmarks");
            AssertEqual("Section 1.1", marks[0].Children[0].Title, "child bookmark");
            AssertEqual(1, marks[0].Children[0].PageIndex, "child target");
            AssertEqual(2, marks[1].PageIndex, "second target");
        }
        bytes = DocumentService.RenameBookmark(bytes, [0, 0], "Renamed");
        bytes = DocumentService.DeleteBookmark(bytes, [1]);
        using (var doc = PdfiumDocument.Load(bytes))
        {
            var marks = doc.GetBookmarks();
            AssertEqual(1, marks.Count, "after delete");
            AssertEqual("Renamed", marks[0].Children[0].Title, "after rename");
        }
        var fonts = DocumentService.ListFonts(bytes);
        Assert(fonts.Any(f => f.Name.Contains("Arial")) && fonts.Any(f => f.Name.Contains("Times")), "fonts listed: " + string.Join(", ", fonts.Select(f => f.Name)));
        Assert(fonts.All(f => f.Embedded), "fonts embedded");
    }

    [Check]
    public void Search_finds_text_and_patterns()
    {
        using var doc = PdfiumDocument.Load(Samples.Basic(2));
        var hits = TextFinder.Search(doc, "quick brown", new SearchOptions()).ToList();
        AssertEqual(2, hits.Count, "one hit per page");
        Assert(hits[0].Context.Contains("quick brown fox"), "context snippet");
        AssertEqual(0, TextFinder.Search(doc, "QUICK", new SearchOptions(MatchCase: true)).Count(), "case sensitive");
        AssertEqual(0, TextFinder.Search(doc, "quic", new SearchOptions(WholeWord: true)).Count(), "whole word");
        AssertEqual(2, TextFinder.Search(doc, TextFinder.Patterns["Email addresses"], new SearchOptions(Regex: true)).Count(), "emails");
        AssertEqual(2, TextFinder.Search(doc, TextFinder.Patterns["Credit card numbers"], new SearchOptions(Regex: true)).Count(), "card numbers");
        AssertEqual(2, TextFinder.Search(doc, TextFinder.Patterns["Phone numbers"], new SearchOptions(Regex: true)).Count(), "phone numbers");
    }

    [Check]
    public void Attachments_roundtrip()
    {
        var bytes = PdfiumOps.AddAttachment(Samples.Basic(1), "notes.txt", "hello attachment"u8.ToArray());
        using (var doc = PdfiumDocument.Load(bytes))
        {
            var list = doc.GetAttachments();
            AssertEqual(1, list.Count, "attachment count");
            AssertEqual("notes.txt", list[0].Name, "attachment name");
            AssertEqual("hello attachment", System.Text.Encoding.UTF8.GetString(doc.GetAttachmentData(0)), "attachment data");
        }
        // The attachment must survive a PDFsharp edit and be removable.
        bytes = PageService.Rotate(bytes, [0], 90);
        using (var doc = PdfiumDocument.Load(bytes)) AssertEqual(1, doc.GetAttachments().Count, "kept after an edit");
        bytes = PdfiumOps.DeleteAttachment(bytes, 0);
        using (var doc = PdfiumDocument.Load(bytes)) AssertEqual(0, doc.GetAttachments().Count, "deleted");
    }

    [Check]
    public void Sanitize_removes_hidden_information()
    {
        var bytes = AnnotateEverything(FormSample(), 0);
        bytes = PdfiumOps.AddAttachment(bytes, "secret.txt", "x"u8.ToArray());
        bytes = DocumentService.AddBookmark(bytes, [], "Mark", 0);
        var clean = DocumentService.Sanitize(bytes, new SanitizeOptions(Bookmarks: true, FlattenForms: true));
        using var doc = PdfiumDocument.Load(clean);
        AssertEqual("", doc.GetMeta("Title"), "title removed");
        AssertEqual("", doc.GetMeta("Author"), "author removed");
        AssertEqual(0, doc.GetAttachments().Count, "attachments removed");
        AssertEqual(0, doc.GetBookmarks().Count, "bookmarks removed");
        AssertEqual(0, doc.GetAnnotations(0).Count, "comments removed and fields flattened");
    }

    [Check]
    public void Redaction_destroys_the_marked_content()
    {
        var bytes = Samples.Basic(2);
        using (var doc = PdfiumDocument.Load(bytes))
        {
            var hits = TextFinder.Search(doc, TextFinder.Patterns["Credit card numbers"], new SearchOptions(Regex: true), [0]).ToList();
            bytes = RedactionService.MarkHits(bytes, hits, "tester", out var marked);
            AssertEqual(1, marked, "one area marked");
        }
        AssertEqual(1, RedactionService.ReadMarks(bytes).Count, "marks are stored in the file");
        var redacted = RedactionService.Apply(bytes, out var affected);
        File.WriteAllBytes(PathFor("redacted.pdf"), redacted);
        AssertEqual("0", string.Join(",", affected), "affected pages");
        Assert(!ContainsAscii(redacted, "4111"), "card number is gone from the raw file");
        using var after = PdfiumDocument.Load(redacted);
        AssertEqual("", after.GetText(0).Text.Trim(), "redacted page has no text left");
        Assert(after.GetText(1).Text.Contains("4111 1111"), "other pages untouched");
        AssertEqual(0, after.GetAnnotations(0).Count, "marks consumed");
        var g = after.GetGeometry(0);
        Assert(Math.Abs(g.ViewWidth - 612) < 0.5 && Math.Abs(g.ViewHeight - 792) < 0.5, "page size kept");
        SavePng(after.RenderBitmap(0, 80), "redacted.png");
    }

    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        var n = System.Text.Encoding.ASCII.GetBytes(needle);
        for (var i = 0; i <= haystack.Length - n.Length; i++)
        {
            var match = true;
            for (var j = 0; j < n.Length && match; j++) match = haystack[i + j] == n[j];
            if (match) return true;
        }
        return false;
    }

    [Check]
    public async Task Ocr_makes_image_pages_searchable()
    {
        if (OcrService.Languages().Count == 0)
        {
            Console.WriteLine("      (skipped: no Windows OCR language installed)");
            return;
        }
        Console.WriteLine("      OCR languages: " + string.Join(", ", OcrService.Languages().Select(l => l.Tag)));
        byte[] scanned;
        using (var doc = PdfiumDocument.Load(Samples.Basic(1)))
        {
            scanned = CreatePdf.FromBitmap(doc.RenderBitmap(0, 200));
        }
        using (var image = PdfiumDocument.Load(scanned))
        {
            AssertEqual("", image.GetText(0).Text.Trim(), "image-only page has no text");
            Assert(Math.Abs(image.GetGeometry(0).ViewWidth - 612) < 1, "image page keeps the physical size");
        }
        var (result, pages, words) = await OcrService.AddTextLayerAsync(scanned, [0], "en-US");
        File.WriteAllBytes(PathFor("ocr.pdf"), result);
        AssertEqual(1, pages, "pages recognised");
        Assert(words > 20, $"words recognised ({words})");
        using var searchable = PdfiumDocument.Load(result);
        using (var picture = PdfiumDocument.Load(scanned))
        {
            var difference = MeanDifference(picture.RenderBitmap(0, 100), searchable.RenderBitmap(0, 100));
            Assert(difference < 0.05, $"the text layer must be invisible: the page should look exactly as before (mean pixel difference {difference:0.000})");
        }
        var text = searchable.GetText(0).Text;
        Assert(text.Contains("quick brown fox"), "recognised text is searchable: " + text.Replace("\r\n", " | "));
        var hit = TextFinder.Search(searchable, "lazy dog", new SearchOptions()).First();
        var where = searchable.GetGeometry(0).ToView(hit.Rects[0]);
        Assert(Math.Abs(where.Top - 172) < 14 && where.Left > 200 && where.Left < 320, $"hidden text sits over the picture of the words ({where})");
        // A second pass must not stack another layer on a page that already has text.
        var (_, again, _) = await OcrService.AddTextLayerAsync(result, [0], "en-US");
        AssertEqual(0, again, "pages with text are skipped");
    }

    [Check]
    public void Create_pdf_from_text_images_and_docx()
    {
        using (var doc = PdfiumDocument.Load(CreatePdf.FromText("First line\n    indented line\nThird\tTabbed", monospace: true, title: "Notes")))
        {
            var text = doc.GetText(0).Text;
            Assert(text.Contains("Notes") && text.Contains("indented line") && text.Contains("Tabbed"), "text file converted");
        }
        using (var doc = PdfiumDocument.Load(CreatePdf.Blank(300, 400, 2)))
        {
            AssertEqual(2, doc.PageCount, "blank pages");
            Assert(Math.Abs(doc.GetGeometry(1).ViewHeight - 400) < 0.01, "blank size");
        }
        var docxPath = PathFor("sample.docx");
        File.WriteAllBytes(docxPath, DocxSample.Build());
        var pdf = DocxConverter.Convert(docxPath);
        File.WriteAllBytes(PathFor("from-docx.pdf"), pdf);
        using var converted = PdfiumDocument.Load(pdf);
        var body = converted.GetText(0).Text;
        Assert(body.Contains("Quarterly Report") && body.Contains("bold words") && body.Contains("Cell B2"), "docx text, runs and table converted");
        SavePng(converted.RenderBitmap(0, 70), "from-docx.png");
    }
}
