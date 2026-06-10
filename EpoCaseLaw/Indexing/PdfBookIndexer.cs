using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Outline;
using DocumentBookmarkNode = UglyToad.PdfPig.Outline.DocumentBookmarkNode;

namespace EpoCaseLaw.Indexing;

public sealed partial class PdfBookIndexer(ILogger logger)
{
    public void IndexCaseLawBook(string pdfPath, SqliteConnection conn)
    {
        var count = IndexPdf(conn, pdfPath, "clb", DeriveClbSectionId);
        logger.LogInformation("Indexed case law book: {Count} sections from {Path}", count, pdfPath);
    }

    public void IndexGuidelines(string pdfPath, SqliteConnection conn)
    {
        var count = IndexPdf(conn, pdfPath, "gl", DeriveGlSectionId);
        logger.LogInformation("Indexed guidelines: {Count} sections from {Path}", count, pdfPath);
    }

    private int IndexPdf(SqliteConnection conn, string pdfPath, string source,
        Func<IReadOnlyList<string>, string?> sectionIdFn)
    {
        using var document = PdfDocument.Open(pdfPath);
        var leaves = CollectLeafBookmarks(document).OrderBy(b => b.Page).ToList();
        var usedIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var inserted = 0;

        // Several sections can share a page; cutting bodies at heading positions keeps
        // them from all receiving the same whole-page text.
        var pageCache = new Dictionary<int, string>();
        string PageText(int p) =>
            pageCache.TryGetValue(p, out var cached)
                ? cached
                : pageCache[p] = p >= 1 && p <= document.NumberOfPages ? CleanPageText(document.GetPage(p)) : "";

        var headingOffsets = new int[leaves.Count];
        for (var i = 0; i < leaves.Count; i++)
            headingOffsets[i] = FindHeadingOffset(PageText(leaves[i].Page), leaves[i].Breadcrumb[^1]);

        using var tx = conn.BeginTransaction();
        for (var i = 0; i < leaves.Count; i++)
        {
            var leaf = leaves[i];
            var pageFrom = leaf.Page;
            var (body, pageTo) = ExtractBody(PageText, document.NumberOfPages, leaves, headingOffsets, i);
            if (body.Length < 40)
                continue;

            var sectionId = sectionIdFn(leaf.Breadcrumb) ?? CleanTitle(leaf.Breadcrumb[^1]);
            // The same id can legitimately recur (e.g. fallback titles); disambiguate.
            if (usedIds.TryGetValue(sectionId, out var seen))
            {
                usedIds[sectionId] = seen + 1;
                sectionId = $"{sectionId} ({seen + 1})";
            }
            else
            {
                usedIds[sectionId] = 1;
            }

            var title = CleanTitle(leaf.Breadcrumb[^1]);
            var breadcrumb = string.Join(" > ", leaf.Breadcrumb.Select(CleanTitle));
            var sectionDbId = InsertSection(conn, source, sectionId, title, breadcrumb, pageFrom, pageTo, body);
            inserted++;

            var pieces = Chunker.SplitText(body);
            for (var p = 0; p < pieces.Count; p++)
            {
                // Prefix each embedded chunk with its breadcrumb so the vector
                // carries the section's context, not just loose body text.
                InsertChunk(conn, sectionDbId, p, $"{breadcrumb}\n\n{pieces[p]}");
            }
        }

        tx.Commit();
        return inserted;
    }

    private static List<(IReadOnlyList<string> Breadcrumb, int Page)> CollectLeafBookmarks(PdfDocument document)
    {
        var result = new List<(IReadOnlyList<string>, int)>();
        if (document.TryGetBookmarks(out var bookmarks))
            Walk(bookmarks.Roots, []);
        return result;

        void Walk(IEnumerable<BookmarkNode> nodes, List<string> trail)
        {
            foreach (var node in nodes)
            {
                var title = CleanTitle(node.Title);
                var next = new List<string>(trail) { title };
                var children = node.Children.ToList();
                if (children.Count == 0)
                {
                    if (node is DocumentBookmarkNode d && d.PageNumber > 0)
                        result.Add((next, d.PageNumber));
                }
                else
                {
                    Walk(children, next);
                }
            }
        }
    }

    /// <summary>
    /// A section's body runs from its heading position on its start page to the next
    /// section's heading. When a heading can't be located in the page text, the slice
    /// degrades to page granularity (the pre-existing behaviour).
    /// </summary>
    private static (string Body, int PageTo) ExtractBody(
        Func<int, string> pageText, int numberOfPages,
        List<(IReadOnlyList<string> Breadcrumb, int Page)> leaves, int[] headingOffsets, int index)
    {
        var startPage = leaves[index].Page;
        var startOffset = Math.Max(0, headingOffsets[index]);

        int endPage, endOffset;
        if (index + 1 < leaves.Count)
        {
            endPage = leaves[index + 1].Page;
            endOffset = headingOffsets[index + 1];
            if (endOffset < 0)
            {
                // Next heading not locatable: stop at the page before it, unless both
                // sections share the page, where overlap is the only safe option.
                if (endPage > startPage)
                    endPage--;
                endOffset = int.MaxValue;
            }
        }
        else
        {
            endPage = numberOfPages;
            endOffset = int.MaxValue;
        }

        var sb = new StringBuilder();
        for (var p = startPage; p <= endPage && p <= numberOfPages; p++)
        {
            var text = pageText(p);
            var from = p == startPage ? Math.Min(startOffset, text.Length) : 0;
            var to = p == endPage ? Math.Min(endOffset, text.Length) : text.Length;
            if (to <= from)
                continue;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(text[from..to].Trim('\n'));
        }

        // If the next section starts at the very top of endPage, nothing of it is ours.
        if (endOffset == 0 && endPage > startPage)
            endPage--;
        return (sb.ToString(), endPage);
    }

    /// <summary>Index of the bookmark title within the page text, or -1. Whitespace-flexible,
    /// case-insensitive; long titles are matched on their first words only.</summary>
    private static int FindHeadingOffset(string pageText, string title)
    {
        var tokens = CleanTitle(title).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(8);
        var pattern = string.Join(@"\s+", tokens.Select(Regex.Escape));
        if (pattern.Length == 0)
            return -1;

        var m = Regex.Match(pageText, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        return m.Success ? m.Index : -1;
    }

    private static string CleanPageText(Page page)
    {
        string raw;
        try
        {
            raw = ContentOrderTextExtractor.GetText(page);
        }
        catch
        {
            raw = page.Text;
        }

        raw = HyphenBreak().Replace(raw, "$1$2");
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !IsRunningHeaderOrPageNumber(l))
            .ToArray();
        return string.Join("\n", lines);
    }

    private static bool IsRunningHeaderOrPageNumber(string line)
    {
        if (line.Length <= 2)
            return true;
        if (PageNumberLine().IsMatch(line))
            return true;
        if (line.Contains("Guidelines for Examination in the EPO", StringComparison.OrdinalIgnoreCase) && line.Length < 120)
            return true;
        return false;
    }

    /// <summary>["I. PATENTABILITY", "A. Patentable inventions", ..., "1.1 Whether..."] → "I.A.1.1"</summary>
    private static string? DeriveClbSectionId(IReadOnlyList<string> breadcrumb)
    {
        var roman = breadcrumb.Count > 0 ? ClbRomanPart().Match(breadcrumb[0]) : Match.Empty;
        if (!roman.Success)
            return null;

        var parts = new List<string> { roman.Groups[1].Value };
        if (breadcrumb.Count > 1)
        {
            var letter = ClbLetterPart().Match(breadcrumb[1]);
            if (letter.Success)
                parts.Add(letter.Groups[1].Value);
        }

        // The leaf's dotted number ("4.5.4") already encodes intermediate levels.
        var number = NumberPart().Match(breadcrumb[^1]);
        if (number.Success && breadcrumb.Count > 2)
            parts.Add(number.Groups[1].Value);

        return parts.Count > 1 ? string.Join('.', parts) : null;
    }

    /// <summary>["Part A Guidelines...", "Chapter II – Filing...", "1.1 ..."] → "A-II, 1.1"; General Part → "Gen, 1.1"</summary>
    private static string? DeriveGlSectionId(IReadOnlyList<string> breadcrumb)
    {
        string? partLetter = null;
        string? chapterRoman = null;
        foreach (var level in breadcrumb)
        {
            var pm = GlPart().Match(level);
            if (pm.Success)
                partLetter = pm.Groups[1].Value;
            else if (level.StartsWith("General Part", StringComparison.OrdinalIgnoreCase))
                partLetter = "Gen";

            var cm = GlChapter().Match(level);
            if (cm.Success)
                chapterRoman = cm.Groups[1].Value;
        }

        var number = NumberPart().Match(breadcrumb[^1]);
        if (partLetter is null || !number.Success)
            return null;

        return chapterRoman is null
            ? $"{partLetter}, {number.Groups[1].Value}"
            : $"{partLetter}-{chapterRoman}, {number.Groups[1].Value}";
    }

    private static string CleanTitle(string title) =>
        Whitespace().Replace(title.Trim(), " ");

    private static long InsertSection(SqliteConnection conn, string source, string sectionId, string title,
        string breadcrumb, int pageFrom, int pageTo, string body)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO book_sections(source, section_id, title, breadcrumb, page_from, page_to, part, body)
            VALUES ($src, $sid, $title, $bc, $pf, $pt, 1, $body);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$sid", sectionId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$bc", breadcrumb);
        cmd.Parameters.AddWithValue("$pf", pageFrom);
        cmd.Parameters.AddWithValue("$pt", pageTo);
        cmd.Parameters.AddWithValue("$body", body);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void InsertChunk(SqliteConnection conn, long sectionId, int seq, string text)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chunks(kind, decision_id, book_section_id, lang, seq, text)
            VALUES ('book', NULL, $sid, 'en', $seq, $text);
            """;
        cmd.Parameters.AddWithValue("$sid", sectionId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.ExecuteNonQuery();
    }

    [GeneratedRegex(@"(\w+)-\s*\n\s*(\w+)")]
    private static partial Regex HyphenBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^([IVXLC]+)\.\s")]
    private static partial Regex ClbRomanPart();

    [GeneratedRegex(@"^([A-Z])\.\s")]
    private static partial Regex ClbLetterPart();

    [GeneratedRegex(@"^(\d+(?:\.\d+)*)\.?\s")]
    private static partial Regex NumberPart();

    [GeneratedRegex(@"^Part\s+([A-H])\b")]
    private static partial Regex GlPart();

    [GeneratedRegex(@"^Chapter\s+([IVXLC]+)\b")]
    private static partial Regex GlChapter();

    [GeneratedRegex(@"^[0-9]+$|^[ivxlc]+$|^[IVXLC]+$")]
    private static partial Regex PageNumberLine();
}
