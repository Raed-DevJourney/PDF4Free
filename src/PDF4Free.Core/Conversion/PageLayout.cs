using System.Windows;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Core.Conversion;

internal sealed record LayoutWord(string Text, double Left, double Right);

internal sealed record LayoutLine(string Text, Rect Box, double FontSize, bool Bold, bool Italic, uint Color, IReadOnlyList<LayoutWord> Words);

internal sealed record LayoutParagraph(string Text, double Left, double FontSize, bool Bold, bool Italic, uint Color, double GapBefore);

/// <summary>Rebuilds lines and paragraphs from the positioned characters of a page, for the export converters.</summary>
internal static class PageLayout
{
    public static List<LayoutLine> Lines(PdfiumDocument doc, int pageIndex)
    {
        var text = doc.GetText(pageIndex);
        var geometry = doc.GetGeometry(pageIndex);
        var runs = doc.GetPageObjects(pageIndex).Where(o => o.Kind == PageObjectKind.Text).ToList();
        var lines = new List<LayoutLine>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text.Text[i] != '\n' && text.Text[i] != '\r') continue;
            if (i > start && BuildLine(text, geometry, runs, start, i) is { } line) lines.Add(line);
            start = i + 1;
        }
        return lines;
    }

    private static LayoutLine? BuildLine(PageText text, PageGeometry geometry, List<PageObjectInfo> runs, int start, int end)
    {
        var words = new List<LayoutWord>();
        Rect? lineBox = null;
        PdfRect? firstCharBox = null;
        var wordStart = -1;
        for (var i = start; i <= end; i++)
        {
            var separator = i == end || char.IsWhiteSpace(text.Text[i]);
            if (!separator && wordStart < 0) wordStart = i;
            if (!separator || wordStart < 0) continue;
            Rect? box = null;
            for (var c = wordStart; c < i; c++)
            {
                if (text.Boxes[c].IsEmpty) continue;
                firstCharBox ??= text.Boxes[c];
                var view = geometry.ToView(text.Boxes[c]);
                box = box is { } b ? Rect.Union(b, view) : view;
            }
            if (box is { } wordBox)
            {
                words.Add(new LayoutWord(Clean(text.Text[wordStart..i]), wordBox.Left, wordBox.Right));
                lineBox = lineBox is { } l ? Rect.Union(l, wordBox) : wordBox;
            }
            wordStart = -1;
        }
        if (words.Count == 0 || lineBox is not { } boxOfLine) return null;
        var run = firstCharBox is { } probe ? FindRun(runs, probe) : null;
        var size = run?.FontSize ?? Math.Max(6, boxOfLine.Height * 0.9);
        return new LayoutLine(string.Join(' ', words.Select(w => w.Text)), boxOfLine, Math.Round(size * 2) / 2, run?.Bold ?? false, run?.Italic ?? false, run?.ColorRgb ?? 0, words);
    }

    private static PageObjectInfo? FindRun(List<PageObjectInfo> runs, PdfRect charBox)
    {
        var centre = charBox.Center;
        foreach (var run in runs)
        {
            if (run.Bounds.Inflate(0.5).Contains(centre)) return run;
        }
        return null;
    }

    /// <summary>Joins consecutive lines that look like one paragraph: same style, tight spacing, similar left edge.</summary>
    public static List<LayoutParagraph> Paragraphs(List<LayoutLine> lines)
    {
        var paragraphs = new List<LayoutParagraph>();
        LayoutLine? previous = null;
        var current = new List<LayoutLine>();
        var gapBefore = 0.0;

        void Flush()
        {
            if (current.Count == 0) return;
            var first = current[0];
            var text = string.Join(' ', current.Select(l => l.Text));
            paragraphs.Add(new LayoutParagraph(text, current.Min(l => l.Box.Left), first.FontSize, first.Bold, first.Italic, first.Color, gapBefore));
            current.Clear();
        }

        foreach (var line in lines)
        {
            if (previous is not null)
            {
                var gap = line.Box.Top - previous.Box.Bottom;
                var sameStyle = Math.Abs(line.FontSize - previous.FontSize) < 0.6 && line.Bold == previous.Bold;
                var continues = sameStyle && gap > -previous.Box.Height * 0.5 && gap < previous.Box.Height * 0.75 && Math.Abs(line.Box.Left - previous.Box.Left) < previous.FontSize * 3;
                if (!continues)
                {
                    Flush();
                    gapBefore = Math.Clamp(gap, 0, 36);
                }
            }
            current.Add(line);
            previous = line;
        }
        Flush();
        return paragraphs;
    }

    /// <summary>Removes characters that are not allowed in XML text.</summary>
    public static string Clean(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var n = 0;
        foreach (var ch in value)
        {
            if (ch == '\t' || ch >= ' ' && ch != '￾' && ch != '￿' && !char.IsSurrogate(ch)) buffer[n++] = ch;
        }
        return new string(buffer[..n]);
    }
}
