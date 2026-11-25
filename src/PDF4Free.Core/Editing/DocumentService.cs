using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Core.Editing;

public sealed record EncryptionOptions(
    string OpenPassword,
    string PermissionsPassword,
    bool AllowPrint = true,
    bool AllowCopy = true,
    bool AllowModify = true,
    bool AllowAnnotate = true,
    bool AllowFillForms = true,
    bool Aes256 = true);

public sealed record SanitizeOptions(
    bool Metadata = true,
    bool JavaScript = true,
    bool Attachments = true,
    bool Comments = true,
    bool Links = false,
    bool Bookmarks = false,
    bool Thumbnails = true,
    bool FlattenForms = false);

public sealed record DocumentInfo(string Title, string Author, string Subject, string Keywords, string Creator, string Producer, string Created, string Modified);

public sealed record FontUse(string Name, string Type, bool Embedded);

/// <summary>Document-wide operations: security, hidden-information removal, properties and bookmarks.</summary>
public static class DocumentService
{
    // ------------------------------------------------------------------ security

    public static byte[] Encrypt(byte[] source, EncryptionOptions options) =>
        PdfIO.Modify(source, doc =>
        {
            var owner = string.IsNullOrEmpty(options.PermissionsPassword) ? options.OpenPassword : options.PermissionsPassword;
            if (string.IsNullOrEmpty(owner)) throw new InvalidOperationException("Enter at least one password.");
            var security = doc.SecuritySettings;
            if (!string.IsNullOrEmpty(options.OpenPassword)) security.UserPassword = options.OpenPassword;
            security.OwnerPassword = owner;
            security.PermitPrint = options.AllowPrint;
            security.PermitFullQualityPrint = options.AllowPrint;
            security.PermitExtractContent = options.AllowCopy;
            security.PermitModifyDocument = options.AllowModify;
            security.PermitAssembleDocument = options.AllowModify;
            security.PermitAnnotations = options.AllowAnnotate;
            security.PermitFormsFill = options.AllowFillForms || options.AllowAnnotate;
            if (options.Aes256) doc.SecurityHandler.SetEncryptionToV5();
            else doc.SecurityHandler.SetEncryptionToV4UsingAES();
        });

    // ------------------------------------------------------------------ hidden information

    public static byte[] Sanitize(byte[] source, SanitizeOptions options)
    {
        var cleaned = PdfIO.Modify(source, doc =>
        {
            var catalog = doc.Internals.Catalog;
            if (options.Metadata) RemoveMetadata(doc, catalog);
            if (options.JavaScript) RemoveScripts(doc, catalog);
            if (options.Attachments) RemoveAttachments(catalog);
            if (options.Bookmarks) catalog.Elements.Remove("/Outlines");
            foreach (var page in doc.Pages)
            {
                if (options.Thumbnails) page.Elements.Remove("/Thumb");
                if (options.Metadata)
                {
                    page.Elements.Remove("/Metadata");
                    page.Elements.Remove("/PieceInfo");
                }
                FilterAnnotations(page, options);
            }
        });
        return options.FlattenForms ? PdfiumOps.Flatten(cleaned) : cleaned;
    }

    private static void RemoveMetadata(PdfDocument doc, PdfDictionary catalog)
    {
        catalog.Elements.Remove("/Metadata");
        catalog.Elements.Remove("/PieceInfo");
        foreach (var key in new[] { "/Title", "/Author", "/Subject", "/Keywords", "/Creator", "/Company", "/SourceModified" })
        {
            doc.Info.Elements.Remove(key);
        }
    }

    private static void RemoveScripts(PdfDocument doc, PdfDictionary catalog)
    {
        catalog.Elements.Remove("/AA");
        if (catalog.Dict("/OpenAction") is { } open && open.Name("/S") == "/JavaScript") catalog.Elements.Remove("/OpenAction");
        catalog.Dict("/Names")?.Elements.Remove("/JavaScript");
        foreach (var page in doc.Pages)
        {
            page.Elements.Remove("/AA");
            foreach (var item in PdfX.Annots(page, false).Elements)
            {
                if (PdfX.Resolve(item) is not PdfDictionary annot) continue;
                annot.Elements.Remove("/AA");
                if (annot.Dict("/A") is { } action && action.Name("/S") == "/JavaScript") annot.Elements.Remove("/A");
            }
        }
    }

    private static void RemoveAttachments(PdfDictionary catalog)
    {
        catalog.Dict("/Names")?.Elements.Remove("/EmbeddedFiles");
        catalog.Elements.Remove("/AF");
    }

    private static void FilterAnnotations(PdfPage page, SanitizeOptions options)
    {
        var annots = PdfX.Annots(page, false);
        for (var i = annots.Elements.Count - 1; i >= 0; i--)
        {
            if (PdfX.Resolve(annots.Elements[i]) is not PdfDictionary annot) continue;
            var subtype = annot.Name("/Subtype");
            var remove = subtype switch
            {
                "/Link" => options.Links,
                "/Widget" => false,
                "/FileAttachment" => options.Attachments || options.Comments,
                _ => options.Comments,
            };
            if (remove) annots.Elements.RemoveAt(i);
        }
    }

    // ------------------------------------------------------------------ properties

    public static DocumentInfo ReadInfo(PdfiumDocument doc) => new(
        doc.GetMeta("Title"), doc.GetMeta("Author"), doc.GetMeta("Subject"), doc.GetMeta("Keywords"),
        doc.GetMeta("Creator"), doc.GetMeta("Producer"), FormatDate(doc.GetMeta("CreationDate")), FormatDate(doc.GetMeta("ModDate")));

    public static byte[] WriteInfo(byte[] source, DocumentInfo info) =>
        PdfIO.Modify(source, doc =>
        {
            doc.Info.Title = info.Title;
            doc.Info.Author = info.Author;
            doc.Info.Subject = info.Subject;
            doc.Info.Keywords = info.Keywords;
            // The XMP packet would otherwise keep announcing the old values.
            doc.Internals.Catalog.Elements.Remove("/Metadata");
        });

    /// <summary>Turns "D:20261125140233+02'00'" into "2026-11-25 14:02:33".</summary>
    public static string FormatDate(string pdfDate)
    {
        var digits = new string(pdfDate.Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return pdfDate;
        var text = $"{digits[..4]}-{digits.Substring(4, 2)}-{digits.Substring(6, 2)}";
        if (digits.Length >= 12) text += $" {digits.Substring(8, 2)}:{digits.Substring(10, 2)}";
        if (digits.Length >= 14) text += $":{digits.Substring(12, 2)}";
        return text;
    }

    public static List<FontUse> ListFonts(byte[] source)
    {
        using var doc = PdfIO.Open(source, PdfDocumentOpenMode.Import);
        var fonts = new Dictionary<string, FontUse>();
        foreach (var obj in doc.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary dict || dict.Name("/Type") != "/Font") continue;
            var subtype = dict.Name("/Subtype").TrimStart('/');
            if (subtype == "Type0" || dict.Name("/BaseFont").Length == 0 && subtype != "Type3") continue;
            var name = dict.Name("/BaseFont").TrimStart('/');
            var plus = name.IndexOf('+');
            if (plus == 6) name = name[(plus + 1)..];
            if (name.Length == 0) name = "(Type 3 font)";
            var descriptor = dict.Dict("/FontDescriptor");
            var embedded = descriptor is not null && (descriptor.Elements.ContainsKey("/FontFile") || descriptor.Elements.ContainsKey("/FontFile2") || descriptor.Elements.ContainsKey("/FontFile3"));
            fonts[name + "|" + subtype] = new FontUse(name, subtype, embedded || subtype == "Type3");
        }
        return [.. fonts.Values.OrderBy(f => f.Name)];
    }

    // ------------------------------------------------------------------ bookmarks

    /// <summary>Adds a bookmark under the bookmark at <paramref name="parentPath"/> (empty path = top level).</summary>
    public static byte[] AddBookmark(byte[] source, IReadOnlyList<int> parentPath, string title, int pageIndex) =>
        PdfIO.Modify(source, doc =>
        {
            var target = Children(doc, parentPath);
            target.Add(title, doc.Pages[Math.Clamp(pageIndex, 0, doc.PageCount - 1)], true);
        });

    public static byte[] RenameBookmark(byte[] source, IReadOnlyList<int> path, string title) =>
        PdfIO.Modify(source, doc =>
        {
            if (path.Count == 0) return;
            var siblings = Children(doc, [.. path.Take(path.Count - 1)]);
            if (path[^1] < siblings.Count) siblings[path[^1]].Title = title;
        });

    public static byte[] DeleteBookmark(byte[] source, IReadOnlyList<int> path) =>
        PdfIO.Modify(source, doc =>
        {
            if (path.Count == 0) return;
            var siblings = Children(doc, [.. path.Take(path.Count - 1)]);
            if (path[^1] < siblings.Count) siblings.RemoveAt(path[^1]);
        });

    private static PdfOutlineCollection Children(PdfDocument doc, IReadOnlyList<int> path)
    {
        var collection = doc.Outlines;
        foreach (var index in path)
        {
            if (index < 0 || index >= collection.Count) throw new InvalidOperationException("The bookmark no longer exists.");
            collection = collection[index].Outlines;
        }
        return collection;
    }
}
