using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;

namespace PDF4Free.Core.Conversion;

/// <summary>
/// A self-contained Word (.docx) to PDF converter for machines without Microsoft Word.
/// It keeps text, basic character formatting, headings, alignment, lists, tables and inline pictures; it does not
/// reproduce floating objects, columns, headers/footers or exact pagination.
/// </summary>
public static class DocxConverter
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private sealed class Context(ZipArchive zip, Dictionary<string, string> relationships, double defaultSize, double contentWidth)
    {
        public ZipArchive Zip { get; } = zip;
        public Dictionary<string, string> Relationships { get; } = relationships;
        public double DefaultSize { get; } = defaultSize;
        public double ContentWidth { get; } = contentWidth;
    }

    public static byte[] Convert(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var body = Load(zip, "word/document.xml")?.Root?.Element(W + "body")
                   ?? throw new InvalidDataException("This is not a valid Word document.");
        var defaultSize = ReadDefaultSize(zip);
        var document = CreatePdf.NewDocument("Calibri", defaultSize);
        var section = document.AddSection();
        var contentWidth = ApplyPageSetup(section, body.Elements(W + "sectPr").LastOrDefault());
        var context = new Context(zip, ReadRelationships(zip), defaultSize, contentWidth);

        foreach (var element in body.Elements())
        {
            if (element.Name == W + "p")
            {
                if (IsOn(element.Element(W + "pPr")?.Element(W + "pageBreakBefore")) == true) section.AddPageBreak();
                AddParagraph(context, section.AddParagraph(), element, section);
            }
            else if (element.Name == W + "tbl") AddTable(context, section, element);
        }
        return CreatePdf.Render(document);
    }

    private static XDocument? Load(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static double ReadDefaultSize(ZipArchive zip)
    {
        var size = Load(zip, "word/styles.xml")?.Descendants(W + "docDefaults").Descendants(W + "sz").FirstOrDefault()?.Attribute(W + "val")?.Value;
        return HalfPoints(size) ?? 11;
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchive zip)
    {
        var map = new Dictionary<string, string>();
        var doc = Load(zip, "word/_rels/document.xml.rels");
        if (doc?.Root is null) return map;
        foreach (var rel in doc.Root.Elements(Rel + "Relationship"))
        {
            var id = rel.Attribute("Id")?.Value;
            var target = rel.Attribute("Target")?.Value;
            if (id is not null && target is not null) map[id] = target;
        }
        return map;
    }

    private static double ApplyPageSetup(Section section, XElement? sectPr)
    {
        double width = 612, height = 792, left = 72, right = 72, top = 72, bottom = 72;
        if (sectPr?.Element(W + "pgSz") is { } size)
        {
            width = Twips(size.Attribute(W + "w")?.Value) ?? width;
            height = Twips(size.Attribute(W + "h")?.Value) ?? height;
        }
        if (sectPr?.Element(W + "pgMar") is { } margin)
        {
            left = Twips(margin.Attribute(W + "left")?.Value) ?? left;
            right = Twips(margin.Attribute(W + "right")?.Value) ?? right;
            top = Twips(margin.Attribute(W + "top")?.Value) ?? top;
            bottom = Twips(margin.Attribute(W + "bottom")?.Value) ?? bottom;
        }
        var setup = section.PageSetup;
        setup.PageWidth = Unit.FromPoint(width);
        setup.PageHeight = Unit.FromPoint(height);
        setup.LeftMargin = Unit.FromPoint(left);
        setup.RightMargin = Unit.FromPoint(right);
        setup.TopMargin = Unit.FromPoint(Math.Abs(top));
        setup.BottomMargin = Unit.FromPoint(Math.Abs(bottom));
        return Math.Max(100, width - left - right);
    }

    private static double? Twips(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var twips) ? twips / 20.0 : null;

    private static double? HalfPoints(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var half) ? half / 2.0 : null;

    // ------------------------------------------------------------------ paragraphs

    private static void AddParagraph(Context context, Paragraph paragraph, XElement source, Section? sectionForPageBreaks)
    {
        var properties = source.Element(W + "pPr");
        var style = properties?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
        var (styleSize, styleBold) = HeadingLook(style, context.DefaultSize);
        paragraph.Format.SpaceAfter = Unit.FromPoint(styleBold ? 6 : 8);
        if (styleBold) paragraph.Format.SpaceBefore = Unit.FromPoint(10);
        paragraph.Format.Alignment = properties?.Element(W + "jc")?.Attribute(W + "val")?.Value switch
        {
            "center" => ParagraphAlignment.Center,
            "right" or "end" => ParagraphAlignment.Right,
            "both" or "distribute" => ParagraphAlignment.Justify,
            _ => ParagraphAlignment.Left,
        };
        if (Twips(properties?.Element(W + "ind")?.Attribute(W + "left")?.Value) is { } indent and > 0)
        {
            paragraph.Format.LeftIndent = Unit.FromPoint(Math.Min(indent, context.ContentWidth / 2));
        }
        if (properties?.Element(W + "numPr") is not null || style.StartsWith("ListParagraph", StringComparison.OrdinalIgnoreCase) && properties?.Element(W + "numPr") is not null)
        {
            paragraph.Format.LeftIndent = Unit.FromPoint(Math.Max(18, paragraph.Format.LeftIndent.Point));
            paragraph.Format.FirstLineIndent = Unit.FromPoint(-12);
            paragraph.AddText("•  ");
        }
        AddRuns(context, paragraph, source, styleSize, styleBold, sectionForPageBreaks);
    }

    private static (double Size, bool Bold) HeadingLook(string style, double defaultSize) => style.ToLowerInvariant() switch
    {
        "title" => (26, false),
        "subtitle" => (14, false),
        "heading1" or "heading 1" => (16, true),
        "heading2" or "heading 2" => (13, true),
        "heading3" or "heading 3" => (12, true),
        var s when s.StartsWith("heading", StringComparison.Ordinal) => (11.5, true),
        _ => (defaultSize, false),
    };

    private static void AddRuns(Context context, Paragraph paragraph, XElement container, double styleSize, bool styleBold, Section? section)
    {
        foreach (var child in container.Elements())
        {
            if (child.Name == W + "r") AddRun(context, paragraph, child, styleSize, styleBold, section);
            else if (child.Name == W + "hyperlink" || child.Name == W + "smartTag" || child.Name == W + "ins" || child.Name == W + "fldSimple" || child.Name == W + "sdtContent" || child.Name == W + "sdt")
            {
                AddRuns(context, paragraph, child, styleSize, styleBold, section);
            }
        }
    }

    private static void AddRun(Context context, Paragraph paragraph, XElement run, double styleSize, bool styleBold, Section? section)
    {
        var properties = run.Element(W + "rPr");
        var bold = IsOn(properties?.Element(W + "b")) ?? styleBold;
        var italic = IsOn(properties?.Element(W + "i")) ?? false;
        var underline = properties?.Element(W + "u")?.Attribute(W + "val")?.Value is { } u && u != "none";
        var size = HalfPoints(properties?.Element(W + "sz")?.Attribute(W + "val")?.Value) ?? styleSize;
        var color = properties?.Element(W + "color")?.Attribute(W + "val")?.Value;

        foreach (var node in run.Elements())
        {
            if (node.Name == W + "t")
            {
                var text = paragraph.AddFormattedText(node.Value);
                text.Bold = bold;
                text.Italic = italic;
                text.Size = Unit.FromPoint(size);
                if (underline) text.Underline = Underline.Single;
                if (color is { Length: 6 } && uint.TryParse(color, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) text.Color = new Color(0xFF000000 | rgb);
            }
            else if (node.Name == W + "tab") paragraph.AddTab();
            else if (node.Name == W + "br")
            {
                if (node.Attribute(W + "type")?.Value == "page" && section is not null) section.AddPageBreak();
                else paragraph.AddLineBreak();
            }
            else if (node.Name == W + "drawing") AddPicture(context, paragraph, node);
        }
    }

    private static bool? IsOn(XElement? toggle)
    {
        if (toggle is null) return null;
        var value = toggle.Attribute(W + "val")?.Value;
        return value is null or "1" or "true" or "on";
    }

    private static void AddPicture(Context context, Paragraph paragraph, XElement drawing)
    {
        var relationId = drawing.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed")?.Value;
        if (relationId is null || !context.Relationships.TryGetValue(relationId, out var target)) return;
        var entry = context.Zip.GetEntry("word/" + target.TrimStart('/').Replace("../", ""));
        if (entry is null) return;
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        byte[] png;
        try
        {
            png = Editing.PageCanvas.ToPng(Editing.PageCanvas.Decode(copy.ToArray()));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            return; // EMF/WMF and other formats WPF cannot decode are skipped.
        }
        var image = paragraph.AddImage("base64:" + System.Convert.ToBase64String(png));
        image.LockAspectRatio = true;
        var extent = drawing.Descendants(Wp + "extent").FirstOrDefault()?.Attribute("cx")?.Value;
        var width = double.TryParse(extent, NumberStyles.Float, CultureInfo.InvariantCulture, out var emu) ? emu / 12700.0 : context.ContentWidth;
        image.Width = Unit.FromPoint(Math.Min(width, context.ContentWidth));
    }

    // ------------------------------------------------------------------ tables

    private static void AddTable(Context context, Section section, XElement source)
    {
        var rows = source.Elements(W + "tr").ToList();
        if (rows.Count == 0) return;
        var grid = source.Element(W + "tblGrid")?.Elements(W + "gridCol").Select(c => Twips(c.Attribute(W + "w")?.Value) ?? 0).ToList() ?? [];
        var columnCount = Math.Max(grid.Count, rows.Max(r => r.Elements(W + "tc").Sum(SpanOf)));
        if (columnCount == 0) return;
        var total = grid.Sum();
        var table = section.AddTable();
        table.Borders.Width = 0.5;
        table.Borders.Color = Colors.Gray;
        for (var c = 0; c < columnCount; c++)
        {
            var width = c < grid.Count && total > 0 ? grid[c] * Math.Min(1, context.ContentWidth / total) : context.ContentWidth / columnCount;
            table.AddColumn(Unit.FromPoint(Math.Max(12, width)));
        }
        foreach (var sourceRow in rows)
        {
            var row = table.AddRow();
            var column = 0;
            foreach (var cell in sourceRow.Elements(W + "tc"))
            {
                if (column >= columnCount) break;
                var span = Math.Min(SpanOf(cell), columnCount - column);
                var target = row.Cells[column];
                if (span > 1) target.MergeRight = span - 1;
                foreach (var paragraph in cell.Elements(W + "p")) AddParagraph(context, target.AddParagraph(), paragraph, null);
                column += span;
            }
        }
        section.AddParagraph();
    }

    private static int SpanOf(XElement cell) =>
        int.TryParse(cell.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val")?.Value, out var span) ? Math.Max(1, span) : 1;
}
