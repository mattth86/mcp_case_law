using System.Text.RegularExpressions;

namespace EpoCaseLaw.Indexing;

/// <summary>
/// Normalizes ep-legal-ref-presentation codes ("EPC2000_Art_123(2)_(2007)", "RPBA_2020_Art_013(2)",
/// "EPC1973_R_057a") and user-typed provisions ("Art. 123(2) EPC", "Rule 103(1)(a)") to one canonical
/// form, e.g. "Art. 123(2) EPC" / "R. 103(1)(a) EPC" / "Art. 13(2) RPBA".
/// Era variants (EPC 1973/2000, RPBA 2007/2020) are deliberately collapsed so that a single
/// lookup finds all of them; the original code is preserved in the raw column.
/// </summary>
public static partial class LegalRefNormalizer
{
    private static readonly Dictionary<string, string> IssuerAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EPC"] = "EPC",
        ["VOBK"] = "RPBA",       // German name for the RPBA
        ["RPBA"] = "RPBA",
        ["PRBA"] = "RPBA",       // typos present in the data
        ["RBPA"] = "RPBA",
        ["RPEBA"] = "RPEBA",
        ["RPBEA"] = "RPEBA",
        ["VOGBK"] = "RPEBA",
        ["REE"] = "REE",
        ["PCT"] = "PCT",
        ["RFees"] = "RFees",
        ["Guidelines"] = "Guidelines",
    };

    private static readonly string[] KindSeparators = ["_Art_", "_Rule_", "_R_"];

    public static (string Normalized, string Raw) Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ("", raw);

        raw = raw.Trim();
        foreach (var sep in KindSeparators)
        {
            var idx = raw.LastIndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx <= 0)
                continue;

            var issuer = ResolveIssuer(raw[..idx]);
            var numberPart = raw[(idx + sep.Length)..];
            var m = NumberAndSubs().Match(numberPart);
            if (!m.Success)
                break;

            var kind = sep.Contains("Art", StringComparison.OrdinalIgnoreCase) ? "Art." : "R.";
            return (FormatRef(kind, m.Groups["num"].Value, m.Groups["sub"].Value, issuer), raw);
        }

        return (raw, raw);
    }

    public static string NormalizeQueryProvision(string input)
    {
        input = input.Trim();
        var m = QueryPattern().Match(input);
        if (!m.Success)
            return input;

        var kind = m.Groups["kind"].Value.StartsWith("a", StringComparison.OrdinalIgnoreCase) ? "Art." : "R.";
        var issuer = m.Groups["issuer"].Success ? ResolveIssuer(m.Groups["issuer"].Value) : "EPC";
        return FormatRef(kind, m.Groups["num"].Value, m.Groups["sub"].Value, issuer);
    }

    /// <summary>Variants to look for in book/guidelines prose, e.g. ["Art. 123(2)"] or ["R. 103", "Rule 103"].</summary>
    public static IReadOnlyList<string> ProseVariants(string normalized)
    {
        var withoutIssuer = TrailingIssuer().Replace(normalized, "");
        return withoutIssuer.StartsWith("R.", StringComparison.Ordinal)
            ? [withoutIssuer, withoutIssuer.Replace("R.", "Rule", StringComparison.Ordinal)]
            : [withoutIssuer];
    }

    private static string ResolveIssuer(string prefix)
    {
        prefix = prefix.Trim().TrimStart('_');
        if (prefix.StartsWith("blank", StringComparison.OrdinalIgnoreCase))
            prefix = prefix["blank".Length..];

        // Collapse era designations: EPC1973, EPC2000, RPBA_2020, RFees1973, PCT2003 …
        prefix = YearSuffix().Replace(prefix, "").Trim(' ', '_');
        return IssuerAliases.TryGetValue(prefix, out var name) ? name : prefix;
    }

    private static string FormatRef(string kind, string num, string sub, string issuer)
    {
        var digits = num.TrimStart('0');
        if (digits.Length == 0 || !char.IsAsciiDigit(digits[0]))
            digits = "0" + digits;
        return $"{kind} {digits}{sub} {issuer}".Trim();
    }

    // "013(2)", "057a", "112a(2)(c)", optionally followed by "_(2007)" era notes and/or
    // sentence qualifiers ("_Sent_3", " Satz 3") or "_Protocol_Interpretation" (Art. 69).
    // Sentence-level specificity is collapsed into the article; the raw column keeps it.
    [GeneratedRegex(@"^(?<num>\d+[a-z]*(?:\.\d+)?)\s*(?<sub>(?:\([^)\s]+\))*)(?:[_\s]*\(\d{4}\))?(?:[_\s]+(?:Sent|Satz)[_\s.]*\d+)?(?:_Protocol_Interpretation)?$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberAndSubs();

    // "Art. 123(2) EPC", "Article 112a(2)(c)", "Rule 103(1)(a) EPC", "R 103", "Art. 13(2) RPBA 2020",
    // "Rule 4.10 PCT" (PCT rules use decimal numbering)
    [GeneratedRegex(@"^(?<kind>Art(?:icle)?|R(?:ule)?)\.?\s*(?<num>\d+[a-z]*(?:\.\d+)?)\s*(?<sub>(?:\([^)\s]+\))*)\s*(?<issuer>[A-Za-z][A-Za-z0-9 _]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex QueryPattern();

    [GeneratedRegex(@"\s+(EPC|RPBA|RPEBA|REE|PCT|RFees|Guidelines)$")]
    private static partial Regex TrailingIssuer();

    [GeneratedRegex(@"[ _]?(19|20)\d\d")]
    private static partial Regex YearSuffix();
}
