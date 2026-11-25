namespace PDF4Free.Core.Model;

public sealed class PdfPasswordRequiredException(bool wrongPassword)
    : Exception(wrongPassword ? "The password is incorrect." : "This document is password protected.")
{
    public bool WrongPassword { get; } = wrongPassword;
}

public sealed class PdfLoadException(string message) : Exception(message);

/// <summary>The extracted text of one page with one bounding box per character.</summary>
public sealed class PageText
{
    public PageText(string text, PdfRect[] boxes)
    {
        Text = text;
        Boxes = boxes;
    }

    public string Text { get; }
    public PdfRect[] Boxes { get; }
    public int Length => Text.Length;

    /// <summary>Index of the character under the point, or the nearest one within <paramref name="tolerance"/> points; -1 if none.</summary>
    public int IndexAt(PdfPoint p, double tolerance)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < Boxes.Length; i++)
        {
            var b = Boxes[i];
            if (b.IsEmpty) continue;
            var dx = Math.Max(Math.Max(b.Left - p.X, 0), p.X - b.Right);
            var dy = Math.Max(Math.Max(b.Bottom - p.Y, 0), p.Y - b.Top);
            var distance = dx + dy * 3;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return bestDistance <= tolerance ? best : -1;
    }

    /// <summary>Merged per-line rectangles covering the characters in [start, start + length).</summary>
    public List<PdfRect> GetRangeRects(int start, int length)
    {
        var result = new List<PdfRect>();
        var end = Math.Min(Boxes.Length, start + length);
        PdfRect? current = null;
        for (var i = Math.Max(0, start); i < end; i++)
        {
            var b = Boxes[i];
            if (b.IsEmpty) continue;
            if (current is { } c && SameLine(c, b))
            {
                current = c.Union(b);
            }
            else
            {
                if (current is { } done) result.Add(done);
                current = b;
            }
        }
        if (current is { } last) result.Add(last);
        return result;
    }

    public string Substring(int start, int length)
    {
        start = Math.Clamp(start, 0, Text.Length);
        length = Math.Clamp(length, 0, Text.Length - start);
        return Text.Substring(start, length);
    }

    /// <summary>Expands a character index to the word around it.</summary>
    public (int Start, int Length) WordAt(int index)
    {
        if (index < 0 || index >= Text.Length) return (0, 0);
        var s = index;
        var e = index;
        while (s > 0 && !char.IsWhiteSpace(Text[s - 1])) s--;
        while (e < Text.Length - 1 && !char.IsWhiteSpace(Text[e + 1])) e++;
        return (s, e - s + 1);
    }

    private static bool SameLine(PdfRect a, PdfRect b)
    {
        var overlap = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        var minHeight = Math.Min(a.Height, b.Height);
        var gap = b.Left - a.Right;
        return overlap > minHeight * 0.4 && gap > -a.Height * 2 && gap < a.Height * 4;
    }
}

public sealed record SearchHit(int PageIndex, int CharIndex, int Length, IReadOnlyList<PdfRect> Rects, string Context);

public sealed class BookmarkNode
{
    public string Title { get; init; } = "";
    public int PageIndex { get; init; } = -1;
    public double? Top { get; init; }
    public string? Uri { get; init; }
    public List<BookmarkNode> Children { get; } = [];
}

public sealed record LinkInfo(PdfRect Rect, int PageIndex, double? Top, string? Uri);

public sealed record AnnotationInfo(
    int PageIndex,
    int Index,
    string Subtype,
    PdfRect Rect,
    string Contents,
    string Author,
    string Modified,
    uint ColorRgb,
    bool Hidden)
{
    public bool IsMarkup => Subtype is not ("Link" or "Widget" or "Popup");
    public string DisplayType => Subtype switch
    {
        "Text" => "Sticky note",
        "FreeText" => "Text box",
        "StrikeOut" => "Strikethrough",
        "Ink" => "Drawing",
        "Square" => "Rectangle",
        "Circle" => "Ellipse",
        "Redact" => "Redaction mark",
        _ => Subtype,
    };
}

public enum PageObjectKind { Text, Path, Image, Shading, Form, Unknown }

public sealed record PageObjectInfo(
    int Index,
    PageObjectKind Kind,
    PdfRect Bounds,
    string Text,
    double FontSize,
    string FontName,
    bool Bold,
    bool Italic,
    uint ColorRgb,
    PdfPoint Origin);

public sealed record AttachmentInfo(int Index, string Name, long Size);

public sealed record RawSignature(byte[] Contents, int[] ByteRange, string SubFilter, string Reason, string Time);

public sealed record DocumentSecurity(bool IsEncrypted, int Revision, uint Permissions)
{
    /// <summary>
    /// True when nothing is restricted: the file is not encrypted, or it was opened with the permissions password
    /// (PDFium then reports every permission bit set; the two lowest bits are reserved and always clear).
    /// </summary>
    public bool OwnerAccess => !IsEncrypted || (Permissions | 0x3) == 0xFFFFFFFF;
    public bool CanPrint => OwnerAccess || (Permissions & 0x4) != 0;
    public bool CanModify => OwnerAccess || (Permissions & 0x8) != 0;
    public bool CanCopy => OwnerAccess || (Permissions & 0x10) != 0;
    public bool CanAnnotate => OwnerAccess || (Permissions & 0x20) != 0;
    public bool CanFillForms => OwnerAccess || (Permissions & 0x100) != 0 || CanAnnotate;
    public bool CanAssemble => OwnerAccess || (Permissions & 0x400) != 0 || CanModify;
}
