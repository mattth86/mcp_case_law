using System.Text;
using System.Text.RegularExpressions;
using EpoCaseLaw.Indexing;

namespace EpoCaseLaw.Search;

public static partial class FtsHelper
{
    public static string BuildMatchQuery(string userQuery)
    {
        userQuery = userQuery.Trim();
        if (string.IsNullOrEmpty(userQuery))
            return "";

        if (CaseNumber.LooksLikeCaseNumber(userQuery))
        {
            var cn = CaseNumber.TryParseCanonical(userQuery);
            if (cn is not null)
                return $"case_number : \"{Escape(cn)}\"";
        }

        if (userQuery.StartsWith('"') && userQuery.EndsWith('"'))
            return EscapeQuoted(userQuery);

        // A case number inside a longer query ("late-filed requests T 664/16") is matched
        // canonically: against the case_number column and as a phrase in the text (which
        // also finds decisions citing it). Splitting on whitespace would shred it into
        // useless tokens ("T", "664/16") otherwise.
        var clauses = new List<string>();
        foreach (Match m in InlineCaseNumberPattern().Matches(userQuery))
        {
            var cn = CaseNumber.TryParseCanonical(m.Value);
            if (cn is not null)
                clauses.Add($"(\"{Escape(cn)}\" OR case_number : \"{Escape(cn)}\")");
        }

        var remainder = clauses.Count > 0 ? InlineCaseNumberPattern().Replace(userQuery, " ") : userQuery;
        var termsQuery = BuildTermQuery(remainder);
        if (clauses.Count == 0)
            return termsQuery;

        // OR, not AND: the cited decision itself may be in another language than the
        // accompanying words (e.g. an English description of a German decision), and
        // bm25's heavy case_number weight ranks the direct hit first anyway.
        // (Parenthesized sub-expressions can't sit in an implicit-AND phrase list.)
        if (termsQuery.Length > 0)
            clauses.Insert(0, $"({termsQuery})");
        return string.Join(" OR ", clauses);
    }

    // Every term is emitted quoted ("term"): bare terms with punctuation
    // (Art. 123(2), problem-solution) are FTS5 syntax errors otherwise.
    // Prefix expansion (*) only on longer terms — "a*"/"be*" would expand to a
    // huge slice of the term dictionary and dominate query time.
    private static string BuildTermQuery(string text)
    {
        var terms = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', terms.Select(t => t.Length >= 4 ? $"\"{Escape(t)}\"*" : $"\"{Escape(t)}\""));
    }

    public static string Escape(string term) =>
        term.Replace("\"", "\"\"", StringComparison.Ordinal);

    private static string EscapeQuoted(string phrase) =>
        $"\"{Escape(phrase.Trim('"'))}\"";

    [GeneratedRegex(@"\b([GTJDWR])\s*(\d{1,4})\s*/\s*(\d{2,4})\b", RegexOptions.IgnoreCase)]
    public static partial Regex InlineCaseNumberPattern();
}
