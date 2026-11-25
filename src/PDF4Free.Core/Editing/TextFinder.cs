using System.Text.RegularExpressions;
using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Core.Editing;

public sealed record SearchOptions(bool MatchCase = false, bool WholeWord = false, bool Regex = false);

/// <summary>Finds text in a document and reports where each match sits on its page.</summary>
public static class TextFinder
{
    /// <summary>Ready-made patterns for sensitive data, offered by the redaction tool.</summary>
    public static readonly IReadOnlyDictionary<string, string> Patterns = new Dictionary<string, string>
    {
        ["Email addresses"] = @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
        ["Phone numbers"] = @"(?<!\d[\s.-]?)(?:\+\d{1,3}[\s.-]?)?(?:\(\d{2,4}\)[\s.-]?|\d{2,4}[\s.-])\d{3,4}[\s.-]\d{3,4}(?![\s.-]?\d)",
        ["Credit card numbers"] = @"(?<!\d)(?:\d{4}[\s-]?){3}\d{4}(?!\d)",
        ["US Social Security numbers"] = @"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)",
        ["Dates"] = @"(?<!\d)(?:\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}|\d{4}-\d{2}-\d{2})(?!\d)",
        ["IBAN"] = @"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,4})?\b",
    };

    public static IEnumerable<SearchHit> Search(PdfiumDocument doc, string query, SearchOptions options, IEnumerable<int>? pages = null, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        var regex = BuildRegex(query, options);
        foreach (var pageIndex in pages ?? Enumerable.Range(0, doc.PageCount))
        {
            if (cancel.IsCancellationRequested) yield break;
            var text = doc.GetText(pageIndex);
            if (text.Length == 0) continue;
            // Line ends become spaces (same length), so a phrase that wraps still matches and indexes stay valid.
            var flat = text.Text.Replace('\r', ' ').Replace('\n', ' ');
            foreach (Match match in regex.Matches(flat))
            {
                if (match.Length == 0) continue;
                var rects = text.GetRangeRects(match.Index, match.Length);
                if (rects.Count == 0) continue;
                yield return new SearchHit(pageIndex, match.Index, match.Length, rects, Context(flat, match.Index, match.Length));
            }
        }
    }

    private static Regex BuildRegex(string query, SearchOptions options)
    {
        var pattern = options.Regex ? query : Regex.Escape(query).Replace(@"\ ", @"\s+");
        if (options.WholeWord) pattern = $@"(?<!\w){pattern}(?!\w)";
        var flags = RegexOptions.CultureInvariant | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Regex(pattern, flags, TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("The search pattern is not valid: " + ex.Message, ex);
        }
    }

    private static string Context(string text, int index, int length)
    {
        var start = Math.Max(0, index - 30);
        var end = Math.Min(text.Length, index + length + 40);
        var snippet = Regex.Replace(text[start..end], @"\s+", " ").Trim();
        return (start > 0 ? "…" : "") + snippet + (end < text.Length ? "…" : "");
    }
}
