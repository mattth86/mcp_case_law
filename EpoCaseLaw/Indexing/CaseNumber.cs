using System.Text.RegularExpressions;

namespace EpoCaseLaw.Indexing;

public static partial class CaseNumber
{
    public static string Format(string code, string appealNum, int year) =>
        $"{code} {appealNum.PadLeft(4, '0')}/{year % 100:D2}";

    public static string? TryParseCanonical(string input)
    {
        input = input.Trim();
        var m = CasePattern().Match(input);
        if (!m.Success)
            return null;

        var code = m.Groups["code"].Value.ToUpperInvariant();
        var num = m.Groups["num"].Value.PadLeft(4, '0');
        var year = int.Parse(m.Groups["year"].Value);
        if (year < 100)
            year += year >= 70 ? 1900 : 2000;

        return $"{code} {num}/{year % 100:D2}";
    }

    public static bool LooksLikeCaseNumber(string query) => CasePattern().IsMatch(query.Trim());

    [GeneratedRegex(@"^(?<code>[GTJDWR])\s*(?<num>\d{1,4})\s*/\s*(?<year>\d{2,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex CasePattern();
}
