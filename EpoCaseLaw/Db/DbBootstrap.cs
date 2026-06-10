using Microsoft.Data.Sqlite;

namespace EpoCaseLaw.Db;

public static class DbBootstrap
{
    public static bool IsIndexed(string dbPath) =>
        File.Exists(dbPath) && GetMeta(dbPath, "indexed_at") is not null;

    public static string? GetMeta(string dbPath, string key)
    {
        if (!File.Exists(dbPath))
            return null;

        try
        {
            using var conn = Open(dbPath, readOnly: true);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            // A file that isn't a database, or one without a meta table, means "not indexed".
            return null;
        }
    }

    public static SqliteConnection Open(string dbPath, bool readOnly = false, bool bulkLoad = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        };

        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();

        if (!readOnly)
        {
            using var pragma = conn.CreateCommand();
            if (bulkLoad)
            {
                pragma.CommandText = """
                    PRAGMA journal_mode = MEMORY;
                    PRAGMA synchronous = OFF;
                    PRAGMA temp_store = MEMORY;
                    PRAGMA mmap_size = 30000000000;
                    """;
            }
            else
            {
                pragma.CommandText = """
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;
                    PRAGMA mmap_size = 30000000000;
                    """;
            }

            pragma.ExecuteNonQuery();
        }

        TryLoadVecExtension(conn);
        return conn;
    }

    public static void TryLoadVecExtension(SqliteConnection conn)
    {
        var lib = Paths.SqliteVecLib;
        if (!File.Exists(lib))
            return;

        try
        {
            conn.EnableExtensions(true);
            conn.LoadExtension(lib);
        }
        catch
        {
            // Vector search degrades to BM25-only.
        }
    }

    public static bool VecExtensionLoaded(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT vec_version()";
            cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureSearchSchema(string dbPath)
    {
        if (!File.Exists(dbPath))
            return;

        try
        {
            using var conn = Open(dbPath);
            CreateSchema(conn, includeVec: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Search schema check failed; existing database will be used as-is: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void CreateSchema(SqliteConnection conn, bool includeVec = true)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS decisions (
              id INTEGER PRIMARY KEY,
              case_number TEXT NOT NULL,
              code TEXT NOT NULL,
              appeal_num TEXT,
              year INTEGER,
              ecli TEXT UNIQUE,
              board TEXT,
              decision_date TEXT,
              language TEXT,
              distribution TEXT,
              title TEXT,
              application_number TEXT,
              ipc TEXT,
              headword TEXT,
              keywords TEXT,
              oj_reference TEXT,
              has_headnote INTEGER NOT NULL DEFAULT 0,
              available_languages TEXT
            );

            CREATE TABLE IF NOT EXISTS decision_texts (
              id INTEGER PRIMARY KEY,
              decision_id INTEGER REFERENCES decisions(id),
              lang TEXT,
              is_original INTEGER,
              headnote TEXT,
              catchwords TEXT,
              facts TEXT,
              reasons TEXT,
              orders TEXT,
              UNIQUE(decision_id, lang)
            );

            CREATE TABLE IF NOT EXISTS decision_provisions (
              decision_id INTEGER,
              provision TEXT,
              raw TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_prov ON decision_provisions(provision);

            CREATE TABLE IF NOT EXISTS decision_citations (
              citing_id INTEGER,
              cited_case_number TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_cit_cited ON decision_citations(cited_case_number);

            -- External-content FTS (not contentless): snippet()/highlight() need access
            -- to the original text. The indexer still inserts rows explicitly; the view
            -- only serves reads. decision_texts rows are never updated or deleted, so no
            -- sync triggers are needed.
            CREATE VIEW IF NOT EXISTS decisions_fts_src AS
              SELECT dt.id AS id, d.case_number, d.headword, d.keywords, dt.catchwords,
                     dt.headnote, d.title, dt.facts, dt.reasons, dt.orders
              FROM decision_texts dt JOIN decisions d ON d.id = dt.decision_id;

            CREATE VIRTUAL TABLE IF NOT EXISTS decisions_fts USING fts5(
              case_number, headword, keywords, catchwords, headnote, title, facts, reasons, orders,
              content='decisions_fts_src', content_rowid='id', tokenize='unicode61 remove_diacritics 2'
            );

            CREATE TABLE IF NOT EXISTS book_sections (
              id INTEGER PRIMARY KEY,
              source TEXT NOT NULL,
              section_id TEXT NOT NULL,
              title TEXT,
              breadcrumb TEXT,
              page_from INTEGER,
              page_to INTEGER,
              part INTEGER DEFAULT 1,
              body TEXT
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS book_fts USING fts5(
              section_id, title, breadcrumb, body,
              content='book_sections', content_rowid='id', tokenize='unicode61 remove_diacritics 2'
            );

            CREATE TABLE IF NOT EXISTS chunks (
              id INTEGER PRIMARY KEY,
              kind TEXT NOT NULL,
              decision_id INTEGER,
              book_section_id INTEGER,
              lang TEXT,
              seq INTEGER,
              text TEXT NOT NULL,
              embedding BLOB
            );
            CREATE INDEX IF NOT EXISTS idx_chunks_pending ON chunks(id) WHERE embedding IS NULL;
            CREATE INDEX IF NOT EXISTS idx_chunks_decision ON chunks(decision_id, kind);
            CREATE INDEX IF NOT EXISTS idx_chunks_book ON chunks(book_section_id);

            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);

            CREATE TRIGGER IF NOT EXISTS book_sections_ai AFTER INSERT ON book_sections BEGIN
              INSERT INTO book_fts(rowid, section_id, title, breadcrumb, body)
              VALUES (new.id, new.section_id, new.title, new.breadcrumb, new.body);
            END;
            CREATE TRIGGER IF NOT EXISTS book_sections_ad AFTER DELETE ON book_sections BEGIN
              INSERT INTO book_fts(book_fts, rowid, section_id, title, breadcrumb, body)
              VALUES ('delete', old.id, old.section_id, old.title, old.breadcrumb, old.body);
            END;
            CREATE TRIGGER IF NOT EXISTS book_sections_au AFTER UPDATE ON book_sections BEGIN
              INSERT INTO book_fts(book_fts, rowid, section_id, title, breadcrumb, body)
              VALUES ('delete', old.id, old.section_id, old.title, old.breadcrumb, old.body);
              INSERT INTO book_fts(rowid, section_id, title, breadcrumb, body)
              VALUES (new.id, new.section_id, new.title, new.breadcrumb, new.body);
            END;
            """;
        cmd.ExecuteNonQuery();

        if (EnsureDecisionsFtsSchema(conn))
            RebuildDecisionFts(conn);

        if (includeVec && VecExtensionLoaded(conn))
        {
            using var vecCmd = conn.CreateCommand();
            vecCmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS chunks_vec USING vec0(
                  chunk_id INTEGER PRIMARY KEY,
                  embedding float[384]
                );
                """;
            try
            {
                vecCmd.ExecuteNonQuery();
            }
            catch
            {
                // vec table may already exist with different schema
            }
        }
    }

    public static void RebuildDecisionFts(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO decisions_fts(decisions_fts) VALUES ('rebuild')";
        cmd.ExecuteNonQuery();
    }

    private static bool EnsureDecisionsFtsSchema(SqliteConnection conn)
    {
        using var read = conn.CreateCommand();
        read.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'decisions_fts'";
        var sql = read.ExecuteScalar() as string;
        if (sql is not null
            && sql.Contains("content", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("decisions_fts_src", StringComparison.OrdinalIgnoreCase))
            return false;

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP TABLE IF EXISTS decisions_fts";
            drop.ExecuteNonQuery();
        }

        CreateDecisionsFtsTable(conn);
        return true;
    }

    private static void CreateDecisionsFtsTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE VIRTUAL TABLE decisions_fts USING fts5(
              case_number, headword, keywords, catchwords, headnote, title, facts, reasons, orders,
              content='decisions_fts_src', content_rowid='id', tokenize='unicode61 remove_diacritics 2'
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public static void SetMeta(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
