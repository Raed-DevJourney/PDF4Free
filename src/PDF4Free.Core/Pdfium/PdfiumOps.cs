using System.Runtime.InteropServices;
using PDF4Free.Core.Model;

namespace PDF4Free.Core.Pdfium;

/// <summary>Edits that PDFium performs: each takes a PDF as bytes and returns the changed PDF as bytes.</summary>
public static class PdfiumOps
{
    /// <summary>Removes content objects (text runs, images, paths) from a page.</summary>
    public static byte[] RemoveObjects(byte[] source, int pageIndex, IReadOnlyCollection<int> objectIndexes) =>
        EditPage(source, pageIndex, page =>
        {
            var handles = objectIndexes.Distinct().Select(i => Native.FPDFPage_GetObject(page, i)).Where(h => h != IntPtr.Zero).ToList();
            foreach (var handle in handles)
            {
                if (Native.FPDFPage_RemoveObject(page, handle) != 0) Native.FPDFPageObj_Destroy(handle);
            }
        });

    /// <summary>Applies an affine transform (PDF matrix order) to one content object.</summary>
    public static byte[] TransformObject(byte[] source, int pageIndex, int objectIndex, double a, double b, double c, double d, double e, double f) =>
        EditPage(source, pageIndex, page =>
        {
            var handle = Native.FPDFPage_GetObject(page, objectIndex);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("The selected object no longer exists.");
            Native.FPDFPageObj_Transform(handle, a, b, c, d, e, f);
        });

    /// <summary>Burns annotations and form fields into the page content. <paramref name="pages"/> null means every page.</summary>
    public static byte[] Flatten(byte[] source, IEnumerable<int>? pages = null)
    {
        using var doc = PdfiumDocument.Load(source);
        lock (PdfiumLibrary.Sync)
        {
            foreach (var index in pages ?? Enumerable.Range(0, doc.PageCount))
            {
                doc.WithPage(index, page => Native.FPDFPage_Flatten(page, 0));
            }
            return PdfiumDocument.SaveLocked(doc.Handle, false);
        }
    }

    public static byte[] AddAttachment(byte[] source, string name, byte[] data)
    {
        using var doc = PdfiumDocument.Load(source);
        lock (PdfiumLibrary.Sync)
        {
            var attachment = Native.FPDFDoc_AddAttachment(doc.Handle, name);
            if (attachment == IntPtr.Zero) throw new InvalidOperationException($"An attachment named '{name}' already exists.");
            var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                if (Native.FPDFAttachment_SetFile(attachment, doc.Handle, pin.AddrOfPinnedObject(), (uint)data.Length) == 0)
                    throw new InvalidOperationException("The file could not be attached.");
            }
            finally
            {
                pin.Free();
            }
            return PdfiumDocument.SaveLocked(doc.Handle, false);
        }
    }

    public static byte[] DeleteAttachment(byte[] source, int index)
    {
        using var doc = PdfiumDocument.Load(source);
        lock (PdfiumLibrary.Sync)
        {
            if (Native.FPDFDoc_DeleteAttachment(doc.Handle, index) == 0) throw new InvalidOperationException("The attachment could not be removed.");
            return PdfiumDocument.SaveLocked(doc.Handle, false);
        }
    }

    /// <summary>Returns an unencrypted copy. The caller must have checked that the password grants owner access.</summary>
    public static byte[] RemoveSecurity(byte[] source, string? password)
    {
        using var doc = PdfiumDocument.Load(source, password);
        if (!doc.Security.OwnerAccess) throw new UnauthorizedAccessException("The permissions password is required to remove security from this document.");
        return doc.Save(removeSecurity: true);
    }

    /// <summary>Rewrites the file through PDFium, which repairs many structural problems.</summary>
    public static byte[] Resave(byte[] source, string? password = null)
    {
        using var doc = PdfiumDocument.Load(source, password);
        return doc.Save();
    }

    private static byte[] EditPage(byte[] source, int pageIndex, Action<IntPtr> edit)
    {
        using var doc = PdfiumDocument.Load(source);
        lock (PdfiumLibrary.Sync)
        {
            doc.WithPage(pageIndex, page =>
            {
                edit(page);
                if (Native.FPDFPage_GenerateContent(page) == 0) throw new InvalidOperationException("The page content could not be rewritten.");
                return true;
            });
            return PdfiumDocument.SaveLocked(doc.Handle, false);
        }
    }
}
