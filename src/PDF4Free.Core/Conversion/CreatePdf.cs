using System.Windows.Media.Imaging;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PDF4Free.Core.Editing;

namespace PDF4Free.Core.Conversion;

public enum ImagePageSize { FitImage, A4, Letter }

/// <summary>Turns other file types into PDF.</summary>
public static class CreatePdf
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".jfif", ".ico", ".wdp"];
    public static readonly string[] TextExtensions = [".txt", ".log", ".md", ".csv", ".json", ".xml", ".ini", ".cs", ".py", ".js", ".sql"];
    public static readonly string[] WordExtensions = [".docx", ".doc", ".rtf", ".odt", ".docm"];
    public static readonly string[] ExcelExtensions = [".xlsx", ".xls", ".xlsm", ".ods"];
    public static readonly string[] PowerPointExtensions = [".pptx", ".ppt", ".pptm", ".odp"];
    public static readonly string[] WebExtensions = [".html", ".htm", ".mht", ".mhtml", ".svg"];

    public static IEnumerable<string> AllExtensions =>
        new[] { ".pdf" }.Concat(ImageExtensions).Concat(TextExtensions).Concat(WordExtensions).Concat(ExcelExtensions).Concat(PowerPointExtensions).Concat(WebExtensions);

    public static bool IsWebFile(string path) => WebExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Converts any supported file except web pages (those need a browser engine and are handled by the app).</summary>
    public static byte[] FromFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".pdf") return File.ReadAllBytes(path);
        if (ImageExtensions.Contains(extension)) return FromImages([File.ReadAllBytes(path)]);
        if (TextExtensions.Contains(extension) && !(extension == ".csv" && OfficeAutomation.IsAvailable(OfficeApp.Excel)))
        {
            return FromText(File.ReadAllText(path), monospace: extension is not (".txt" or ".md"));
        }
        if (WordExtensions.Contains(extension))
        {
            if (OfficeAutomation.IsAvailable(OfficeApp.Word)) return OfficeAutomation.Convert(path, OfficeApp.Word);
            if (extension is ".docx" or ".docm") return DocxConverter.Convert(path);
            throw new NotSupportedException($"Converting {extension} files needs Microsoft Word, which is not installed. (.docx files are converted without it.)");
        }
        if (ExcelExtensions.Contains(extension) || extension == ".csv")
        {
            if (OfficeAutomation.IsAvailable(OfficeApp.Excel)) return OfficeAutomation.Convert(path, OfficeApp.Excel);
            throw new NotSupportedException("Converting spreadsheets needs Microsoft Excel, which is not installed.");
        }
        if (PowerPointExtensions.Contains(extension))
        {
            if (OfficeAutomation.IsAvailable(OfficeApp.PowerPoint)) return OfficeAutomation.Convert(path, OfficeApp.PowerPoint);
            throw new NotSupportedException("Converting presentations needs Microsoft PowerPoint, which is not installed.");
        }
        throw new NotSupportedException($"Files of type '{extension}' cannot be converted to PDF.");
    }

    public static byte[] Blank(double widthPoints = 612, double heightPoints = 792, int pages = 1)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < Math.Max(1, pages); i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(widthPoints);
            page.Height = XUnit.FromPoint(heightPoints);
        }
        return PdfIO.Save(doc);
    }

    /// <summary>One page per image (or per frame of a multi-page TIFF).</summary>
    public static byte[] FromImages(IReadOnlyList<byte[]> images, ImagePageSize size = ImagePageSize.FitImage, double margin = 0)
    {
        using var doc = new PdfDocument();
        foreach (var data in images)
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var passThrough = decoder.Frames.Count == 1 && PageCanvas.IsJpeg(data);
            foreach (var frame in decoder.Frames)
            {
                var dpiX = frame.DpiX is > 20 and < 2400 ? frame.DpiX : 96;
                var dpiY = frame.DpiY is > 20 and < 2400 ? frame.DpiY : 96;
                var naturalWidth = frame.PixelWidth * 72.0 / dpiX;
                var naturalHeight = frame.PixelHeight * 72.0 / dpiY;
                using var image = XImage.FromStream(new MemoryStream(passThrough ? data : PageCanvas.ToPng(frame)));
                AddImagePage(doc, image, naturalWidth, naturalHeight, size, margin);
            }
        }
        if (doc.PageCount == 0) throw new InvalidOperationException("No images to convert.");
        return PdfIO.Save(doc);
    }

    public static byte[] FromBitmap(BitmapSource bitmap) => FromImages([PageCanvas.ToPng(bitmap)]);

    private static void AddImagePage(PdfDocument doc, XImage image, double naturalWidth, double naturalHeight, ImagePageSize size, double margin)
    {
        var page = doc.AddPage();
        var (pageWidth, pageHeight) = size switch
        {
            ImagePageSize.A4 => (595.28, 841.89),
            ImagePageSize.Letter => (612.0, 792.0),
            _ => (naturalWidth + 2 * margin, naturalHeight + 2 * margin),
        };
        if (size != ImagePageSize.FitImage && naturalWidth > naturalHeight) (pageWidth, pageHeight) = (pageHeight, pageWidth);
        page.Width = XUnit.FromPoint(pageWidth);
        page.Height = XUnit.FromPoint(pageHeight);
        var scale = Math.Min((pageWidth - 2 * margin) / naturalWidth, (pageHeight - 2 * margin) / naturalHeight);
        if (size == ImagePageSize.FitImage) scale = 1;
        var width = naturalWidth * scale;
        var height = naturalHeight * scale;
        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(image, new XRect((pageWidth - width) / 2, (pageHeight - height) / 2, width, height));
    }

    public static byte[] FromText(string text, bool monospace = false, string? title = null)
    {
        var document = NewDocument(monospace ? "Consolas" : "Arial", monospace ? 9.5 : 11);
        var section = document.AddSection();
        if (!string.IsNullOrEmpty(title))
        {
            var heading = section.AddParagraph(title);
            heading.Format.Font.Size = 16;
            heading.Format.Font.Bold = true;
            heading.Format.SpaceAfter = Unit.FromPoint(10);
        }
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var paragraph = section.AddParagraph();
            AddTextWithTabs(paragraph, line);
        }
        return Render(document);
    }

    private static void AddTextWithTabs(Paragraph paragraph, string line)
    {
        // Leading spaces are significant in plain text; MigraDoc would collapse them.
        var indent = line.Length - line.TrimStart(' ').Length;
        if (indent > 0) paragraph.AddSpace(indent);
        var parts = line.TrimStart(' ').Split('\t');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) paragraph.AddSpace(4);
            paragraph.AddText(parts[i]);
        }
    }

    internal static Document NewDocument(string font, double size)
    {
        var document = new Document();
        var normal = document.Styles["Normal"]!;
        normal.Font.Name = font;
        normal.Font.Size = size;
        return document;
    }

    internal static byte[] Render(Document document)
    {
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output, false);
        return output.ToArray();
    }
}

public enum OfficeApp { Word, Excel, PowerPoint }

/// <summary>Uses an installed Microsoft Office application to export a document as PDF with full fidelity.</summary>
public static class OfficeAutomation
{
    private static string ProgId(OfficeApp app) => app switch
    {
        OfficeApp.Word => "Word.Application",
        OfficeApp.Excel => "Excel.Application",
        _ => "PowerPoint.Application",
    };

    public static bool IsAvailable(OfficeApp app) => Type.GetTypeFromProgID(ProgId(app)) is not null;

    public static byte[] Convert(string path, OfficeApp app)
    {
        var output = Path.Combine(Path.GetTempPath(), $"pdf4free-{Guid.NewGuid():N}.pdf");
        try
        {
            RunSta(() => Export(Path.GetFullPath(path), output, app));
            if (!File.Exists(output)) throw new InvalidOperationException($"Microsoft {app} did not produce a PDF.");
            return File.ReadAllBytes(output);
        }
        finally
        {
            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless.
            }
        }
    }

    private static void Export(string input, string output, OfficeApp app)
    {
        var type = Type.GetTypeFromProgID(ProgId(app)) ?? throw new InvalidOperationException($"Microsoft {app} is not installed.");
        dynamic application = Activator.CreateInstance(type)!;
        try
        {
            switch (app)
            {
                case OfficeApp.Word:
                    application.Visible = false;
                    application.DisplayAlerts = 0;
                    dynamic document = application.Documents.Open(input, false, true, false);
                    document.ExportAsFixedFormat(output, 17);
                    document.Close(false);
                    break;
                case OfficeApp.Excel:
                    application.Visible = false;
                    application.DisplayAlerts = false;
                    dynamic workbook = application.Workbooks.Open(input, 0, true);
                    workbook.ExportAsFixedFormat(0, output);
                    workbook.Close(false);
                    break;
                default:
                    dynamic presentation = application.Presentations.Open(input, -1, 0, 0);
                    presentation.SaveAs(output, 32);
                    presentation.Close();
                    break;
            }
        }
        finally
        {
            application.Quit();
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Microsoft Office could not convert the file: " + failure.Message, failure);
    }
}
