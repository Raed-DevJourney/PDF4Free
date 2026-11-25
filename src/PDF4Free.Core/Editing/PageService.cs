using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDF4Free.Core.Model;

namespace PDF4Free.Core.Editing;

/// <summary>Organises pages: rotate, delete, reorder, insert, extract, split, merge and crop.</summary>
public static class PageService
{
    /// <summary>Rotates pages by a multiple of 90 degrees (clockwise positive).</summary>
    public static byte[] Rotate(byte[] source, IEnumerable<int> pages, int degrees) =>
        PdfIO.Modify(source, doc =>
        {
            foreach (var index in Valid(doc, pages))
            {
                var page = doc.Pages[index];
                var rotation = ((page.Int("/Rotate") + degrees) % 360 + 360) % 360;
                page.Elements.SetInteger("/Rotate", rotation);
            }
        });

    public static byte[] Delete(byte[] source, IEnumerable<int> pages) =>
        PdfIO.Modify(source, doc =>
        {
            var doomed = Valid(doc, pages).OrderByDescending(i => i).ToList();
            if (doomed.Count >= doc.PageCount) throw new InvalidOperationException("A document must keep at least one page.");
            foreach (var index in doomed) doc.Pages.RemoveAt(index);
        });

    /// <summary>Rearranges the document so that new page i is old page <paramref name="order"/>[i]. Pages left out are removed.</summary>
    public static byte[] Reorder(byte[] source, IReadOnlyList<int> order)
    {
        if (order.Count == 0) throw new InvalidOperationException("A document must keep at least one page.");
        using var input = PdfIO.Open(source, PdfDocumentOpenMode.Import);
        var keepsEveryPageOnce = order.Count == input.PageCount && order.Distinct().Count() == order.Count;
        if (!keepsEveryPageOnce) return Extract(source, order);
        return PdfIO.Modify(source, doc =>
        {
            var current = Enumerable.Range(0, doc.PageCount).ToList();
            for (var target = 0; target < order.Count; target++)
            {
                var from = current.IndexOf(order[target]);
                if (from == target) continue;
                doc.Pages.MovePage(from, target);
                current.RemoveAt(from);
                current.Insert(target, order[target]);
            }
        });
    }

    /// <summary>Moves a block of pages so that it starts at <paramref name="insertBefore"/> (an index in the original numbering).</summary>
    public static byte[] Move(byte[] source, IReadOnlyCollection<int> pages, int insertBefore, int pageCount)
    {
        var moving = pages.Distinct().OrderBy(i => i).ToList();
        var order = new List<int>();
        for (var i = 0; i <= pageCount; i++)
        {
            if (i == insertBefore) order.AddRange(moving);
            if (i < pageCount && !moving.Contains(i)) order.Add(i);
        }
        return Reorder(source, order);
    }

    public static byte[] InsertBlank(byte[] source, int index, double widthPoints, double heightPoints) =>
        PdfIO.Modify(source, doc =>
        {
            var page = doc.InsertPage(Math.Clamp(index, 0, doc.PageCount));
            page.Width = XUnit.FromPoint(widthPoints);
            page.Height = XUnit.FromPoint(heightPoints);
        });

    /// <summary>Inserts pages of another PDF before <paramref name="index"/>. <paramref name="otherPages"/> null means all of them.</summary>
    public static byte[] InsertFrom(byte[] source, int index, byte[] other, IReadOnlyList<int>? otherPages = null) =>
        PdfIO.Modify(source, doc =>
        {
            using var donor = PdfIO.Open(other, PdfDocumentOpenMode.Import);
            var at = Math.Clamp(index, 0, doc.PageCount);
            foreach (var i in otherPages ?? Enumerable.Range(0, donor.PageCount).ToList())
            {
                if (i < 0 || i >= donor.PageCount) continue;
                doc.InsertPage(at++, donor.Pages[i]);
            }
        });

    public static byte[] Duplicate(byte[] source, IReadOnlyList<int> pages)
    {
        var ordered = pages.Distinct().OrderBy(i => i).ToList();
        if (ordered.Count == 0) return source;
        return InsertFrom(source, ordered[^1] + 1, source, ordered);
    }

    public static byte[] Replace(byte[] source, int index, byte[] other, int otherPage)
    {
        var inserted = InsertFrom(source, index + 1, other, [otherPage]);
        return Delete(inserted, [index]);
    }

    /// <summary>Builds a new PDF from the listed pages, in the listed order (a page may appear more than once).</summary>
    public static byte[] Extract(byte[] source, IEnumerable<int> pages)
    {
        using var input = PdfIO.Open(source, PdfDocumentOpenMode.Import);
        using var output = new PdfDocument();
        CopyInfo(input, output);
        foreach (var index in pages)
        {
            if (index >= 0 && index < input.PageCount) output.AddPage(input.Pages[index]);
        }
        if (output.PageCount == 0) throw new InvalidOperationException("No pages were selected.");
        return PdfIO.Save(output);
    }

    public static List<byte[]> SplitEvery(byte[] source, int pagesPerFile, int pageCount)
    {
        var size = Math.Max(1, pagesPerFile);
        var parts = new List<byte[]>();
        for (var start = 0; start < pageCount; start += size)
        {
            parts.Add(Extract(source, Enumerable.Range(start, Math.Min(size, pageCount - start))));
        }
        return parts;
    }

    public static List<byte[]> SplitRanges(byte[] source, IEnumerable<IReadOnlyList<int>> ranges) =>
        ranges.Where(r => r.Count > 0).Select(r => Extract(source, r)).ToList();

    /// <summary>Joins documents end to end. With <paramref name="bookmarkTitles"/>, each source gets a top-level bookmark.</summary>
    public static byte[] Merge(IReadOnlyList<byte[]> sources, IReadOnlyList<string>? bookmarkTitles = null)
    {
        if (sources.Count == 0) throw new InvalidOperationException("Nothing to combine.");
        using var output = new PdfDocument();
        for (var s = 0; s < sources.Count; s++)
        {
            using var input = PdfIO.Open(sources[s], PdfDocumentOpenMode.Import);
            if (s == 0) CopyInfo(input, output);
            PdfPage? first = null;
            for (var i = 0; i < input.PageCount; i++)
            {
                var added = output.AddPage(input.Pages[i]);
                first ??= added;
            }
            if (bookmarkTitles is not null && first is not null && s < bookmarkTitles.Count)
            {
                output.Outlines.Add(bookmarkTitles[s], first, true);
            }
        }
        return PdfIO.Save(output);
    }

    /// <summary>Sets the visible area of pages. The rectangle is given in view points of each page.</summary>
    public static byte[] Crop(byte[] source, IEnumerable<int> pages, System.Windows.Rect viewRect) =>
        PdfIO.Modify(source, doc =>
        {
            foreach (var index in Valid(doc, pages))
            {
                var page = doc.Pages[index];
                var geometry = PdfX.Geometry(page);
                var clipped = System.Windows.Rect.Intersect(viewRect, new System.Windows.Rect(0, 0, geometry.ViewWidth, geometry.ViewHeight));
                if (clipped.IsEmpty || clipped.Width < 10 || clipped.Height < 10) continue;
                page.Elements["/CropBox"] = geometry.ToPage(clipped).ToArray(doc);
            }
        });

    /// <summary>Trims the given margins (in points, measured on the displayed page) from each page.</summary>
    public static byte[] CropMargins(byte[] source, IEnumerable<int> pages, double left, double top, double right, double bottom) =>
        PdfIO.Modify(source, doc =>
        {
            foreach (var index in Valid(doc, pages))
            {
                var page = doc.Pages[index];
                var geometry = PdfX.Geometry(page);
                var rect = new System.Windows.Rect(left, top, Math.Max(10, geometry.ViewWidth - left - right), Math.Max(10, geometry.ViewHeight - top - bottom));
                page.Elements["/CropBox"] = geometry.ToPage(rect).ToArray(doc);
            }
        });

    public static byte[] RemoveCrop(byte[] source, IEnumerable<int> pages) =>
        PdfIO.Modify(source, doc =>
        {
            foreach (var index in Valid(doc, pages)) doc.Pages[index].Elements.Remove("/CropBox");
        });

    /// <summary>Parses "1-3, 5, 8-" style input into zero-based page indexes. Returns null when the text is not valid.</summary>
    public static List<int>? ParseRange(string text, int pageCount)
    {
        var result = new List<int>();
        foreach (var raw in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = raw.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(raw, out var single) || single < 1 || single > pageCount) return null;
                result.Add(single - 1);
                continue;
            }
            var fromText = raw[..dash].Trim();
            var toText = raw[(dash + 1)..].Trim();
            var from = fromText.Length == 0 ? 1 : int.TryParse(fromText, out var f) ? f : -1;
            var to = toText.Length == 0 ? pageCount : int.TryParse(toText, out var t) ? t : -1;
            if (from < 1 || to < 1 || from > pageCount || to > pageCount) return null;
            var step = from <= to ? 1 : -1;
            for (var i = from; i != to + step; i += step) result.Add(i - 1);
        }
        return result.Count == 0 ? null : result;
    }

    private static IEnumerable<int> Valid(PdfDocument doc, IEnumerable<int> pages) =>
        pages.Where(i => i >= 0 && i < doc.PageCount).Distinct();

    private static void CopyInfo(PdfDocument from, PdfDocument to)
    {
        to.Info.Title = from.Info.Title;
        to.Info.Author = from.Info.Author;
        to.Info.Subject = from.Info.Subject;
        to.Info.Keywords = from.Info.Keywords;
    }
}
