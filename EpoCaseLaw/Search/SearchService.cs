using System.Text.Json;
using EpoCaseLaw.Db;
using EpoCaseLaw.Embeddings;
using EpoCaseLaw.Indexing;
using Microsoft.Data.Sqlite;

namespace EpoCaseLaw.Search;

public sealed class SearchService
{
    public const string IndexMissingMessage =
        "Index not built. Run: dotnet run --project EpoCaseLaw -- index";

    private readonly string dbPath;

    public SearchService(string dbPath, bool ensureSchema = true)
    {
        this.dbPath = dbPath;
        if (ensureSchema)
            DbBootstrap.EnsureSearchSchema(dbPath);
    }

    public bool IsReady => DbBootstrap.IsIndexed(dbPath);

    public JsonDocument SearchDecisions(
        string query,
        string? board = null,
        string? typeCode = null,
        string? dateFrom = null,
        string? dateTo = null,
        string? language = null,
        string? provision = null,
        string? ipcPrefix = null,
        bool onlyWithHeadnote = false,
        string mode = "auto",
        int limit = 10,
        int offset = 0)
    {
        if (!IsReady)
            return Error(IndexMissingMessage);
        if (string.IsNullOrWhiteSpace(query))
            return Error("Search query must not be empty.");
        if (offset < 0 || offset >= HybridSearch.MaxCandidateWindow)
            return Error($"offset must be between 0 and {HybridSearch.MaxCandidateWindow - 1}.");

        using var conn = DbBootstrap.Open(dbPath, readOnly: true);
        var hybrid = new HybridSearch(conn);
        var results = hybrid.SearchDecisions(query, board, typeCode, dateFrom, dateTo, language, provision,
            ipcPrefix, onlyWithHeadnote, mode, limit, offset, out var total, out var totalIsCapped, out var searchMode);

        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            mode = searchMode,
            total,
            total_is_capped = totalIsCapped,
            offset,
            limit,
            results,
        }));
    }

    public JsonDocument GetDecision(string caseNumberOrEcli, string part = "summary", string? lang = null)
    {
        if (!IsReady)
            return Error(IndexMissingMessage);

        using var conn = DbBootstrap.Open(dbPath, readOnly: true);
        var decision = ResolveDecision(conn, caseNumberOrEcli);
        if (decision is null)
            return Error($"Decision not found: {caseNumberOrEcli}");

        var text = LoadDecisionText(conn, decision.Id, lang ?? decision.Language);
        if (text is null)
            return Error($"No text for language {lang ?? decision.Language}");

        object? payload = part.ToLowerInvariant() switch
        {
            "summary" => new
            {
                decision.CaseNumber,
                decision.Ecli,
                decision.DecisionDate,
                decision.Board,
                decision.Language,
                decision.Title,
                decision.Headword,
                keywords = decision.Keywords?.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                text.Headnote,
                text.Catchwords,
                text.Orders,
                decision.Ipc,
                decision.ApplicationNumber,
                available_languages = decision.AvailableLanguages?.Split(',', StringSplitOptions.RemoveEmptyEntries),
            },
            "facts" => new { decision.CaseNumber, text.Lang, text.Facts },
            "reasons" => LongPartPreview(conn, decision.CaseNumber, decision.Id, text, "reasons"),
            "order" => new { decision.CaseNumber, text.Lang, text.Orders },
            "full" => new
            {
                decision.CaseNumber,
                decision.Ecli,
                decision.DecisionDate,
                decision.Board,
                text.Lang,
                decision.Title,
                decision.Headword,
                keywords = decision.Keywords?.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                text.Headnote,
                text.Catchwords,
                note = "Full decision text may exceed MCP response limits. Use get_decision_passages with part='facts', part='reasons', or part='order' for paginated text.",
                text_lengths = new
                {
                    facts = text.Facts.Length,
                    reasons = text.Reasons.Length,
                    orders = text.Orders.Length,
                },
            },
            _ => null,
        };

        if (payload is null)
            return Error($"Unknown part: {part}");

        return JsonDocument.Parse(JsonSerializer.Serialize(payload));
    }

    public JsonDocument GetDecisionPassages(
        string caseNumberOrEcli,
        string part = "reasons",
        string? lang = null,
        int offset = 0,
        int limit = 3,
        int maxChars = 6000)
    {
        if (!IsReady)
            return Error(IndexMissingMessage);
        if (offset < 0)
            return Error("offset must be non-negative.");

        using var conn = DbBootstrap.Open(dbPath, readOnly: true);
        var decision = ResolveDecision(conn, caseNumberOrEcli);
        if (decision is null)
            return Error($"Decision not found: {caseNumberOrEcli}");

        var text = LoadDecisionText(conn, decision.Id, lang ?? decision.Language);
        if (text is null)
            return Error($"No text for language {lang ?? decision.Language}");

        var chunks = LoadDecisionPassageChunks(conn, decision.Id, text, part);
        if (chunks is null)
            return Error($"Unknown part for passages: {part}");

        var selected = TakePassages(chunks, offset, limit, maxChars);
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            case_number = decision.CaseNumber,
            ecli = decision.Ecli,
            lang = text.Lang,
            part = NormalizePassagePart(part),
            offset,
            limit,
            max_chars = maxChars,
            total_chunks = chunks.Count,
            next_offset = offset + selected.Count < chunks.Count ? offset + selected.Count : (int?)null,
            chunks = selected.Select(c => new
            {
                seq = c.Seq,
                chars = c.Text.Length,
                text = c.Text,
            }),
        }));
    }

    public JsonDocument SearchDecisionPassages(
        string caseNumberOrEcli,
        string query,
        string part = "reasons",
        string? lang = null,
        string mode = "auto",
        int limit = 5)
    {
        if (!IsReady)
            return Error(IndexMissingMessage);
        if (string.IsNullOrWhiteSpace(query))
            return Error("Search query must not be empty.");

        using var conn = DbBootstrap.Open(dbPath, readOnly: true);
        var decision = ResolveDecision(conn, caseNumberOrEcli);
        if (decision is null)
            return Error($"Decision not found: {caseNumberOrEcli}");

        var text = LoadDecisionText(conn, decision.Id, lang ?? decision.Language);
        if (text is null)
            return Error($"No text for language {lang ?? decision.Language}");

        var chunks = LoadDecisionPassageChunks(conn, decision.Id, text, part);
        if (chunks is null)
            return Error($"Unknown part for passages: {part}");

        var normalizedMode = mode.ToLowerInvariant();
        IReadOnlyList<PassageHit> lexical = normalizedMode == "vector"
            ? Array.Empty<PassageHit>()
            : SearchPassagesLexical(chunks, query, limit);
        IReadOnlyList<PassageHit> vector = normalizedMode == "lexical"
            ? Array.Empty<PassageHit>()
            : SearchPassagesVector(chunks, query, limit);

        IReadOnlyList<PassageHit> results;
        var searchMode = "lexical";
        if (normalizedMode == "vector")
        {
            results = vector;
            searchMode = vector.Count > 0 ? "vector" : "vector_unavailable";
        }
        else if ((normalizedMode == "hybrid" || normalizedMode == "auto") && vector.Count > 0)
        {
            results = FusePassageHits(lexical, vector, limit);
            searchMode = "hybrid";
        }
        else
        {
            results = lexical;
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            case_number = decision.CaseNumber,
            ecli = decision.Ecli,
            lang = text.Lang,
            part = NormalizePassagePart(part),
            query,
            mode = searchMode,
            total_chunks = chunks.Count,
            results = results.Select(h => new
            {
                seq = h.Chunk.Seq,
                score = h.Score,
                chars = h.Chunk.Text.Length,
                text = h.Chunk.Text,
            }),
        }));
    }

    private object LongPartPreview(SqliteConnection conn, string caseNumber, long decisionId, DecisionTextRow text, string part)
    {
        var chunks = LoadDecisionPassageChunks(conn, decisionId, text, part) ?? [];
        var selected = TakePassages(chunks, 0, 3, 6000);
        return new
        {
            CaseNumber = caseNumber,
            text.Lang,
            Part = NormalizePassagePart(part),
            note = "This part is long and is returned as a bounded preview. Use get_decision_passages for pagination or search_decision_passages for targeted passages.",
            total_chunks = chunks.Count,
            next_offset = selected.Count < chunks.Count ? selected.Count : (int?)null,
            chunks = selected.Select(c => new
            {
                seq = c.Seq,
                chars = c.Text.Length,
                text = c.Text,
            }),
        };
    }

    private static List<PassageChunk>? LoadDecisionPassageChunks(
        SqliteConnection conn,
        long decisionId,
        DecisionTextRow text,
        string part)
    {
        var normalized = NormalizePassagePart(part);
        return normalized switch
        {
            "headnote" => SplitAdHoc(text.Headnote),
            "catchwords" => SplitAdHoc(text.Catchwords),
            "facts" => SplitAdHoc(text.Facts),
            "reasons" => LoadStoredChunks(conn, decisionId, "reasons", text.Lang)
                         ?? SplitAdHoc(text.Reasons),
            "order" => SplitAdHoc(text.Orders),
            "full" => SplitAdHoc(JoinDecisionText(text)),
            _ => null,
        };
    }

    private static string NormalizePassagePart(string part) => part.Trim().ToLowerInvariant() switch
    {
        "fact" or "facts" => "facts",
        "reason" or "reasons" => "reasons",
        "order" or "orders" => "order",
        "headnote" or "headnotes" => "headnote",
        "catchword" or "catchwords" => "catchwords",
        "full" => "full",
        _ => part.Trim().ToLowerInvariant(),
    };

    private static List<PassageChunk>? LoadStoredChunks(SqliteConnection conn, long decisionId, string kind, string lang)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, seq, text, embedding
            FROM chunks
            WHERE decision_id = $id AND kind = $kind AND lang = $lang
            ORDER BY seq;
            """;
        cmd.Parameters.AddWithValue("$id", decisionId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$lang", lang);

        var chunks = new List<PassageChunk>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            chunks.Add(new PassageChunk(
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetInt64(0),
                reader.IsDBNull(3) ? null : (byte[])reader["embedding"]));
        }

        return chunks.Count == 0 ? null : chunks;
    }

    private static List<PassageChunk> SplitAdHoc(string text) =>
        Chunker.SplitText(text)
            .Select((chunk, index) => new PassageChunk(index, chunk, null, null))
            .ToList();

    private static string JoinDecisionText(DecisionTextRow text)
    {
        var sections = new List<string>();
        Add("HEADNOTE", text.Headnote);
        Add("CATCHWORDS", text.Catchwords);
        Add("FACTS AND SUBMISSIONS", text.Facts);
        Add("REASONS", text.Reasons);
        Add("ORDER", text.Orders);
        return string.Join("\n\n", sections);

        void Add(string title, string body)
        {
            if (!string.IsNullOrWhiteSpace(body))
                sections.Add($"{title}\n{body}");
        }
    }

    private static List<PassageChunk> TakePassages(
        IReadOnlyList<PassageChunk> chunks,
        int offset,
        int limit,
        int maxChars)
    {
        var selected = new List<PassageChunk>();
        var chars = 0;
        foreach (var chunk in chunks.Skip(offset))
        {
            if (selected.Count >= limit)
                break;
            if (selected.Count > 0 && chars + chunk.Text.Length > maxChars)
                break;

            selected.Add(chunk);
            chars += chunk.Text.Length;
        }

        return selected;
    }

    private static List<PassageHit> SearchPassagesLexical(
        IReadOnlyList<PassageChunk> chunks,
        string query,
        int limit)
    {
        var terms = TokenizeQuery(query);
        var hits = new List<PassageHit>();
        foreach (var chunk in chunks)
        {
            var score = ScorePassage(chunk.Text, query, terms);
            if (score > 0)
                hits.Add(new PassageHit(chunk, score));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Chunk.Seq)
            .Take(limit)
            .ToList();
    }

    private static List<PassageHit> SearchPassagesVector(
        IReadOnlyList<PassageChunk> chunks,
        string query,
        int limit)
    {
        var embedder = EmbedderCache.TryGet();
        if (embedder is null)
            return [];

        var chunksById = chunks
            .Where(c => c.ChunkId is not null)
            .ToDictionary(c => c.ChunkId!.Value);
        if (chunksById.Count == 0)
            return [];

        var queryVector = embedder.EmbedQuery(query);
        var hits = new List<PassageHit>();
        foreach (var chunk in chunksById.Values)
        {
            if (chunk.Embedding is null)
                continue;
            hits.Add(new PassageHit(chunk, Dot(queryVector, chunk.Embedding)));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Chunk.Seq)
            .Take(limit)
            .ToList();
    }

    private static List<PassageHit> FusePassageHits(
        IReadOnlyList<PassageHit> lexical,
        IReadOnlyList<PassageHit> vector,
        int limit)
    {
        const double rrfK = 60;
        var bySeq = lexical.Concat(vector).Select(h => h.Chunk).DistinctBy(c => c.Seq).ToDictionary(c => c.Seq);
        var scores = new Dictionary<int, double>();
        Add(lexical);
        Add(vector);

        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(limit)
            .Select(kv => new PassageHit(bySeq[kv.Key], kv.Value))
            .ToList();

        void Add(IReadOnlyList<PassageHit> hits)
        {
            for (var i = 0; i < hits.Count; i++)
            {
                var seq = hits[i].Chunk.Seq;
                scores[seq] = scores.GetValueOrDefault(seq) + 1.0 / (rrfK + i + 1);
            }
        }
    }

    private static List<string> TokenizeQuery(string query)
    {
        var terms = new List<string>();
        var current = new List<char>();
        foreach (var c in query.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Add(c);
                continue;
            }

            Flush();
        }

        Flush();
        return terms.Distinct().ToList();

        void Flush()
        {
            if (current.Count > 1)
                terms.Add(new string(current.ToArray()));
            current.Clear();
        }
    }

    private static double ScorePassage(string text, string query, IReadOnlyList<string> terms)
    {
        var score = text.Contains(query, StringComparison.OrdinalIgnoreCase) ? 10.0 : 0.0;
        foreach (var term in terms)
            score += CountOccurrences(text, term);
        return score;
    }

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            var found = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                break;
            count++;
            index = found + term.Length;
        }

        return count;
    }

    private static double Dot(float[] queryVector, byte[] passageEmbedding)
    {
        var passageVector = new float[passageEmbedding.Length / sizeof(float)];
        Buffer.BlockCopy(passageEmbedding, 0, passageVector, 0, passageEmbedding.Length);

        var len = Math.Min(queryVector.Length, passageVector.Length);
        var sum = 0.0;
        for (var i = 0; i < len; i++)
            sum += queryVector[i] * passageVector[i];
        return sum;
    }

    public JsonDocument GetDecisionCitations(string caseNumber, string direction = "cites")
    {
        if (!IsReady)
            return Error(IndexMissingMessage);

        using var conn = DbBootstrap.Open(dbPath, readOnly: true);
        var decision = ResolveDecision(conn, caseNumber);
        if (decision is null)
            return Error($"Decision not found: {caseNumber}");

        if (direction.Equals("cited_by", StringComparison.OrdinalIgnoreCase))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT d.case_number, d.ecli, d.decision_date, d.board, d.headword, d.keywords
                FROM decision_citations c
                JOIN decisions d ON d.id = c.citing_id
                WHERE c.cited_case_number = $cn
                ORDER BY d.decision_date DESC
                LIMIT 50;
                """;
            cmd.Parameters.AddWithValue("$cn", decision.CaseNumber);
            var citedBy = ReadCitationRows(cmd);
            return JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                case_number = decision.CaseNumber,
                direction = "cited_by",
                results = citedBy,
            }));
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT cited_case_number FROM decision_citations WHERE citing_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", decision.Id);
            var cited = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var cn = reader.GetString(0);
                var target = ResolveDecision(conn, cn);
                cited.Add(target is null
                    ? new { case_number = cn, in_corpus = false }
                    : new
                    {
                        case_number = target.CaseNumber,
                        in_corpus = true,
                        target.Ecli,
                        target.DecisionDate,
                        target.Board,
                        target.Headword,
                    });
            }

            return JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                case_number = decision.CaseNumber,
                direction = "cites",
                results = cited,
            }));
        }
    }

    public JsonDocument SearchLegalTexts(string query, string source = "both", string mode = "auto", int limit = 10)
    {
        if (!IsReady)
            return Error(IndexMissingMessage);
        if (string.IsNullOrWhiteSpace(query))
            return Error("Search query must not be empty.");

        using var conn = DbBootstrap.Open(dbPath, readOnly: true);
        var hybrid = new HybridSearch(conn);
        var results = hybrid.SearchLegalTexts(query, source, mode, limit, out var searchMode);

        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            mode = searchMode,
            results,
        }));
    }

    public JsonDocument GetLegalTextSection(string source, string sectionId)
    {
        if (!IsReady)
            return Error(IndexMissingMessage);

        var src = NormalizeBookSource(source);
        using var conn = DbBootstrap.Open(dbPath, readOnly: true);

        // Agents copy section ids from search results, but tolerate trimming/casing
        // slips and unambiguous prefixes ("G-VII, 5.2" for "G-VII, 5.2 (2)").
        var resolved = ResolveSectionId(conn, src, sectionId.Trim());
        if (resolved is null)
            return Error($"Section not found: {source} {sectionId}");
        sectionId = resolved;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, source, section_id, title, breadcrumb, page_from, page_to, part, body
            FROM book_sections
            WHERE source = $src AND section_id = $sid
            ORDER BY part;
            """;
        cmd.Parameters.AddWithValue("$src", src);
        cmd.Parameters.AddWithValue("$sid", sectionId);

        var parts = new List<object>();
        string? breadcrumb = null;
        string? title = null;
        int? pageFrom = null, pageTo = null;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                breadcrumb ??= reader.IsDBNull(4) ? null : reader.GetString(4);
                title ??= reader.IsDBNull(3) ? null : reader.GetString(3);
                pageFrom ??= reader.GetInt32(5);
                pageTo = reader.GetInt32(6);
                parts.Add(new { part = reader.GetInt32(7), body = reader.GetString(8) });
            }
        }

        if (parts.Count == 0)
            return Error($"Section not found: {source} {sectionId}");

        var neighbours = FindNeighbourSections(conn, src, sectionId);
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            source = src,
            section_id = sectionId,
            title,
            breadcrumb,
            pages = new { from = pageFrom, to = pageTo },
            parts,
            neighbours,
        }));
    }

    private static string? ResolveSectionId(SqliteConnection conn, string src, string sectionId)
    {
        string? QueryOne(string where, bool distinctMustBeSingle = false)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = distinctMustBeSingle
                ? $"SELECT DISTINCT section_id FROM book_sections WHERE source = $src AND {where} LIMIT 2"
                : $"SELECT section_id FROM book_sections WHERE source = $src AND {where} LIMIT 1";
            cmd.Parameters.AddWithValue("$src", src);
            if (where.Contains("$sid_prefix", StringComparison.Ordinal))
                cmd.Parameters.AddWithValue("$sid_prefix", EscapeLike(sectionId) + "%");
            else
                cmd.Parameters.AddWithValue("$sid", sectionId);

            var ids = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetString(0));
            return distinctMustBeSingle && ids.Count != 1 ? null : ids.FirstOrDefault();
        }

        return QueryOne("section_id = $sid")
            ?? QueryOne("section_id = $sid COLLATE NOCASE")
            ?? QueryOne("section_id LIKE $sid_prefix ESCAPE '\\'", distinctMustBeSingle: true);
    }

    private static string EscapeLike(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    public JsonDocument LookupProvision(string provision)
    {
        if (!IsReady)
            return Error(IndexMissingMessage);

        var normalized = LegalRefNormalizer.NormalizeQueryProvision(provision);
        using var conn = DbBootstrap.Open(dbPath, readOnly: true);

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(DISTINCT decision_id) FROM decision_provisions WHERE provision = $p";
        countCmd.Parameters.AddWithValue("$p", normalized);
        var count = Convert.ToInt64(countCmd.ExecuteScalar());

        using var recentCmd = conn.CreateCommand();
        recentCmd.CommandText = """
            SELECT DISTINCT d.case_number, d.ecli, d.decision_date, d.board, d.headword, d.distribution
            FROM decision_provisions p
            JOIN decisions d ON d.id = p.decision_id
            WHERE p.provision = $p
            ORDER BY d.decision_date DESC
            LIMIT 10;
            """;
        recentCmd.Parameters.AddWithValue("$p", normalized);
        var recent = ReadProvisionRows(recentCmd);

        using var citedCmd = conn.CreateCommand();
        citedCmd.CommandText = """
            SELECT d.case_number, d.ecli, d.decision_date, d.board, d.headword, d.distribution, COUNT(*) AS cnt
            FROM decision_provisions p
            JOIN decisions d ON d.id = p.decision_id
            WHERE p.provision = $p
            GROUP BY d.id
            ORDER BY (CASE WHEN d.has_headnote = 1 THEN 0 ELSE 1 END),
                     (CASE WHEN d.distribution IN ('A','B') THEN 0 ELSE 1 END),
                     cnt DESC
            LIMIT 5;
            """;
        citedCmd.Parameters.AddWithValue("$p", normalized);
        var topCited = ReadProvisionRows(citedCmd);

        // Match the provision as a phrase in book/guidelines prose ("Art. 123(2)" tokenizes
        // to [art, 123, 2], which also matches "Art 123(2)"); rules are spelt "Rule N" there.
        var phrases = LegalRefNormalizer.ProseVariants(normalized)
            .Select(v => $"\"{FtsHelper.Escape(v)}\"");
        using var bookCmd = conn.CreateCommand();
        bookCmd.CommandText = """
            SELECT bs.source, bs.section_id, bs.title, bs.breadcrumb
            FROM book_fts
            JOIN book_sections bs ON bs.id = book_fts.rowid
            WHERE book_fts MATCH $match
            ORDER BY bm25(book_fts)
            LIMIT 5;
            """;
        bookCmd.Parameters.AddWithValue("$match", string.Join(" OR ", phrases));
        var bookHits = new List<object>();
        using (var reader = bookCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                bookHits.Add(new
                {
                    source = reader.GetString(0),
                    section_id = reader.GetString(1),
                    title = reader.IsDBNull(2) ? null : reader.GetString(2),
                    breadcrumb = reader.IsDBNull(3) ? null : reader.GetString(3),
                });
            }
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            provision = normalized,
            decision_count = count,
            recent_decisions = recent,
            top_cited_decisions = topCited,
            book_sections = bookHits,
        }));
    }

    private static string NormalizeBookSource(string source) => source.ToLowerInvariant() switch
    {
        "case_law_book" or "clb" => "clb",
        "guidelines" or "gl" => "gl",
        _ => source,
    };

    private static object FindNeighbourSections(SqliteConnection conn, string source, string sectionId)
    {
        // Sections are inserted in page order, so id order is document order.
        return new
        {
            previous = Adjacent("bs.id < (SELECT id FROM book_sections WHERE source = $src AND section_id = $sid LIMIT 1)", "bs.id DESC"),
            next = Adjacent("bs.id > (SELECT MAX(id) FROM book_sections WHERE source = $src AND section_id = $sid)", "bs.id"),
        };

        List<object> Adjacent(string where, string order)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT bs.section_id, bs.title FROM book_sections bs
                WHERE bs.source = $src AND bs.section_id != $sid AND {where}
                ORDER BY {order}
                LIMIT 3;
                """;
            cmd.Parameters.AddWithValue("$src", source);
            cmd.Parameters.AddWithValue("$sid", sectionId);
            var list = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(new { section_id = reader.GetString(0), title = reader.IsDBNull(1) ? null : reader.GetString(1) });
            if (order.EndsWith("DESC", StringComparison.Ordinal))
                list.Reverse();
            return list;
        }
    }

    private static List<object> ReadCitationRows(SqliteCommand cmd)
    {
        var list = new List<object>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new
            {
                case_number = reader.GetString(0),
                ecli = reader.IsDBNull(1) ? null : reader.GetString(1),
                date = reader.IsDBNull(2) ? null : reader.GetString(2),
                board = reader.IsDBNull(3) ? null : reader.GetString(3),
                headword = reader.IsDBNull(4) ? null : reader.GetString(4),
                keywords = reader.IsDBNull(5) ? null : reader.GetString(5)?.Split('\n').Take(3),
            });
        }

        return list;
    }

    private static List<object> ReadProvisionRows(SqliteCommand cmd)
    {
        var list = new List<object>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new
            {
                case_number = reader.GetString(0),
                ecli = reader.IsDBNull(1) ? null : reader.GetString(1),
                date = reader.IsDBNull(2) ? null : reader.GetString(2),
                board = reader.IsDBNull(3) ? null : reader.GetString(3),
                headword = reader.IsDBNull(4) ? null : reader.GetString(4),
                distribution = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }

        return list;
    }

    private DecisionRow? ResolveDecision(SqliteConnection conn, string key)
    {
        var canonical = CaseNumber.TryParseCanonical(key);
        using var cmd = conn.CreateCommand();
        if (canonical is not null)
        {
            cmd.CommandText = "SELECT * FROM decisions WHERE case_number = $k LIMIT 1";
            cmd.Parameters.AddWithValue("$k", canonical);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM decisions WHERE ecli = $k OR case_number = $k LIMIT 1";
            cmd.Parameters.AddWithValue("$k", key.Trim());
        }

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDecision(reader) : null;
    }

    private static DecisionTextRow? LoadDecisionText(SqliteConnection conn, long decisionId, string lang)
    {
        // Requested language first, then the original, then anything (some decisions
        // exist only as a translation, so is_original may match no row).
        return Query("decision_id = $id AND lang = $lang")
            ?? Query("decision_id = $id AND is_original = 1")
            ?? Query("decision_id = $id");

        DecisionTextRow? Query(string where)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT lang, headnote, catchwords, facts, reasons, orders
                FROM decision_texts
                WHERE {where}
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", decisionId);
            if (where.Contains("$lang"))
                cmd.Parameters.AddWithValue("$lang", lang);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;
            return new DecisionTextRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5));
        }
    }

    private static DecisionRow ReadDecision(SqliteDataReader reader) => new(
        reader.GetInt64(reader.GetOrdinal("id")),
        reader.GetString(reader.GetOrdinal("case_number")),
        reader.IsDBNull(reader.GetOrdinal("ecli")) ? null : reader.GetString(reader.GetOrdinal("ecli")),
        reader.IsDBNull(reader.GetOrdinal("decision_date")) ? null : reader.GetString(reader.GetOrdinal("decision_date")),
        reader.IsDBNull(reader.GetOrdinal("board")) ? null : reader.GetString(reader.GetOrdinal("board")),
        reader.GetString(reader.GetOrdinal("language")),
        reader.IsDBNull(reader.GetOrdinal("title")) ? null : reader.GetString(reader.GetOrdinal("title")),
        reader.IsDBNull(reader.GetOrdinal("headword")) ? null : reader.GetString(reader.GetOrdinal("headword")),
        reader.IsDBNull(reader.GetOrdinal("keywords")) ? null : reader.GetString(reader.GetOrdinal("keywords")),
        reader.IsDBNull(reader.GetOrdinal("ipc")) ? null : reader.GetString(reader.GetOrdinal("ipc")),
        reader.IsDBNull(reader.GetOrdinal("application_number")) ? null : reader.GetString(reader.GetOrdinal("application_number")),
        reader.IsDBNull(reader.GetOrdinal("available_languages")) ? null : reader.GetString(reader.GetOrdinal("available_languages")));

    private static JsonDocument Error(string message) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { error = message }));

    private sealed record DecisionRow(
        long Id, string CaseNumber, string? Ecli, string? DecisionDate, string? Board,
        string Language, string? Title, string? Headword, string? Keywords,
        string? Ipc, string? ApplicationNumber, string? AvailableLanguages);

    private sealed record DecisionTextRow(
        string Lang, string Headnote, string Catchwords, string Facts, string Reasons, string Orders);

    private sealed record PassageChunk(int Seq, string Text, long? ChunkId, byte[]? Embedding);

    private sealed record PassageHit(PassageChunk Chunk, double Score);
}
