using System.Globalization;
using System.Xml;
using EpoCaseLaw.Db;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace EpoCaseLaw.Indexing;

public sealed class IndexRunner(ILogger logger)
{
    /// <summary>
    /// Default: full rebuild (database deleted first).
    /// incremental: keep the database and its embeddings; add new decisions/languages from the
    ///   XML, skip PDFs that are already indexed. Run "embed" afterwards to vectorize new chunks.
    /// booksOnly: replace the PDF-derived data (sections + book chunks) on the existing
    ///   database, leaving decisions and their embeddings alone.
    /// </summary>
    public async Task RunAsync(bool incremental = false, bool booksOnly = false, CancellationToken ct = default)
    {
        var dataRoot = Paths.DataRoot;
        var dbPath = Paths.DbPath;
        var xmlPath = Paths.DecisionsXmlPath;
        var clbPath = Path.Combine(dataRoot, Paths.CaseLawBookPdf);
        var glPath = Path.Combine(dataRoot, Paths.GuidelinesPdf);

        foreach (var path in new[] { xmlPath, clbPath, glPath })
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Required source file not found: {path}");
        }

        var dataVersion = booksOnly
            ? DbBootstrap.GetMeta(dbPath, "data_version") ?? DetectDataVersion(xmlPath)
            : DetectDataVersion(xmlPath);

        if (!booksOnly)
        {
            var xmlIndexer = new DecisionXmlIndexer(logger);
            await xmlIndexer.IndexAsync(xmlPath, dbPath, incremental, ct).ConfigureAwait(false);
        }

        await using (var conn = DbBootstrap.Open(dbPath, bulkLoad: !incremental && !booksOnly))
        {
            DbBootstrap.CreateSchema(conn);
            var pdfIndexer = new PdfBookIndexer(logger);

            if (booksOnly)
                DeleteBookData(conn);

            if (booksOnly || !(incremental && HasBookSource(conn, "clb")))
                pdfIndexer.IndexCaseLawBook(clbPath, conn);
            else
                logger.LogInformation("Case law book already indexed; skipping (use 'index --books' to refresh).");

            if (booksOnly || !(incremental && HasBookSource(conn, "gl")))
                pdfIndexer.IndexGuidelines(glPath, conn);
            else
                logger.LogInformation("Guidelines already indexed; skipping (use 'index --books' to refresh).");

            var counts = GetCounts(conn);
            DbBootstrap.SetMeta(conn, "indexed_at", DateTime.UtcNow.ToString("O"));
            DbBootstrap.SetMeta(conn, "data_version", dataVersion);
            DbBootstrap.SetMeta(conn, "decisions_xml", Path.GetFileName(xmlPath));
            DbBootstrap.SetMeta(conn, "decision_count", counts.Decisions.ToString(CultureInfo.InvariantCulture));
            DbBootstrap.SetMeta(conn, "chunk_count", counts.Chunks.ToString(CultureInfo.InvariantCulture));
            DbBootstrap.SetMeta(conn, "book_section_count", counts.BookSections.ToString(CultureInfo.InvariantCulture));

            logger.LogInformation(
                "Index complete: {Decisions} decisions, {Chunks} chunks, {Sections} book sections → {Db}",
                counts.Decisions, counts.Chunks, counts.BookSections, dbPath);

            var pendingEmbeddings = Scalar(conn, "SELECT COUNT(*) FROM chunks WHERE embedding IS NULL");
            if (pendingEmbeddings > 0)
                logger.LogInformation("{Pending} chunks have no embedding yet; run 'embed' to vectorize them.", pendingEmbeddings);
        }
    }

    /// <summary>
    /// Re-normalizes decision_provisions rows that were stored raw (normalizer didn't match
    /// at index time). Lets normalizer improvements reach an existing database without the
    /// full rebuild that would discard embeddings.
    /// </summary>
    public void FixProvisions(string dbPath)
    {
        using var conn = DbBootstrap.Open(dbPath);
        using var select = conn.CreateCommand();
        select.CommandText = "SELECT rowid, raw FROM decision_provisions WHERE provision = raw";

        var pending = new List<(long Rowid, string Normalized)>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                var raw = reader.GetString(1);
                var (normalized, _) = LegalRefNormalizer.Normalize(raw);
                if (normalized != raw && normalized.Length > 0)
                    pending.Add((reader.GetInt64(0), normalized));
            }
        }

        using var tx = conn.BeginTransaction();
        foreach (var (rowid, normalized) in pending)
        {
            using var update = conn.CreateCommand();
            update.CommandText = "UPDATE decision_provisions SET provision = $p WHERE rowid = $r";
            update.Parameters.AddWithValue("$p", normalized);
            update.Parameters.AddWithValue("$r", rowid);
            update.ExecuteNonQuery();
        }

        tx.Commit();
        logger.LogInformation("Re-normalized {Count} provision rows.", pending.Count);
    }

    private void DeleteBookData(SqliteConnection conn)
    {
        var vecLoaded = DbBootstrap.VecExtensionLoaded(conn);
        if (!vecLoaded && HasEmbeddedBookChunks(conn))
            throw new InvalidOperationException(
                "Cannot refresh book data because sqlite-vec is unavailable while embedded book chunks exist. Restore native/vec0 or rebuild the database.");
        if (vecLoaded)
            Exec(conn, "DELETE FROM chunks_vec WHERE chunk_id IN (SELECT id FROM chunks WHERE kind = 'book')");
        Exec(conn, "DELETE FROM chunks WHERE kind = 'book'");
        Exec(conn, "DELETE FROM book_sections"); // triggers keep book_fts in sync
        logger.LogInformation("Removed existing book sections and chunks; re-indexing PDFs.");
    }

    private static string DetectDataVersion(string xmlPath)
    {
        using var reader = XmlReader.Create(xmlPath, new XmlReaderSettings { IgnoreWhitespace = true });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "ep-appeal-bib-data")
                return reader.GetAttribute("volume") ?? Path.GetFileNameWithoutExtension(xmlPath);
        }

        return Path.GetFileNameWithoutExtension(xmlPath);
    }

    private static bool HasEmbeddedBookChunks(SqliteConnection conn) =>
        Scalar(conn, "SELECT EXISTS(SELECT 1 FROM chunks WHERE kind = 'book' AND embedding IS NOT NULL)") != 0;

    private static bool HasBookSource(SqliteConnection conn, string source)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM book_sections WHERE source = $src)";
        cmd.Parameters.AddWithValue("$src", source);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static (long Decisions, long Chunks, long BookSections) GetCounts(SqliteConnection conn) =>
        (Scalar(conn, "SELECT COUNT(*) FROM decisions"),
         Scalar(conn, "SELECT COUNT(*) FROM chunks"),
         Scalar(conn, "SELECT COUNT(*) FROM book_sections"));
}
