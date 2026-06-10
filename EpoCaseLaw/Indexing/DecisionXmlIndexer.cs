using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using EpoCaseLaw.Db;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace EpoCaseLaw.Indexing;

public sealed class DecisionXmlIndexer(ILogger logger)
{
    private const int BatchSize = 500;
    private int _docsInBatch;
    private int _newDecisions;
    private SqliteTransaction? _tx;

    /// <summary>
    /// Incremental mode keeps the existing database (and its embeddings): new ECLIs and new
    /// language versions are added, existing rows are left untouched. Corrections to already
    /// indexed decision texts are NOT picked up — do a full rebuild for those.
    /// </summary>
    public async Task IndexAsync(string xmlPath, string dbPath, bool incremental = false, CancellationToken ct = default)
    {
        if (!incremental)
        {
            foreach (var stale in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                if (File.Exists(stale))
                    File.Delete(stale);
            }
        }

        // Incremental runs use WAL instead of the unsafe bulk pragmas: the database
        // already holds hours of embedding work that a crash must not corrupt.
        await using var conn = DbBootstrap.Open(dbPath, bulkLoad: !incremental);
        DbBootstrap.CreateSchema(conn);

        var docCount = 0;
        BeginBatch(conn);

        using var reader = XmlReader.Create(xmlPath, new XmlReaderSettings { Async = true, IgnoreWhitespace = true });
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "ep-appeal-decision")
                continue;

            using var subtree = reader.ReadSubtree();
            var element = XElement.Load(subtree);
            docCount++;
            ProcessDocument(conn, element);

            if (_docsInBatch >= BatchSize)
                CommitBatch(conn);

            if (docCount % 1000 == 0)
                logger.LogInformation("Parsed {Count} XML documents...", docCount);
        }

        FinalizeBatch(conn);
        logger.LogInformation("Rebuilding decision FTS index from external content...");
        DbBootstrap.RebuildDecisionFts(conn);

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            pragma.ExecuteNonQuery();
        }

        DbBootstrap.SetMeta(conn, "documents_parsed", docCount.ToString(CultureInfo.InvariantCulture));
        logger.LogInformation("Finished XML indexing: {Docs} documents parsed, {New} new decisions", docCount, _newDecisions);
    }

    private void BeginBatch(SqliteConnection conn)
    {
        _tx = conn.BeginTransaction();
        _docsInBatch = 0;
    }

    private void CommitBatch(SqliteConnection conn)
    {
        _tx?.Commit();
        BeginBatch(conn);
    }

    private void FinalizeBatch(SqliteConnection conn)
    {
        _tx?.Commit();
        _tx = null;
        _docsInBatch = 0;
    }

    private void ProcessDocument(SqliteConnection conn, XElement doc)
    {
        _docsInBatch++;
        var procedureLang = doc.Attribute("procedure-lang")?.Value ?? doc.Attribute("lang")?.Value ?? "en";
        var lang = doc.Attribute("lang")?.Value ?? procedureLang;
        var bib = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-appeal-bib-data");
        if (bib is null)
            return;

        var ecli = bib.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-ecli")?.Value?.Trim();
        if (string.IsNullOrEmpty(ecli))
            return;

        var existingId = FindDecisionIdByEcli(conn, ecli);
        if (existingId is null)
        {
            existingId = InsertNewDecision(conn, doc, bib, ecli, procedureLang);
            InsertProvisionsAndCitations(conn, existingId.Value, doc, bib);
            _newDecisions++;
        }
        else
        {
            AppendLanguage(conn, existingId.Value, lang);
        }

        var isOriginal = string.Equals(lang, procedureLang, StringComparison.OrdinalIgnoreCase);
        var headword = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-headword")?.Value?.Trim();
        var keywords = isOriginal ? CollectKeywords(doc) : null;

        var headnote = ExtractBody(doc, "ep-headnote");
        var catchwords = ExtractBody(doc, "ep-catchword");
        var facts = ExtractBody(doc, "ep-summary-of-facts");
        var reasons = ExtractBody(doc, "ep-reasons-for-decision");
        var orders = ExtractBody(doc, "ep-appeal-order");

        if (isOriginal && existingId is not null)
            UpdateOriginalMetadata(conn, existingId.Value, headword, keywords, headnote.Length > 0);

        if (DecisionTextExists(conn, existingId!.Value, lang))
            return;

        var textId = InsertDecisionText(conn, existingId.Value, lang, isOriginal ? 1 : 0,
            headnote, catchwords, facts, reasons, orders);
        InsertFts(conn, textId);

        if (isOriginal)
        {
            // Essence chunks are usually short, but a long headnote can push one past
            // the embedding window, so they go through the chunker too.
            var essence = Chunker.BuildEssence(headword, keywords, catchwords, headnote);
            var essenceChunks = Chunker.SplitText(essence);
            for (var i = 0; i < essenceChunks.Count; i++)
                InsertChunk(conn, "essence", existingId.Value, null, lang, i, essenceChunks[i]);

            var reasonChunks = Chunker.SplitText(reasons);
            for (var i = 0; i < reasonChunks.Count; i++)
                InsertChunk(conn, "reasons", existingId.Value, null, lang, i, reasonChunks[i]);
        }
    }

    private static bool DecisionTextExists(SqliteConnection conn, long decisionId, string lang)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM decision_texts WHERE decision_id = $id AND lang = $lang LIMIT 1";
        cmd.Parameters.AddWithValue("$id", decisionId);
        cmd.Parameters.AddWithValue("$lang", lang);
        return cmd.ExecuteScalar() is not null;
    }

    private static long? FindDecisionIdByEcli(SqliteConnection conn, string ecli)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM decisions WHERE ecli = $ecli LIMIT 1";
        cmd.Parameters.AddWithValue("$ecli", ecli);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static long InsertNewDecision(SqliteConnection conn, XElement doc, XElement bib, string ecli, string procedureLang)
    {
        var code = bib.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-case-num")?.Attribute("code")?.Value ?? "T";
        var appealNum = bib.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-appeal-num")?.Value?.Trim() ?? "0000";
        var year = int.TryParse(bib.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-year")?.Value, out var y) ? y : 0;
        var caseNumber = CaseNumber.Format(code, appealNum, year);
        var board = bib.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-board-of-appeal-code")?.Value?.Trim();
        var dateRaw = bib.Descendants().FirstOrDefault(e => e.Name.LocalName == "date")?.Value?.Trim();
        var decisionDate = ParseDate(dateRaw);
        var distribution = bib.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-distribution-code")?.Value?.Trim();
        var title = bib.Descendants().FirstOrDefault(e => e.Name.LocalName == "invention-title")?.Value?.Trim();
        var appNum = bib.Descendants().FirstOrDefault(e => e.Name.LocalName == "application-reference")
            ?.Descendants().FirstOrDefault(e => e.Name.LocalName == "doc-number")?.Value?.Trim();
        var ipc = string.Join(' ', bib.Descendants()
            .Where(e => e.Name.LocalName == "classification-ipcr")
            .Select(FormatIpc)
            .Where(s => !string.IsNullOrEmpty(s)));
        var headword = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-headword")?.Value?.Trim();
        var keywords = CollectKeywords(doc);
        var oj = FormatOj(bib);
        var hasHeadnote = doc.Descendants().Any(e => e.Name.LocalName == "ep-headnote" && e.Value.Trim().Length > 0);
        var lang = doc.Attribute("lang")?.Value ?? procedureLang;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO decisions(case_number, code, appeal_num, year, ecli, board, decision_date, language,
              distribution, title, application_number, ipc, headword, keywords, oj_reference, has_headnote, available_languages)
            VALUES ($cn, $code, $an, $year, $ecli, $board, $date, $lang, $dist, $title, $app, $ipc, $hw, $kw, $oj, $hn, $langs);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$cn", caseNumber);
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$an", appealNum);
        cmd.Parameters.AddWithValue("$year", year);
        cmd.Parameters.AddWithValue("$ecli", ecli);
        cmd.Parameters.AddWithValue("$board", (object?)board ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$date", (object?)decisionDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lang", procedureLang);
        cmd.Parameters.AddWithValue("$dist", (object?)distribution ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$app", (object?)appNum ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ipc", ipc);
        cmd.Parameters.AddWithValue("$hw", (object?)headword ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kw", keywords);
        cmd.Parameters.AddWithValue("$oj", (object?)oj ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hn", hasHeadnote ? 1 : 0);
        cmd.Parameters.AddWithValue("$langs", lang);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void AppendLanguage(SqliteConnection conn, long decisionId, string lang)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE decisions SET available_languages =
              CASE WHEN available_languages IS NULL OR available_languages = '' THEN $lang
                   WHEN instr(',' || available_languages || ',', ',' || $lang || ',') > 0 THEN available_languages
                   ELSE available_languages || ',' || $lang END
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$lang", lang);
        cmd.Parameters.AddWithValue("$id", decisionId);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateOriginalMetadata(SqliteConnection conn, long decisionId, string? headword, string? keywords, bool hasHeadnote)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE decisions SET headword = COALESCE($hw, headword),
              keywords = CASE WHEN $kw = '' THEN keywords ELSE $kw END,
              has_headnote = CASE WHEN $hn = 1 THEN 1 ELSE has_headnote END
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$hw", (object?)headword ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kw", keywords ?? "");
        cmd.Parameters.AddWithValue("$hn", hasHeadnote ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", decisionId);
        cmd.ExecuteNonQuery();
    }

    private static void InsertProvisionsAndCitations(SqliteConnection conn, long decisionId, XElement doc, XElement bib)
    {
        foreach (var citation in bib.Descendants().Where(e => e.Name.LocalName == "ep-cited-decision")
                     .Concat(doc.Descendants().Where(e => e.Name.LocalName == "ep-cited-decision")))
        {
            var cCode = citation.Attribute("code")?.Value
                        ?? citation.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-case-num")?.Attribute("code")?.Value;
            var cNum = citation.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-appeal-num")?.Value;
            var cYear = citation.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-year")?.Value;
            if (cCode is null || cNum is null || cYear is null || !int.TryParse(cYear, out var cy))
                continue;
            InsertCitation(conn, decisionId, CaseNumber.Format(cCode, cNum, cy));
        }

        foreach (var legal in doc.Descendants().Where(e => e.Name.LocalName == "ep-legal-ref-presentation"))
        {
            var (norm, raw) = LegalRefNormalizer.Normalize(legal.Value);
            if (!string.IsNullOrWhiteSpace(norm))
                InsertProvision(conn, decisionId, norm, raw);
        }
    }

    private static string CollectKeywords(XElement doc) =>
        string.Join('\n', doc.Descendants()
            .Where(e => e.Name.LocalName == "keyword")
            .Select(e => e.Value.Trim())
            .Where(v => !string.IsNullOrEmpty(v)));

    private static long InsertDecisionText(SqliteConnection conn, long decisionId, string lang, int isOriginal,
        string headnote, string catchwords, string facts, string reasons, string orders)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO decision_texts(decision_id, lang, is_original, headnote, catchwords, facts, reasons, orders)
            VALUES ($did, $lang, $orig, $hn, $cw, $facts, $reasons, $orders);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$did", decisionId);
        cmd.Parameters.AddWithValue("$lang", lang);
        cmd.Parameters.AddWithValue("$orig", isOriginal);
        cmd.Parameters.AddWithValue("$hn", headnote);
        cmd.Parameters.AddWithValue("$cw", catchwords);
        cmd.Parameters.AddWithValue("$facts", facts);
        cmd.Parameters.AddWithValue("$reasons", reasons);
        cmd.Parameters.AddWithValue("$orders", orders);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void InsertFts(SqliteConnection conn, long rowid)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO decisions_fts(rowid, case_number, headword, keywords, catchwords, headnote, title, facts, reasons, orders)
            SELECT id, case_number, headword, keywords, catchwords, headnote, title, facts, reasons, orders
            FROM decisions_fts_src
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", rowid);
        cmd.ExecuteNonQuery();
    }

    private static void InsertChunk(SqliteConnection conn, string kind, long decisionId, long? bookSectionId, string lang, int seq, string text)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chunks(kind, decision_id, book_section_id, lang, seq, text)
            VALUES ($kind, $did, $bsid, $lang, $seq, $text);
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$did", decisionId);
        cmd.Parameters.AddWithValue("$bsid", (object?)bookSectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lang", lang);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.ExecuteNonQuery();
    }

    private static void InsertCitation(SqliteConnection conn, long citingId, string citedCaseNumber)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO decision_citations(citing_id, cited_case_number) VALUES ($id, $cited)";
        cmd.Parameters.AddWithValue("$id", citingId);
        cmd.Parameters.AddWithValue("$cited", citedCaseNumber);
        cmd.ExecuteNonQuery();
    }

    private static void InsertProvision(SqliteConnection conn, long decisionId, string provision, string raw)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO decision_provisions(decision_id, provision, raw) VALUES ($id, $p, $r)";
        cmd.Parameters.AddWithValue("$id", decisionId);
        cmd.Parameters.AddWithValue("$p", provision);
        cmd.Parameters.AddWithValue("$r", raw);
        cmd.ExecuteNonQuery();
    }

    private static string ExtractBody(XElement root, string localName)
    {
        var section = root.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        if (section is null)
            return "";

        var sb = new StringBuilder();
        foreach (var p in section.Descendants().Where(e => e.Name.LocalName == "p"))
        {
            var text = string.Concat(p.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
            if (text.Length > 0)
            {
                if (sb.Length > 0)
                    sb.Append("\n\n");
                sb.Append(text);
            }
        }

        if (sb.Length == 0)
            sb.Append(section.Value.Trim());

        return sb.ToString();
    }

    private static string? ParseDate(string? yyyymmdd)
    {
        if (string.IsNullOrWhiteSpace(yyyymmdd) || yyyymmdd.Length != 8)
            return null;
        if (!DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return null;
        return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string FormatIpc(XElement ipc)
    {
        string Get(string name) => ipc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";
        var section = Get("section");
        var cls = Get("class");
        var subclass = Get("subclass");
        var main = Get("main-group");
        var sub = Get("subgroup");
        if (string.IsNullOrEmpty(section))
            return "";
        return $"{section}{cls}{subclass}{main}/{sub}";
    }

    private static string? FormatOj(XElement bib)
    {
        var oj = bib.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-official-journal");
        if (oj is null)
            return null;
        var num = oj.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-official-journal-num")?.Value;
        var year = oj.Descendants().FirstOrDefault(e => e.Name.LocalName == "ep-year")?.Value;
        return num is null ? null : $"OJ EPO {year} {num}";
    }
}
