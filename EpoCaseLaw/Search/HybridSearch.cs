using EpoCaseLaw.Db;
using EpoCaseLaw.Embeddings;
using Microsoft.Data.Sqlite;

namespace EpoCaseLaw.Search;

public sealed class HybridSearch(SqliteConnection conn)
{
    private const int CandidateLimit = 100;
    public const int MaxCandidateWindow = 1000;
    private const int CountLimit = 10000;
    private const int RrfK = 60;

    // Whether any chunk has an embedding only flips false→true once (during the embed
    // run), so a positive result is cached forever and a negative one briefly.
    private static volatile bool _hasEmbeddings;
    private static DateTime _nextEmbeddingCheck = DateTime.MinValue;

    private bool HasAnyEmbeddings()
    {
        if (_hasEmbeddings)
            return true;
        if (DateTime.UtcNow < _nextEmbeddingCheck)
            return false;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM chunks WHERE embedding IS NOT NULL)";
        _hasEmbeddings = Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        if (!_hasEmbeddings)
            _nextEmbeddingCheck = DateTime.UtcNow.AddSeconds(60);
        return _hasEmbeddings;
    }

    public IReadOnlyList<object> SearchDecisions(
        string query,
        string? board,
        string? typeCode,
        string? dateFrom,
        string? dateTo,
        string? language,
        string? provision,
        string? ipcPrefix,
        bool onlyWithHeadnote,
        string mode,
        int limit,
        int offset,
        out long total,
        out bool totalIsCapped,
        out string searchMode)
    {
        // The candidate pool must reach past the requested page, otherwise offsets
        // beyond the pool return nothing despite a large total.
        var pool = Math.Clamp(offset + limit, CandidateLimit, MaxCandidateWindow);
        var lexical = SearchDecisionsLexical(query, board, typeCode, dateFrom, dateTo, language, provision,
            ipcPrefix, onlyWithHeadnote, pool, out total, out totalIsCapped);

        if (ShouldUseHybrid(mode))
        {
            try
            {
                var vector = SearchDecisionsVector(query, board, typeCode, dateFrom, dateTo, language, provision,
                    ipcPrefix, onlyWithHeadnote, pool);
                var fused = ReciprocalRankFusion(
                    lexical.Select(x => ((long)x.decision_id, x.score)).ToList(),
                    vector.Select(x => ((long)x.decision_id, x.score)).ToList());

                // The lexical match count, except the vector leg can surface decisions
                // the lexical query missed entirely.
                total = Math.Max(total, fused.Count);
                searchMode = "hybrid";
                return fused.Skip(offset).Take(limit)
                    .Select(id => lexical.FirstOrDefault(l => l.decision_id == id)
                                ?? vector.First(v => v.decision_id == id))
                    .Select(ToDecisionResult)
                    .Cast<object>()
                    .ToList();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Hybrid decision search failed; falling back to lexical: {ex.GetType().Name}: {ex.Message}");
            }
        }

        searchMode = "lexical";
        return lexical.Skip(offset).Take(limit).Select(ToDecisionResult).Cast<object>().ToList();
    }

    public IReadOnlyList<object> SearchLegalTexts(string query, string source, string mode, int limit, out string searchMode)
    {
        var srcFilter = source.ToLowerInvariant() switch
        {
            "case_law_book" or "clb" => "clb",
            "guidelines" or "gl" => "gl",
            _ => null,
        };

        var lexical = SearchBookLexical(query, srcFilter, CandidateLimit);
        if (ShouldUseHybrid(mode))
        {
            try
            {
                var vector = SearchBookVector(query, srcFilter, CandidateLimit);
                var fused = ReciprocalRankFusion(
                    lexical.Select(x => ((long)x.section_db_id, x.score)).ToList(),
                    vector.Select(x => ((long)x.section_db_id, x.score)).ToList());

                searchMode = "hybrid";
                return fused.Take(limit)
                    .Select(id => lexical.FirstOrDefault(l => l.section_db_id == id)
                                ?? vector.First(v => v.section_db_id == id))
                    .Select(ToBookResult)
                    .Cast<object>()
                    .ToList();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Hybrid legal-text search failed; falling back to lexical: {ex.GetType().Name}: {ex.Message}");
            }
        }

        searchMode = "lexical";
        return lexical.Take(limit).Select(ToBookResult).Cast<object>().ToList();
    }

    private bool ShouldUseHybrid(string mode)
    {
        if (mode.Equals("lexical", StringComparison.OrdinalIgnoreCase))
            return false;
        if (EmbedderCache.TryGet() is null || !HasAnyEmbeddings())
            return false;
        return DbBootstrap.VecExtensionLoaded(conn);
    }

    private List<DecisionHit> SearchDecisionsLexical(string query, string? board, string? typeCode,
        string? dateFrom, string? dateTo, string? language, string? provision, string? ipcPrefix,
        bool onlyWithHeadnote, int limit, out long total, out bool totalIsCapped)
    {
        (total, totalIsCapped) = CountDecisionsLexical(query, board, typeCode, dateFrom, dateTo, language, provision, ipcPrefix, onlyWithHeadnote);
        var match = FtsHelper.BuildMatchQuery(query);
        if (string.IsNullOrEmpty(match))
            return [];

        using var cmd = conn.CreateCommand();
        // A decision has one FTS row per language text; bm25()/snippet() cannot be used
        // under GROUP BY (FTS5 aux functions need direct query context), so over-fetch
        // and keep each decision's best-ranked row in C#.
        cmd.CommandText = $"""
            SELECT d.id, d.case_number, d.ecli, d.decision_date, d.board, dt.lang, d.headword, d.keywords,
                   snippet(decisions_fts, -1, '[[', ']]', '...', 12) AS snip,
                   bm25(decisions_fts, 10.0, 8.0, 8.0, 8.0, 8.0, 4.0, 1.0, 2.0, 1.0) AS score
            FROM decisions_fts
            JOIN decision_texts dt ON dt.id = decisions_fts.rowid
            JOIN decisions d ON d.id = dt.decision_id
            WHERE decisions_fts MATCH $match
              {FilterSql(board, typeCode, dateFrom, dateTo, language, provision, ipcPrefix, onlyWithHeadnote)}
            ORDER BY score
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$match", match);
        AddFilterParams(cmd, board, typeCode, dateFrom, dateTo, language, provision, ipcPrefix, onlyWithHeadnote);
        cmd.Parameters.AddWithValue("$limit", limit * 3);
        return ReadDecisionHits(cmd)
            .GroupBy(h => h.decision_id)
            .Select(g => g.OrderBy(h => h.score).First())
            .OrderBy(h => h.score)
            .Take(limit)
            .ToList();
    }

    private (long Count, bool IsCapped) CountDecisionsLexical(string query, string? board, string? typeCode,
        string? dateFrom, string? dateTo, string? language, string? provision, string? ipcPrefix, bool onlyWithHeadnote)
    {
        var match = FtsHelper.BuildMatchQuery(query);
        if (string.IsNullOrEmpty(match))
            return (0, false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM (
                SELECT DISTINCT d.id
                FROM decisions_fts
                JOIN decision_texts dt ON dt.id = decisions_fts.rowid
                JOIN decisions d ON d.id = dt.decision_id
                WHERE decisions_fts MATCH $match
                  {FilterSql(board, typeCode, dateFrom, dateTo, language, provision, ipcPrefix, onlyWithHeadnote)}
                LIMIT $count_limit
            );
            """;
        cmd.Parameters.AddWithValue("$match", match);
        AddFilterParams(cmd, board, typeCode, dateFrom, dateTo, language, provision, ipcPrefix, onlyWithHeadnote);
        cmd.Parameters.AddWithValue("$count_limit", CountLimit + 1);
        var count = Convert.ToInt64(cmd.ExecuteScalar());
        return (Math.Min(count, CountLimit), count > CountLimit);
    }

    private List<DecisionHit> SearchDecisionsVector(string query, string? board, string? typeCode,
        string? dateFrom, string? dateTo, string? language, string? provision, string? ipcPrefix,
        bool onlyWithHeadnote, int limit)
    {
        var embedder = EmbedderCache.TryGet() ?? throw new InvalidOperationException("Embedding model not available");
        var qvec = E5Embedder.ToBlob(embedder.EmbedQuery(query));

        // KNN runs first (top $k chunks), then joins/filters narrow the result; with
        // selective filters this under-fills rather than re-ranking the filtered set —
        // acceptable for fusion, where the lexical leg still covers filtered matches.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT c.decision_id, MIN(v.distance) AS dist,
                   d.case_number, d.ecli, d.decision_date, d.board, d.language, d.headword, d.keywords,
                   substr(c.text, 1, 240) AS excerpt
            FROM chunks_vec v
            JOIN chunks c ON c.id = v.chunk_id
            JOIN decisions d ON d.id = c.decision_id
            WHERE v.embedding MATCH $qvec AND k = $k
              AND c.kind IN ('essence', 'reasons')
              {FilterSql(board, typeCode, dateFrom, dateTo, language, provision, ipcPrefix, onlyWithHeadnote, "d", "c.lang")}
            GROUP BY c.decision_id
            ORDER BY dist
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$qvec", qvec);
        cmd.Parameters.AddWithValue("$k", limit);
        cmd.Parameters.AddWithValue("$limit", limit);
        AddFilterParams(cmd, board, typeCode, dateFrom, dateTo, language, provision, ipcPrefix, onlyWithHeadnote);

        var hits = new List<DecisionHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new DecisionHit(
                reader.GetInt64(0),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? "" : reader.GetString(9),
                reader.GetFloat(1)));
        }

        return hits;
    }

    private List<BookHit> SearchBookLexical(string query, string? source, int limit)
    {
        var match = FtsHelper.BuildBookMatchQuery(query);
        if (string.IsNullOrEmpty(match))
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bs.id, bs.source, bs.section_id, bs.title, bs.breadcrumb, bs.page_from, bs.page_to,
                   snippet(book_fts, -1, '[[', ']]', '...', 12) AS snip,
                   bm25(book_fts) AS score
            FROM book_fts
            JOIN book_sections bs ON bs.id = book_fts.rowid
            WHERE book_fts MATCH $match
              AND ($src IS NULL OR bs.source = $src)
            ORDER BY score
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$match", match);
        cmd.Parameters.AddWithValue("$src", (object?)source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadBookHits(cmd);
    }

    private List<BookHit> SearchBookVector(string query, string? source, int limit)
    {
        var embedder = EmbedderCache.TryGet() ?? throw new InvalidOperationException("Embedding model not available");
        var qvec = E5Embedder.ToBlob(embedder.EmbedQuery(query));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bs.id, bs.source, bs.section_id, bs.title, bs.breadcrumb, bs.page_from, bs.page_to,
                   substr(c.text, 1, 240) AS excerpt, MIN(v.distance) AS dist
            FROM chunks_vec v
            JOIN chunks c ON c.id = v.chunk_id
            JOIN book_sections bs ON bs.id = c.book_section_id
            WHERE v.embedding MATCH $qvec AND k = $k
              AND c.kind = 'book'
              AND ($src IS NULL OR bs.source = $src)
            GROUP BY bs.id
            ORDER BY dist
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$qvec", qvec);
        cmd.Parameters.AddWithValue("$k", limit);
        cmd.Parameters.AddWithValue("$src", (object?)source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadBookHits(cmd);
    }

    private static List<long> ReciprocalRankFusion(List<(long Id, float Score)> a, List<(long Id, float Score)> b)
    {
        var scores = new Dictionary<long, double>();
        void Add(List<(long Id, float Score)> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var id = list[i].Id;
                scores[id] = scores.GetValueOrDefault(id) + 1.0 / (RrfK + i + 1);
            }
        }

        Add(a);
        Add(b);
        return scores.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    // langColumn must point at the language of the text row actually matched/returned
    // (decision_texts.lang or chunks.lang) — filtering on decisions.available_languages
    // lets a query through on a different-language text and returns a wrong-language snippet.
    private static string FilterSql(string? board, string? typeCode, string? dateFrom, string? dateTo,
        string? language, string? provision, string? ipcPrefix, bool onlyWithHeadnote,
        string alias = "d", string langColumn = "dt.lang") =>
        $"""

          AND ($board IS NULL OR {alias}.board = $board)
          AND ($code IS NULL OR {alias}.code = $code)
          AND ($date_from IS NULL OR {alias}.decision_date >= $date_from)
          AND ($date_to IS NULL OR {alias}.decision_date <= $date_to)
          AND ($lang IS NULL OR {langColumn} = $lang)
          AND ($ipc IS NULL OR {alias}.ipc LIKE $ipc || '%')
          AND ($headnote IS NULL OR {alias}.has_headnote = 1)
          AND ($provision IS NULL OR EXISTS (
              SELECT 1 FROM decision_provisions dp
              WHERE dp.decision_id = {alias}.id AND dp.provision = $provision))
          """;

    private static void AddFilterParams(SqliteCommand cmd, string? board, string? typeCode,
        string? dateFrom, string? dateTo, string? language, string? provision, string? ipcPrefix, bool onlyWithHeadnote)
    {
        cmd.Parameters.AddWithValue("$board", (object?)board ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$code", (object?)typeCode?.ToUpperInvariant() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$date_from", (object?)dateFrom ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$date_to", (object?)dateTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lang", (object?)language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ipc", (object?)ipcPrefix ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$headnote", onlyWithHeadnote ? 1 : DBNull.Value);
        cmd.Parameters.AddWithValue("$provision", (object?)provision ?? DBNull.Value);
    }

    private static List<DecisionHit> ReadDecisionHits(SqliteCommand cmd)
    {
        var list = new List<DecisionHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new DecisionHit(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? "" : reader.GetString(8),
                reader.GetFloat(9)));
        }

        return list;
    }

    private static List<BookHit> ReadBookHits(SqliteCommand cmd)
    {
        var list = new List<BookHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new BookHit(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? "" : reader.GetString(7),
                reader.GetFloat(8)));
        }

        return list;
    }

    private static object ToDecisionResult(DecisionHit h) => new
    {
        case_number = h.CaseNumber,
        ecli = h.Ecli,
        date = h.Date,
        board = h.Board,
        language = h.Language,
        headword = h.Headword,
        keywords = h.Keywords?.Split('\n').Take(3),
        snippet = h.Snippet,
    };

    private static object ToBookResult(BookHit h) => new
    {
        source = h.Source,
        section_id = h.SectionId,
        title = h.Title,
        breadcrumb = h.Breadcrumb,
        pages = new { from = h.PageFrom, to = h.PageTo },
        snippet = h.Snippet,
    };

    private sealed record DecisionHit(
        long decision_id, string CaseNumber, string? Ecli, string? Date, string? Board,
        string Language, string? Headword, string? Keywords, string Snippet, float score);

    private sealed record BookHit(
        long section_db_id, string Source, string SectionId, string? Title, string? Breadcrumb,
        int PageFrom, int PageTo, string Snippet, float score);
}
