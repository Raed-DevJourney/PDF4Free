using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PDF4Free.Core.Editing;

/// <summary>
/// The date or time format of a text field. PDF has no date field type: a "date field" is a text field whose
/// format and keystroke actions call Acrobat's AFDate_FormatEx / AFDate_KeystrokeEx with a pattern such as
/// "dd/mm/yyyy HH:MM". <see cref="Pattern"/> is that pattern, in Acrobat's notation (m = month, M = minute).
/// </summary>
public sealed partial record DateFieldFormat(string Pattern)
{
    /// <summary>Patterns offered when a date field is designed.</summary>
    public static readonly IReadOnlyList<string> DatePatterns =
        ["dd/mm/yyyy", "mm/dd/yyyy", "yyyy-mm-dd", "dd.mm.yyyy", "d mmm yyyy", "d mmmm yyyy", "mmm d, yyyy", "dd-mmm-yy"];

    /// <summary>Patterns offered when a date and time field is designed.</summary>
    public static readonly IReadOnlyList<string> DateTimePatterns =
        ["dd/mm/yyyy HH:MM", "dd/mm/yyyy h:MM tt", "mm/dd/yyyy HH:MM", "mm/dd/yyyy h:MM tt", "yyyy-mm-dd HH:MM", "yyyy-mm-dd HH:MM:ss", "d mmm yyyy HH:MM"];

    // The numbered formats of Acrobat's AFDate_Format(n) and AFTime_Format(n).
    private static readonly string[] NumberedDates =
    [
        "m/d", "m/d/yy", "mm/dd/yy", "mm/yy", "d-mmm", "d-mmm-yy", "dd-mmm-yy", "yy-mm-dd", "mmm-yy", "mmmm-yy",
        "mmm d, yyyy", "mmmm d, yyyy", "m/d/yy h:MM tt", "m/d/yy HH:MM",
    ];

    private static readonly string[] NumberedTimes = ["HH:MM", "h:MM tt", "HH:MM:ss", "h:MM:ss tt"];

    private static readonly string[] IsoPatterns =
        ["yyyy-M-d", "yyyy-M-d H:m", "yyyy-M-d H:m:s", "yyyy-M-d'T'H:m", "yyyy-M-d'T'H:m:s"];

    private static readonly CultureInfo English = AcrobatEnglish();

    /// <summary>True when the pattern shows a day, month or year.</summary>
    public bool HasDate => Pattern.AsSpan().IndexOfAny('d', 'm', 'y') >= 0;

    /// <summary>True when the pattern shows hours or minutes.</summary>
    public bool HasTime => Pattern.AsSpan().IndexOfAny('h', 'H', 'M') >= 0;

    public bool HasSeconds => Pattern.Contains('s');

    public bool TwelveHour => Pattern.Contains('h');

    /// <summary>The script of the field's format action (/AA /F).</summary>
    public string FormatScript => $"{Family}_FormatEx(\"{Pattern}\");";

    /// <summary>The script of the field's keystroke action (/AA /K).</summary>
    public string KeystrokeScript => $"{Family}_KeystrokeEx(\"{Pattern}\");";

    private string Family => HasDate ? "AFDate" : "AFTime";

    /// <summary>What a value looks like in this format, using a day that cannot be mistaken for a month.</summary>
    public string Example => Format(new DateTime(DateTime.Today.Year, 12, 31, 14, 30, 0));

    /// <summary>"Date", "Date and time" or "Time".</summary>
    public string Kind => !HasDate ? "Time" : HasTime ? "Date and time" : "Date";

    public string Format(DateTime value) => value.ToString(NetPattern(relaxed: false), English);

    /// <summary>
    /// Reads a date typed by a person: in the field's own pattern (with or without leading zeros), in ISO form
    /// (2026-11-25, optionally with a time), or with the month written out.
    /// </summary>
    public bool TryParse(string text, out DateTime value)
    {
        text = text.Trim();
        const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces;
        string[] own = [NetPattern(relaxed: false), NetPattern(relaxed: true)];
        if (DateTime.TryParseExact(text, own, English, styles, out value)) return true;
        if (DateTime.TryParseExact(text, IsoPatterns, English, styles, out value)) return true;
        return MonthName().IsMatch(text) && DateTime.TryParse(text, English, styles, out value);
    }

    /// <summary>The text to store for <paramref name="typed"/>: rewritten in the field's pattern when it is a date.</summary>
    public string Normalize(string typed) => TryParse(typed, out var value) ? Format(value) : typed;

    /// <summary>Finds the date format in the script of a field's format or keystroke action; null when there is none.</summary>
    public static DateFieldFormat? FromScript(string script)
    {
        if (ExplicitCall().Match(script) is { Success: true } named) return new DateFieldFormat(named.Groups[2].Value);
        if (NumberedCall().Match(script) is not { Success: true } numbered) return null;
        var table = numbered.Groups[1].Value == "Date" ? NumberedDates : NumberedTimes;
        var index = int.Parse(numbered.Groups[2].Value, English);
        return index < table.Length ? new DateFieldFormat(table[index]) : null;
    }

    [GeneratedRegex(@"AF(Date|Time)_(?:Format|Keystroke)Ex\(\s*[""']([^""']+)[""']\s*\)")]
    private static partial Regex ExplicitCall();

    [GeneratedRegex(@"AF(Date|Time)_(?:Format|Keystroke)\(\s*(\d+)\s*\)")]
    private static partial Regex NumberedCall();

    [GeneratedRegex(@"\p{L}{3}")]
    private static partial Regex MonthName();

    /// <summary>English month and day names with "am"/"pm" in lower case, which is how Acrobat writes these fields.</summary>
    private static CultureInfo AcrobatEnglish()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.AMDesignator = "am";
        culture.DateTimeFormat.PMDesignator = "pm";
        return CultureInfo.ReadOnly(culture);
    }

    /// <summary>Splits a pattern into runs of one letter ("dd", "mmm", "yyyy") and single literal characters.</summary>
    private static List<string> Tokenize(string pattern)
    {
        var tokens = new List<string>();
        for (var i = 0; i < pattern.Length;)
        {
            var length = 1;
            if (IsField(pattern[i]))
            {
                while (i + length < pattern.Length && pattern[i + length] == pattern[i]) length++;
            }
            tokens.Add(pattern.Substring(i, length));
            i += length;
        }
        return tokens;
    }

    private static bool IsField(char c) => c is 'd' or 'm' or 'y' or 'h' or 'H' or 'M' or 's' or 't';

    /// <summary>
    /// The same pattern in .NET notation. Relaxed, the two-digit parts accept one digit as well, so "3/4/2026"
    /// is read by a "dd/mm/yyyy" field as the 3rd of April rather than rejected.
    /// </summary>
    private string NetPattern(bool relaxed)
    {
        var net = new StringBuilder();
        foreach (var token in Tokenize(Pattern))
        {
            if (!IsField(token[0]))
            {
                // Spaces stay as they are: the parser skips white space by itself and would not find an escaped one.
                if (token[0] != ' ') net.Append('\\');
                net.Append(token);
                continue;
            }
            var letter = token[0] switch { 'm' => 'M', 'M' => 'm', var same => same };
            var count = relaxed && token.Length == 2 && token[0] is not ('y' or 't') ? 1 : token.Length;
            net.Append(letter, count);
        }
        // A lone letter would be taken for one of .NET's standard formats.
        return net.Length == 1 ? "%" + net : net.ToString();
    }
}
