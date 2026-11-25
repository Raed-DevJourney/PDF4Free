using System.IO.Compression;
using System.Text;

namespace PDF4Free.Core.Conversion;

/// <summary>Writes an Office Open XML package (docx, xlsx, pptx): a zip with parts, content types and relationships.</summary>
internal sealed class OpenXmlPackage : IDisposable
{
    private readonly FileStream _file;
    private readonly ZipArchive _zip;
    private readonly StringBuilder _overrides = new();
    private readonly StringBuilder _rootRelationships = new();
    private int _rootRelationshipCount;

    public OpenXmlPackage(string path)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        _zip = new ZipArchive(_file, ZipArchiveMode.Create, leaveOpen: true);
    }

    public void AddOverride(string partName, string contentType) =>
        _overrides.Append($"<Override PartName=\"{partName}\" ContentType=\"{contentType}\"/>");

    public void AddRootRelationship(string type, string target) =>
        _rootRelationships.Append($"<Relationship Id=\"rId{++_rootRelationshipCount}\" Type=\"{type}\" Target=\"{target}\"/>");

    public void AddPart(string name, string xml)
    {
        using var writer = new StreamWriter(_zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(xml);
    }

    public void AddBinary(string name, byte[] data)
    {
        using var stream = _zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
        stream.Write(data, 0, data.Length);
    }

    public static string Relationships(string items) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + items + "</Relationships>";

    public void Dispose()
    {
        AddPart("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
            _overrides + "</Types>");
        AddPart("_rels/.rels", Relationships(_rootRelationships.ToString()));
        _zip.Dispose();
        _file.Dispose();
    }

    /// <summary>The smallest theme PowerPoint accepts without offering to repair the file.</summary>
    public const string ThemeXml =
        "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"PDF4Free\"><a:themeElements>" +
        "<a:clrScheme name=\"Office\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
        "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2><a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1>" +
        "<a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>" +
        "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink>" +
        "<a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
        "<a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
        "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"Office\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>" +
        "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>" +
        "<a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>" +
        "<a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>" +
        "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
        "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>" +
        "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>";
}
