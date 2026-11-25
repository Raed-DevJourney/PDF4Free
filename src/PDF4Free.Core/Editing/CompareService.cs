using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Core.Editing;

/// <summary>A run of words that exists in only one of the two compared documents.</summary>
public sealed record DiffSpan(int PageIndex, IReadOnlyList<PdfRect> Rects, string Text);

public sealed record CompareResult(IReadOnlyList<DiffSpan> Removed, IReadOnlyList<DiffSpan> Added, int OldWordCount, int NewWordCount)
{
    public bool Identical => Removed.Count == 0 && Added.Count == 0;
}

/// <summary>Compares the text of two documents word by word (Myers' shortest-edit-script algorithm).</summary>
public static class CompareService
{
    private sealed record Word(string Text, int Page, int Start, int Length);

    private const int MaxEditDistance = 6000;

    public static CompareResult Compare(PdfiumDocument oldDoc, PdfiumDocument newDoc)
    {
        var oldWords = Words(oldDoc);
        var newWords = Words(newDoc);
        var (removed, added) = Diff(oldWords.Select(w => w.Text).ToArray(), newWords.Select(w => w.Text).ToArray());
        return new CompareResult(Spans(oldDoc, oldWords, removed), Spans(newDoc, newWords, added), oldWords.Count, newWords.Count);
    }

    private static List<Word> Words(PdfiumDocument doc)
    {
        var words = new List<Word>();
        for (var p = 0; p < doc.PageCount; p++)
        {
            var text = doc.GetText(p).Text;
            var start = -1;
            for (var i = 0; i <= text.Length; i++)
            {
                var separator = i == text.Length || char.IsWhiteSpace(text[i]);
                if (!separator && start < 0) start = i;
                if (separator && start >= 0)
                {
                    words.Add(new Word(text[start..i], p, start, i - start));
                    start = -1;
                }
            }
        }
        return words;
    }

    /// <summary>Returns which items of <paramref name="a"/> are missing from <paramref name="b"/> and which items of b are new.</summary>
    internal static (bool[] Removed, bool[] Added) Diff(string[] a, string[] b)
    {
        var removed = new bool[a.Length];
        var added = new bool[b.Length];
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        var n = a.Length - prefix - suffix;
        var m = b.Length - prefix - suffix;
        if (n == 0 || m == 0 || !Myers(a, b, prefix, n, m, removed, added))
        {
            for (var i = 0; i < n; i++) removed[prefix + i] = true;
            for (var j = 0; j < m; j++) added[prefix + j] = true;
        }
        return (removed, added);
    }

    private static bool Myers(string[] a, string[] b, int offset, int n, int m, bool[] removed, bool[] added)
    {
        var max = n + m;
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            if (d > MaxEditDistance) return false;
            var snapshot = new int[2 * d + 1];
            for (var k = -d; k <= d; k++) snapshot[k + d] = v[k + max];
            trace.Add(snapshot);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[k - 1 + max] < v[k + 1 + max]) ? v[k + 1 + max] : v[k - 1 + max] + 1;
                var y = x - k;
                while (x < n && y < m && a[offset + x] == b[offset + y])
                {
                    x++;
                    y++;
                }
                v[k + max] = x;
                if (x < n || y < m) continue;
                Backtrack(trace, d, n, m, offset, removed, added);
                return true;
            }
        }
        return false;
    }

    private static void Backtrack(List<int[]> trace, int lastD, int n, int m, int offset, bool[] removed, bool[] added)
    {
        var x = n;
        var y = m;
        for (var d = lastD; d > 0; d--)
        {
            var before = trace[d];
            int At(int k) => before[k + d];
            var k = x - y;
            var previousK = k == -d || (k != d && At(k - 1) < At(k + 1)) ? k + 1 : k - 1;
            var previousX = At(previousK);
            var previousY = previousX - previousK;
            while (x > previousX && y > previousY)
            {
                x--;
                y--;
            }
            if (x == previousX) added[offset + previousY] = true;
            else removed[offset + previousX] = true;
            x = previousX;
            y = previousY;
        }
    }

    private static List<DiffSpan> Spans(PdfiumDocument doc, List<Word> words, bool[] flagged)
    {
        var spans = new List<DiffSpan>();
        var i = 0;
        while (i < words.Count)
        {
            if (!flagged[i])
            {
                i++;
                continue;
            }
            var first = i;
            while (i + 1 < words.Count && flagged[i + 1] && words[i + 1].Page == words[first].Page) i++;
            var start = words[first].Start;
            var length = words[i].Start + words[i].Length - start;
            var page = words[first].Page;
            var text = doc.GetText(page);
            spans.Add(new DiffSpan(page, text.GetRangeRects(start, length), string.Join(' ', words.Skip(first).Take(i - first + 1).Select(w => w.Text))));
            i++;
        }
        return spans;
    }
}
