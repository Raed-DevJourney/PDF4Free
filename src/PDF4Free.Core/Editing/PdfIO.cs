using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Core.Editing;

/// <summary>Opens and saves PDFsharp documents from and to byte arrays.</summary>
public static class PdfIO
{
    public static PdfDocument Open(byte[] data, PdfDocumentOpenMode mode = PdfDocumentOpenMode.Modify)
    {
        try
        {
            return PdfReader.Open(new MemoryStream(data, writable: false), mode);
        }
        catch (Exception first) when (first is not OutOfMemoryException)
        {
            // PDFium tolerates more damage than PDFsharp: let it rewrite the file, then try once more.
            byte[] repaired;
            try
            {
                repaired = PdfiumOps.Resave(data);
            }
            catch
            {
                throw new InvalidOperationException("This document cannot be edited: " + first.Message, first);
            }
            return PdfReader.Open(new MemoryStream(repaired, writable: false), mode);
        }
    }

    public static byte[] Save(PdfDocument document)
    {
        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

    /// <summary>Applies <paramref name="edit"/> to a copy of the document and returns the result.</summary>
    public static byte[] Modify(byte[] source, Action<PdfDocument> edit)
    {
        using var document = Open(source);
        edit(document);
        return Save(document);
    }
}
