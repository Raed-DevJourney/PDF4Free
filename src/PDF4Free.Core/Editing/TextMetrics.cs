using System.Windows;
using System.Windows.Media;

namespace PDF4Free.Core.Editing;

/// <summary>Measures and wraps text set in Helvetica, using Arial's metrics (the two share advance widths).</summary>
internal static class TextMetrics
{
    private static readonly Lazy<GlyphTypeface?> Regular = new(() => Load(FontWeights.Normal));
    private static readonly Lazy<GlyphTypeface?> Bold = new(() => Load(FontWeights.Bold));

    public const double CapHeight = 0.716;
    public const double Ascent = 0.905;
    public const double Descent = 0.212;

    public static double Width(string text, double size, bool bold = false)
    {
        var face = (bold ? Bold : Regular).Value;
        if (face is null) return text.Length * size * 0.5;
        double total = 0;
        foreach (var ch in text)
        {
            total += face.CharacterToGlyphMap.TryGetValue(ch, out var glyph) ? face.AdvanceWidths[glyph] : 0.5;
        }
        return total * size;
    }

    /// <summary>Breaks text into lines no wider than <paramref name="maxWidth"/>; explicit line breaks are kept.</summary>
    public static List<string> Wrap(string text, double size, double maxWidth, bool bold = false)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            WrapParagraph(paragraph, size, maxWidth, bold, lines);
        }
        return lines;
    }

    private static void WrapParagraph(string paragraph, double size, double maxWidth, bool bold, List<string> lines)
    {
        var current = "";
        foreach (var word in paragraph.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Width(candidate, size, bold) <= maxWidth || current.Length == 0)
            {
                current = candidate;
                continue;
            }
            lines.Add(current);
            current = word;
        }
        lines.Add(current);
    }

    private static GlyphTypeface? Load(FontWeight weight)
    {
        var typeface = new Typeface(new FontFamily("Arial"), FontStyles.Normal, weight, FontStretches.Normal);
        return typeface.TryGetGlyphTypeface(out var glyphs) ? glyphs : null;
    }
}
