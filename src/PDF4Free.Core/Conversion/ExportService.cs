using System.Globalization;
using System.Security;
using System.Text;
using System.Windows.Media.Imaging;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Core.Conversion;

public enum ImageExportFormat { Png, Jpeg, Tiff, Bmp }

public enum WordExportMode { EditableText, PageImages }

/// <summary>Exports PDF pages to other formats.</summary>
public static class ExportService
{
    // ------------------------------------------------------------------ images and text

    /// <summary>Writes one image file per page (or one multi-page TIFF) and returns the files written.</summary>
    public static List<string> ToImages(PdfiumDocument doc, IReadOnlyList<int> pages, string folder, string baseName,
        ImageExportFormat format, double dpi, int jpegQuality = 90, IProgress<int>? progress = null)
    {
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        if (format == ImageExportFormat.Tiff)
        {
            var tiff = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
            for (var i = 0; i < pages.Count; i++)
            {
                tiff.Frames.Add(BitmapFrame.Create(doc.RenderBitmap(pages[i], dpi)));
                progress?.Report(i + 1);
            }
            var path = Path.Combine(folder, baseName + ".tif");
            using var stream = File.Create(path);
            tiff.Save(stream);
            files.Add(path);
            return files;
        }
        var digits = Math.Max(2, doc.PageCount.ToString(CultureInfo.InvariantCulture).Length);
        for (var i = 0; i < pages.Count; i++)
        {
            var extension = format switch { ImageExportFormat.Jpeg => ".jpg", ImageExportFormat.Bmp => ".bmp", _ => ".png" };
            var path = Path.Combine(folder, $"{baseName}-{(pages[i] + 1).ToString("D" + digits, CultureInfo.InvariantCulture)}{extension}");
            File.WriteAllBytes(path, Encode(doc.RenderBitmap(pages[i], dpi), format, jpegQuality));
            files.Add(path);
            progress?.Report(i + 1);
        }
        return files;
    }

    public static byte[] Encode(BitmapSource bitmap, ImageExportFormat format, int jpegQuality = 90)
    {
        BitmapEncoder encoder = format switch
        {
            ImageExportFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 10, 100) },
            ImageExportFormat.Bmp => new BmpBitmapEncoder(),
            ImageExportFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    public static string ToText(PdfiumDocument doc, IReadOnlyList<int> pages)
    {
        var builder = new StringBuilder();
        foreach (var page in pages)
        {
            builder.AppendLine(doc.GetText(page).Text.TrimEnd());
            builder.AppendLine();
        }
        return builder.ToString();
    }

    /// <summary>Saves every picture placed on the given pages as a PNG file. Returns the number of files written.</summary>
    public static int ExtractImages(PdfiumDocument doc, IReadOnlyList<int> pages, string folder, string baseName)
    {
        Directory.CreateDirectory(folder);
        var count = 0;
        foreach (var page in pages)
        {
            foreach (var obj in doc.GetPageObjects(page).Where(o => o.Kind == PageObjectKind.Image))
            {
                var bitmap = doc.GetImageObjectBitmap(page, obj.Index);
                if (bitmap is null || bitmap.PixelWidth < 8 || bitmap.PixelHeight < 8) continue;
                count++;
                File.WriteAllBytes(Path.Combine(folder, $"{baseName}-p{page + 1}-{count}.png"), Encode(bitmap, ImageExportFormat.Png));
            }
        }
        return count;
    }

    // ------------------------------------------------------------------ Word

    public static void ToWord(PdfiumDocument doc, IReadOnlyList<int> pages, string path, WordExportMode mode)
    {
        if (pages.Count == 0) throw new InvalidOperationException("No pages to export.");
        var first = doc.GetGeometry(pages[0]);
        using var package = new OpenXmlPackage(path);
        package.AddRootRelationship("http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "word/document.xml");
        package.AddOverride("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");

        var body = new StringBuilder();
        var relationships = new StringBuilder();
        const double margin = 54;
        for (var i = 0; i < pages.Count; i++)
        {
            // Each PDF page starts a Word page through "page break before" on its first paragraph.
            var breakBefore = i > 0 ? "<w:pageBreakBefore/>" : "";
            if (mode == WordExportMode.PageImages) AppendPageImage(doc, pages[i], i + 1, package, body, relationships, breakBefore);
            else AppendParagraphs(doc, pages[i], margin, body, breakBefore);
        }
        var pageMargin = mode == WordExportMode.PageImages ? 0 : margin;
        body.Append(Inv($"<w:sectPr><w:pgSz w:w=\"{Twips(first.ViewWidth)}\" w:h=\"{Twips(first.ViewHeight)}\"/>"))
            .Append(Inv($"<w:pgMar w:top=\"{Twips(pageMargin)}\" w:right=\"{Twips(pageMargin)}\" w:bottom=\"{Twips(pageMargin)}\" w:left=\"{Twips(pageMargin)}\" w:header=\"0\" w:footer=\"0\" w:gutter=\"0\"/></w:sectPr>"));

        package.AddPart("word/document.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"><w:body>" + body + "</w:body></w:document>");
        package.AddPart("word/_rels/document.xml.rels", OpenXmlPackage.Relationships(relationships.ToString()));
    }

    private static void AppendParagraphs(PdfiumDocument doc, int page, double margin, StringBuilder body, string breakBefore)
    {
        var paragraphs = PageLayout.Paragraphs(PageLayout.Lines(doc, page));
        if (paragraphs.Count == 0)
        {
            body.Append($"<w:p><w:pPr>{breakBefore}</w:pPr></w:p>");
            return;
        }
        foreach (var p in paragraphs)
        {
            var indent = Math.Max(0, p.Left - margin);
            body.Append($"<w:p><w:pPr>{breakBefore}").Append(Inv($"<w:spacing w:before=\"{Twips(p.GapBefore)}\" w:after=\"0\"/><w:ind w:left=\"{Twips(indent)}\"/></w:pPr><w:r><w:rPr>"));
            breakBefore = "";
            if (p.Bold) body.Append("<w:b/>");
            if (p.Italic) body.Append("<w:i/>");
            if (p.Color != 0) body.Append(Inv($"<w:color w:val=\"{p.Color:X6}\"/>"));
            body.Append(Inv($"<w:sz w:val=\"{(int)Math.Round(p.FontSize * 2)}\"/></w:rPr><w:t xml:space=\"preserve\">{Xml(p.Text)}</w:t></w:r></w:p>"));
        }
    }

    private static void AppendPageImage(PdfiumDocument doc, int page, int number, OpenXmlPackage package, StringBuilder body, StringBuilder relationships, string breakBefore)
    {
        var geometry = doc.GetGeometry(page);
        package.AddBinary($"word/media/page{number}.jpeg", Encode(doc.RenderBitmap(page, 150), ImageExportFormat.Jpeg, 88));
        relationships.Append(Inv($"<Relationship Id=\"rId{number}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/page{number}.jpeg\"/>"));
        // A hair smaller than the page so Word never pushes the picture onto an extra page.
        var cx = Emu(geometry.ViewWidth - 2);
        var cy = Emu(geometry.ViewHeight - 4);
        body.Append($"<w:p><w:pPr>{breakBefore}<w:spacing w:before=\"0\" w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:r><w:drawing>")
            .Append(Inv($"<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{number}\" name=\"Page {number}\"/>"))
            .Append("<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">")
            .Append("<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">")
            .Append(Inv($"<pic:nvPicPr><pic:cNvPr id=\"{number}\" name=\"page{number}.jpeg\"/><pic:cNvPicPr/></pic:nvPicPr>"))
            .Append(Inv($"<pic:blipFill><a:blip r:embed=\"rId{number}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"))
            .Append(Inv($"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>"))
            .Append("</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>");
    }

    // ------------------------------------------------------------------ Excel

    public static void ToExcel(PdfiumDocument doc, IReadOnlyList<int> pages, string path)
    {
        if (pages.Count == 0) throw new InvalidOperationException("No pages to export.");
        using var package = new OpenXmlPackage(path);
        package.AddRootRelationship("http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
        package.AddOverride("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        var sheets = new StringBuilder();
        var relationships = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            var n = i + 1;
            package.AddOverride($"/xl/worksheets/sheet{n}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            package.AddPart($"xl/worksheets/sheet{n}.xml", SheetXml(PageLayout.Lines(doc, pages[i])));
            sheets.Append(Inv($"<sheet name=\"Page {pages[i] + 1}\" sheetId=\"{n}\" r:id=\"rId{n}\"/>"));
            relationships.Append(Inv($"<Relationship Id=\"rId{n}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{n}.xml\"/>"));
        }
        package.AddPart("xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets>" + sheets + "</sheets></workbook>");
        package.AddPart("xl/_rels/workbook.xml.rels", OpenXmlPackage.Relationships(relationships.ToString()));
    }

    /// <summary>Lines become rows; a wide gap between words starts a new cell, and cells are aligned into shared columns.</summary>
    private static string SheetXml(List<LayoutLine> lines)
    {
        var rows = lines.Select(SplitCells).ToList();
        var columns = ColumnStarts(rows.SelectMany(r => r).Select(c => c.Left));
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Count; r++)
        {
            xml.Append(Inv($"<row r=\"{r + 1}\">"));
            var used = -1;
            foreach (var (left, text) in rows[r])
            {
                var column = Math.Max(used + 1, ColumnIndex(columns, left));
                used = column;
                var reference = ColumnName(column) + (r + 1).ToString(CultureInfo.InvariantCulture);
                if (IsPlainNumber(text, out var number)) xml.Append(Inv($"<c r=\"{reference}\"><v>{number.ToString("R", CultureInfo.InvariantCulture)}</v></c>"));
                else xml.Append(Inv($"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(text)}</t></is></c>"));
            }
            xml.Append("</row>");
        }
        return xml.Append("</sheetData></worksheet>").ToString();
    }

    private static List<(double Left, string Text)> SplitCells(LayoutLine line)
    {
        var cells = new List<(double, string)>();
        var threshold = Math.Max(10, line.FontSize * 1.4);
        var current = new List<LayoutWord>();
        foreach (var word in line.Words)
        {
            if (current.Count > 0 && word.Left - current[^1].Right > threshold)
            {
                cells.Add((current[0].Left, string.Join(' ', current.Select(w => w.Text))));
                current = [];
            }
            current.Add(word);
        }
        if (current.Count > 0) cells.Add((current[0].Left, string.Join(' ', current.Select(w => w.Text))));
        return cells;
    }

    private static List<double> ColumnStarts(IEnumerable<double> lefts)
    {
        var starts = new List<double>();
        foreach (var left in lefts.OrderBy(x => x))
        {
            if (starts.Count == 0 || left - starts[^1] > 12) starts.Add(left);
        }
        return starts;
    }

    private static int ColumnIndex(List<double> starts, double left)
    {
        var index = 0;
        for (var i = 0; i < starts.Count; i++)
        {
            if (starts[i] <= left + 6) index = i;
        }
        return index;
    }

    private static string ColumnName(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    private static bool IsPlainNumber(string text, out double number)
    {
        number = 0;
        var candidate = text.Replace(",", "", StringComparison.Ordinal);
        if (candidate.Length is 0 or > 15 || candidate.Any(c => !char.IsDigit(c) && c != '.' && c != '-')) return false;
        if (candidate.Length > 1 && candidate[0] == '0' && candidate[1] != '.') return false;
        return double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    // ------------------------------------------------------------------ PowerPoint

    private const string PresentationNamespaces =
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";

    private const string EmptyShapeTree =
        "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    /// <summary>One slide per page, each showing a picture of the page.</summary>
    public static void ToPowerPoint(PdfiumDocument doc, IReadOnlyList<int> pages, string path, double dpi = 150)
    {
        if (pages.Count == 0) throw new InvalidOperationException("No pages to export.");
        var first = doc.GetGeometry(pages[0]);
        var slideWidth = Math.Clamp(first.ViewWidth, 72, 4000);
        var slideHeight = Math.Clamp(first.ViewHeight, 72, 4000);
        const string head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
        const string relType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        using var package = new OpenXmlPackage(path);
        package.AddRootRelationship(relType + "officeDocument", "ppt/presentation.xml");
        package.AddOverride("/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml");
        package.AddOverride("/ppt/slideMasters/slideMaster1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml");
        package.AddOverride("/ppt/slideLayouts/slideLayout1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml");
        package.AddOverride("/ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml");

        var slideIds = new StringBuilder();
        var relationships = new StringBuilder();
        relationships.Append($"<Relationship Id=\"rId1\" Type=\"{relType}slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>");
        relationships.Append($"<Relationship Id=\"rId2\" Type=\"{relType}theme\" Target=\"theme/theme1.xml\"/>");
        for (var i = 0; i < pages.Count; i++)
        {
            var n = i + 1;
            var geometry = doc.GetGeometry(pages[i]);
            var fit = Math.Min(slideWidth / geometry.ViewWidth, slideHeight / geometry.ViewHeight);
            var width = geometry.ViewWidth * fit;
            var height = geometry.ViewHeight * fit;
            package.AddBinary($"ppt/media/image{n}.jpeg", Encode(doc.RenderBitmap(pages[i], dpi), ImageExportFormat.Jpeg, 88));
            package.AddOverride($"/ppt/slides/slide{n}.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml");
            package.AddPart($"ppt/slides/slide{n}.xml",
                head + $"<p:sld {PresentationNamespaces}><p:cSld><p:spTree>{EmptyShapeTree}" +
                Inv($"<p:pic><p:nvPicPr><p:cNvPr id=\"2\" name=\"Page {pages[i] + 1}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>") +
                "<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
                Inv($"<p:spPr><a:xfrm><a:off x=\"{Emu((slideWidth - width) / 2)}\" y=\"{Emu((slideHeight - height) / 2)}\"/><a:ext cx=\"{Emu(width)}\" cy=\"{Emu(height)}\"/></a:xfrm>") +
                "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
            package.AddPart($"ppt/slides/_rels/slide{n}.xml.rels", OpenXmlPackage.Relationships(
                $"<Relationship Id=\"rId1\" Type=\"{relType}slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
                Inv($"<Relationship Id=\"rId2\" Type=\"{relType}image\" Target=\"../media/image{n}.jpeg\"/>")));
            slideIds.Append(Inv($"<p:sldId id=\"{255 + n}\" r:id=\"rId{n + 2}\"/>"));
            relationships.Append(Inv($"<Relationship Id=\"rId{n + 2}\" Type=\"{relType}slide\" Target=\"slides/slide{n}.xml\"/>"));
        }

        package.AddPart("ppt/presentation.xml",
            head + $"<p:presentation {PresentationNamespaces}><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" +
            $"<p:sldIdLst>{slideIds}</p:sldIdLst>" +
            Inv($"<p:sldSz cx=\"{Emu(slideWidth)}\" cy=\"{Emu(slideHeight)}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>"));
        package.AddPart("ppt/_rels/presentation.xml.rels", OpenXmlPackage.Relationships(relationships.ToString()));
        package.AddPart("ppt/slideMasters/slideMaster1.xml",
            head + $"<p:sldMaster {PresentationNamespaces}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{EmptyShapeTree}</p:spTree></p:cSld>" +
            "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
            "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst></p:sldMaster>");
        package.AddPart("ppt/slideMasters/_rels/slideMaster1.xml.rels", OpenXmlPackage.Relationships(
            $"<Relationship Id=\"rId1\" Type=\"{relType}slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{relType}theme\" Target=\"../theme/theme1.xml\"/>"));
        package.AddPart("ppt/slideLayouts/slideLayout1.xml",
            head + $"<p:sldLayout {PresentationNamespaces} type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\"><p:spTree>{EmptyShapeTree}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        package.AddPart("ppt/slideLayouts/_rels/slideLayout1.xml.rels", OpenXmlPackage.Relationships(
            $"<Relationship Id=\"rId1\" Type=\"{relType}slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/>"));
        package.AddPart("ppt/theme/theme1.xml", head + OpenXmlPackage.ThemeXml);
    }

    // ------------------------------------------------------------------ HTML

    public static void ToHtml(PdfiumDocument doc, IReadOnlyList<int> pages, string path, bool includePageImages, string title)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append($"<title>{Xml(title)}</title>\n<style>\n")
            .Append("body{font-family:Segoe UI,Arial,sans-serif;line-height:1.45;max-width:52rem;margin:2rem auto;padding:0 1rem;color:#1b1b1b}\n")
            .Append("section{border-bottom:1px solid #ddd;padding-bottom:1.5rem;margin-bottom:1.5rem}\nimg{max-width:100%;border:1px solid #ccc}\n.page{color:#777;font-size:.8rem}\n")
            .Append("</style>\n</head>\n<body>\n");
        foreach (var page in pages)
        {
            html.Append($"<section id=\"page-{page + 1}\">\n<p class=\"page\">Page {page + 1}</p>\n");
            if (includePageImages)
            {
                var data = Convert.ToBase64String(Encode(doc.RenderBitmap(page, 110), ImageExportFormat.Jpeg, 80));
                html.Append($"<img alt=\"Page {page + 1}\" src=\"data:image/jpeg;base64,{data}\">\n");
            }
            var paragraphs = PageLayout.Paragraphs(PageLayout.Lines(doc, page));
            var body = paragraphs.Count == 0 ? 11 : paragraphs.GroupBy(p => p.FontSize).OrderByDescending(g => g.Sum(p => p.Text.Length)).First().Key;
            foreach (var p in paragraphs)
            {
                var tag = p.FontSize >= body * 1.6 ? "h1" : p.FontSize >= body * 1.25 ? "h2" : p.Bold && p.Text.Length < 90 ? "h3" : "p";
                var text = Xml(p.Text);
                if (tag == "p" && p.Bold) text = $"<strong>{text}</strong>";
                if (p.Italic) text = $"<em>{text}</em>";
                html.Append($"<{tag}>{text}</{tag}>\n");
            }
            html.Append("</section>\n");
        }
        html.Append("</body>\n</html>\n");
        File.WriteAllText(path, html.ToString(), new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ helpers

    private static string Xml(string text) => SecurityElement.Escape(PageLayout.Clean(text)) ?? "";

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static long Twips(double points) => (long)Math.Round(points * 20);

    private static long Emu(double points) => (long)Math.Round(points * 12700);
}
