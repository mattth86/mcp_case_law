using System.Globalization;
using EpoCaseLaw.Db;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace EpoCaseLaw.Embeddings;

public sealed class EmbedJob(ILogger logger)
{
    private const int BatchSize = 32;

    // Books and essence chunks are a small fraction of the corpus but carry a large
    // fraction of the retrieval value, so they embed first.
    private static readonly string[] AllStages = ["essence", "book", "reasons"];

    public async Task RunAsync(string dbPath, string? stage, CancellationToken ct = default)
    {
        await SqliteVecFetcher.EnsureAsync(logger, ct).ConfigureAwait(false);

        var downloader = new ModelDownloader(logger);
        await downloader.EnsureModelAsync(ct).ConfigureAwait(false);

        var embedder = EmbedderCache.TryGet()
            ?? throw new InvalidOperationException("Embedding model could not be loaded.");
        await using var conn = DbBootstrap.Open(dbPath);
        DbBootstrap.CreateSchema(conn, includeVec: true);

        var vecLoaded = DbBootstrap.VecExtensionLoaded(conn);
        if (!vecLoaded)
            logger.LogWarning("sqlite-vec extension not loaded; embeddings will be stored but vector KNN unavailable.");
        else
            await BackfillMissingVecRowsAsync(conn, ct).ConfigureAwait(false);

        DbBootstrap.SetMeta(conn, "embedding_model", "intfloat/multilingual-e5-small");

        var stages = stage?.ToLowerInvariant() switch
        {
            "essence" or "book" or "reasons" => new[] { stage.ToLowerInvariant() },
            null or "" or "all" => AllStages,
            _ => throw new ArgumentException($"Unknown stage: {stage}"),
        };

        foreach (var kind in stages)
        {
            logger.LogInformation("Embedding stage: {Kind}", kind);
            await EmbedKindAsync(conn, embedder, kind, vecLoaded, ct).ConfigureAwait(false);
            DbBootstrap.SetMeta(conn, $"embedded_{kind}_at", DateTime.UtcNow.ToString("O"));
        }

        var pending = CountPending(conn);
        if (pending == 0)
            DbBootstrap.SetMeta(conn, "embedded_at", DateTime.UtcNow.ToString("O"));
        logger.LogInformation("Embedding run finished; {Pending} chunks still pending.", pending);
    }

    private async Task EmbedKindAsync(SqliteConnection conn, E5Embedder embedder, string kind, bool vecLoaded, CancellationToken ct)
    {
        var done = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = FetchPending(conn, kind, BatchSize);
            if (batch.Count == 0)
                break;

            var vectors = embedder.EmbedPassages(batch.Select(b => b.Text).ToList());
            using var tx = conn.BeginTransaction();
            for (var i = 0; i < batch.Count; i++)
            {
                var blob = E5Embedder.ToBlob(vectors[i]);
                UpdateChunk(conn, batch[i].Id, blob);
                if (vecLoaded)
                    InsertVec(conn, batch[i].Id, blob);
            }

            tx.Commit();
            done += batch.Count;
            if (done % (BatchSize * 32) == 0)
                logger.LogInformation("Embedded {Count} {Kind} chunks so far", done, kind);
            await Task.Yield();
        }

        logger.LogInformation("Stage {Kind} complete: {Count} chunks embedded this run", kind, done);
    }

    private async Task BackfillMissingVecRowsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var done = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = FetchMissingVecRows(conn, BatchSize);
            if (batch.Count == 0)
                break;

            using var tx = conn.BeginTransaction();
            foreach (var (id, blob) in batch)
                InsertVec(conn, id, blob);
            tx.Commit();

            done += batch.Count;
            if (done % (BatchSize * 32) == 0)
                logger.LogInformation("Backfilled {Count} sqlite-vec rows so far", done);
            await Task.Yield();
        }

        if (done > 0)
            logger.LogInformation("Backfilled {Count} sqlite-vec rows from stored embeddings", done);
    }

    private static long CountPending(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE embedding IS NULL";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<(long Id, string Text)> FetchPending(SqliteConnection conn, string kind, int limit)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, text FROM chunks
            WHERE embedding IS NULL AND kind = $kind
            ORDER BY id
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<(long, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetInt64(0), reader.GetString(1)));
        return list;
    }

    private static List<(long Id, byte[] Blob)> FetchMissingVecRows(SqliteConnection conn, int limit)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.embedding
            FROM chunks c
            WHERE c.embedding IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM chunks_vec v WHERE v.chunk_id = c.id)
            ORDER BY c.id
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<(long, byte[])>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetInt64(0), reader.GetFieldValue<byte[]>(1)));
        return list;
    }

    private static void UpdateChunk(SqliteConnection conn, long id, byte[] blob)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chunks SET embedding = $blob WHERE id = $id";
        cmd.Parameters.AddWithValue("$blob", blob);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void InsertVec(SqliteConnection conn, long chunkId, byte[] blob)
    {
        // vec0 virtual tables do not support UPSERT; delete + insert instead.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM chunks_vec WHERE chunk_id = $id;
            INSERT INTO chunks_vec(chunk_id, embedding) VALUES ($id, $blob);
            """;
        cmd.Parameters.AddWithValue("$id", chunkId);
        cmd.Parameters.AddWithValue("$blob", blob);
        cmd.ExecuteNonQuery();
    }
}
