# EPO Case Law MCP Server — Implementation Plan

*(v2 — hybrid search with embeddings is now Phase 1; Docker assessed in §1.2)*

> **Implementation review, 10 June 2026 — findings addressed.** The build largely followed
> this plan; the review found and fixed: (1) wrong token-id mapping into the ONNX model
> (raw SentencePiece ids instead of HF XLM-R ids — embeddings were semantically useless;
> §9's "tokenizer is the highest-risk integration point" warning materialized exactly);
> (2) sqlite-vec rejects UPSERT, and the silent catch around it meant the vector table was
> never populated; (3) FTS5 syntax errors on punctuated queries; (4) PDF outline traversal
> indexed every section once per tree depth with broken ids; (5) provision era variants
> (EPC 1973/2000, RPBA 2007/2020) not collapsed, so lookups missed nearly everything;
> (6) the ONNX model was reloaded per query (~10 s/search; now a cached singleton);
> (7) chunks up to 10k chars exceeded the 512-token embedding window. Index and
> embeddings were rebuilt after the fixes; `-- smoke` now includes an embedding
> discrimination check that fails loudly if (1) ever regresses.
>
> **Second review round, 10 June 2026 — also addressed:** (8) pagination dead-ended past
> the first 100 candidates (fixed-size pool, offset applied in memory; pool now scales with
> offset+limit, capped at 1000); (9) the `language` filter matched on the decision's
> available languages, returning wrong-language snippets (now filters the matched text
> row's language: `decision_texts.lang` / `chunks.lang`); (10) sections sharing a PDF page
> all got the same whole-page body (882 duplicate groups; bodies are now sliced at heading
> positions located in the page text — zero duplicates); (11) `_Sent_N` / `Satz N` /
> Protocol-on-Art-69 / dotted-PCT-rule refs stayed raw (normalizer extended; a new
> `fix-provisions` command repairs an existing DB in place — 464 rows re-normalized);
> (12) a corrupt/partial `epo.db` made readiness checks throw instead of reporting "index
> missing". Also added: `index --incremental` (adds new decisions/languages, preserves
> embeddings — verified: re-running over the same XML adds 0 and leaves vectors intact)
> and `index --books` (re-extracts the PDFs only). Smoke now also covers pagination and
> language-filter correctness.
>
> **Third review round, 10 June 2026:** (13) decision search snippets were silently empty —
> `decisions_fts` was contentless (`content=''`), and FTS5 cannot reconstruct text for
> snippet()/highlight() on contentless tables; it is now external-content backed by a
> `decisions_fts_src` view (migrated in place, ~23 s rebuild, embeddings untouched);
> (14) case numbers inside longer queries ("admissibility of appeal T 664/16") are now
> detected, canonicalized, and OR-matched against both the case_number column and the text
> (finding citing decisions too); (15) `get_legal_text_section` tolerates casing/whitespace
> slips and unambiguous section-id prefixes. Reviewed-but-not-changed: fp32 ONNX model
> (int8 would invalidate the embedding run in progress — listed under enhancements),
> `.mcp.json --no-build` (deliberate: `dotnet run` build output on stdout would corrupt
> the stdio protocol; README setup step covers the one-time build).

A .NET MCP server that lets an agent (e.g. Claude) search and read:

1. **EPO Boards of Appeal decisions** — `EPDecisions_March2026.xml` (1.2 GB, in this folder)
2. **Case Law of the Boards of Appeal, 11th ed. 2025** — `en-case-law-of-the-boards-of-appeal-2025.pdf` (1,988 pages)
3. **Guidelines for Examination 2026** — `en-epc-guidelines-2026-hyperlinked.pdf` (1,092 pages)

The end goal: a user asks Claude a question about EPO practice ("What is the current approach to claim interpretation using the description?", "Find decisions where late-filed requests were admitted under RPBA Art. 13(2)") and the agent answers by searching this corpus and citing decisions, case-law-book sections, and Guidelines sections.

---

## 1. Search paradigm

**Hybrid search is the current state of the art**: lexical search (BM25) + dense vector (semantic/embedding) search, with the two ranked lists fused by **Reciprocal Rank Fusion (RRF)**, optionally followed by a cross-encoder reranker over the top ~50 candidates. The MCP context adds a complementary paradigm, **agentic retrieval**: the agent iterates — reformulating queries, applying metadata filters, following citations, drilling into sections — so the tool surface (filters, snippets, citation graph, navigation) matters as much as ranking.

**Phase 1 builds full hybrid search**, structured so the server is usable immediately while the long-running embedding job completes:

- `index` command (~10–15 min): streams the XML and PDFs into SQLite with FTS5/BM25 lexical search, all metadata, citation graph, and pre-computed (but not yet embedded) chunks. **The server is fully usable, BM25-only, after this step.**
- `embed` command (long-running, resumable): fills in chunk embeddings using a local ONNX model. Runs in two stages — stage 1 embeds each decision's "essence" (headword + keywords + catchwords + headnote, ~50k chunks, minutes-to-tens-of-minutes) so semantic search starts working early; stage 2 embeds the reasons-for-decision body chunks (the bulk; expect **roughly 1–3 hours on Apple Silicon CPU**, run once, resumable if interrupted).
- The search tools detect at query time whether embeddings exist and silently upgrade from BM25-only → hybrid (BM25 + vector + RRF). No configuration, no agent-visible change.

Deferred to Phase 2 (§8): cross-encoder reranking, larger/hosted embedding models, query-time synonym expansion, vector-native databases.

### 1.2 Does Docker change the plan? (assessed — no, not for the default path)

Docker was considered for two roles and rejected for the default path, for concrete reasons:

- **Embedding inference in a container** (e.g. HuggingFace text-embeddings-inference): on macOS, Docker containers get **no GPU/Metal access**, so this is CPU-only — same speed as running ONNX inference *in-process* in the C# server, but with an extra always-running service. In-process wins: zero runtime dependencies.
- **A vector database container** (Qdrant): at this corpus size (~500k vectors × 384 dims), sqlite-vec brute-force scan answers a query in a few hundred milliseconds — fine for agent usage patterns (a handful of searches per turn). Qdrant's HNSW would make that ~10 ms, but at the cost that **every Claude session silently depends on Docker Desktop being up**; if it isn't, the tools fail. That directly conflicts with the "type `claude` and it works" requirement.

So: **no Docker in Phase 1.** Everything runs in one process against one database file. Docker becomes valuable at the points listed in §10 (the prepared Qdrant + TEI `docker-compose.yml` escape hatch) — chiefly if search latency starts to matter, the corpus grows, or the in-process ONNX/tokenizer route hits an implementation wall.

*(Side note: the fastest bulk-embedding option on this machine would be native Ollama, which does use Metal — but it's a new tool install, and index-time and query-time embeddings must come from the same model/stack, so it would also become a query-time dependency. Not worth it for a one-off job that can run in the background or overnight. Mentioned for completeness only.)*

---

## 2. Facts about the data (already verified — do not re-derive)

### 2.1 `EPDecisions_March2026.xml`

- Single 1.2 GB UTF-8 file. Root `<ep-appeal-decisions date-produced="20260310">` containing **51,299** `<ep-appeal-decision>` elements. **Must be parsed with a streaming `XmlReader`** (never `XDocument.Load` on the whole file). Per-decision subtrees average ~25 KB and can be materialized individually via `reader.ReadSubtree()` → `XElement.Load`.
- **48,618 distinct ECLIs** — ~2,700 documents are translations of the same decision. The `lang` attribute is the document language; `procedure-lang` is the language of proceedings. Where `lang != procedure-lang` the document is a translation. Language split: ~33.6k EN, ~13.9k DE, ~3.9k FR documents.
  - **Dedupe rule:** group by ECLI; mark the document where `lang == procedure-lang` as the original; store translations as alternate texts of the same decision (don't drop them — they make German/French decisions findable with English BM25 queries).
- Schema: `Schema_and_dtd/ep-appeal-decision.v1.1.xsd` (+ DTD). Key per-decision structure:
  - `<ep-appeal-decision accession-num dtd-version lang procedure-lang appeal-type>`
  - `<ep-appeal-bib-data reference="T160664DU1" volume="2026/001">` containing:
    - `<ep-distribution-code>` — A/B/C/D distribution (A = published in OJ, D = no distribution); may be empty
    - `<ep-case-num code="T"><country>EP</country><ep-appeal-num>0664</ep-appeal-num><ep-year>2016</ep-year></ep-case-num>` — codes: **G** (Enlarged Board), **T** (Technical), **J** (Legal), **D** (Disciplinary), **W** (PCT protests), **R** (petitions for review). Render canonical case numbers as `T 0664/16` (code, 4-digit number, 2-digit year).
    - `<ep-board-of-appeal-code>` — e.g. `3.3.01`, `DBA`, `EBA`
    - `<application-reference>` → EP application number
    - `<ep-date-of-decision><date>YYYYMMDD</date>`
    - `<ep-ecli>` — e.g. `ECLI:EP:BA:2019:T066416.20191121`
    - `<invention-title lang=...>`
    - `<classifications-ipcr>` → IPC codes (section/class/subclass/main-group/subgroup → render `A61K9/22`)
    - `<ep-keywords lang=...><keyword>...` — curated indexing phrases, e.g. *"Hilfsantrag - Erfinderische Tätigkeit (ja)"* — **high-value search field**
    - `<ep-headword>` — short subject phrase
    - `<ep-legal-refs><ep-legal-citation>...<ep-legal-ref-presentation>` — cited provisions in coded form, e.g. `REE_Art_008(b)`. **Implementation note:** scan the distinct prefixes in the real data (expect `EPC`, `EPC1973`, `R` rules, `RPBA`, `REE`, `PCT`, …) and write a normaliser to human-readable form (`Art. 56 EPC`, `R. 103(1)(a) EPC`, `Art. 13(2) RPBA 2020`), keeping the raw code too.
    - `<ep-cited-decisions><ep-cited-decision country code><ep-appeal-num><ep-year>` — citation graph edges
    - `<ep-official-journal>` reference where published
  - Body elements (each `lang`-attributed, containing `<p>` paragraphs, occasionally lists/tables per the XSD): `<ep-headnote>`, `<ep-catchword>`, `<ep-summary-of-facts>`, `<ep-reasons-for-decision>`, `<ep-appeal-order>`. Headnotes/catchwords exist only on a minority of (important) decisions.

### 2.2 The two PDFs

Both have **complete bookmark/outline trees** — use them as the chunking skeleton:

- Case law book: **2,868 outline entries**, hierarchical (`I. PATENTABILITY` → `A. Patentable inventions` → `1. …` → `1.1 …`). Citable as e.g. *CLB II.A.3.1*.
- Guidelines: **1,919 outline entries** (`Part A` → `Chapter II` → `1.` → `1.1`). Citable as e.g. *GL A-II, 1.1*.

Chunking rule: a chunk = one **leaf** bookmark's page span (from its destination page to the next bookmark's page). Attach the full breadcrumb (ancestor titles) to every chunk. Split any chunk over ~10,000 chars at paragraph boundaries into `part 1/2/...`. Extract text with **UglyToad.PdfPig** (pure .NET, no native deps; reads both text and outlines).

---

## 3. Technology stack

| Component | Choice | Notes |
|---|---|---|
| Runtime | **.NET 10** (current LTS) | `dotnet --version` to confirm installed; any 8+ works if needed |
| MCP SDK | **`ModelContextProtocol`** NuGet, v1.x | The official C# SDK (modelcontextprotocol/csharp-sdk, maintained with Microsoft). Stable v1.0 since March 2026. Use the stdio transport — that's what Claude Code/Desktop launch. |
| Hosting | `Microsoft.Extensions.Hosting` | `Host.CreateApplicationBuilder` + `.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()` |
| Storage / lexical search | **SQLite + FTS5** via `Microsoft.Data.Sqlite` | The bundled `e_sqlite3` native library includes FTS5. Single file DB, zero setup. |
| Vector search | **sqlite-vec** loadable extension (`vec0`) | No first-class NuGet: download the macOS arm64 `vec0.dylib` from the sqlite-vec GitHub releases at build time (csproj target or a fetch script) and load with `connection.EnableExtensions(); connection.LoadExtension(path)`. Fallback if loading misbehaves: `Microsoft.SemanticKernel.Connectors.SqliteVec` bundles the native extension and can be used just for its packaged binary. |
| Embedding model | **`intfloat/multilingual-e5-small`**, ONNX, int8-quantized | 384 dims, 512-token window, strong EN/DE/FR, ~120 MB quantized. Ready-made ONNX exports with `tokenizer.json` exist on Hugging Face (e.g. the official `onnx/` folder, Xenova/Teradata mirrors). **Auto-download on first `embed` run** into `models/` — no manual step. |
| Inference | `Microsoft.ML.OnnxRuntime` | CPU EP, batch ~16–32, mean-pool over attention mask, L2-normalize. |
| Tokenizer | `Microsoft.ML.Tokenizers` (SentencePiece, XLM-R model) | **Verify early in implementation** that it loads the e5 sentencepiece model and round-trips known token IDs against the HF tokenizer. If not: fall back to `Tokenizers.DotNet` (bindings to HF's Rust tokenizers; loads `tokenizer.json` directly — known to work). |
| PDF extraction | `UglyToad.PdfPig` | Text + bookmark outline with page destinations |
| Logging | `Microsoft.Extensions.Logging` console → **stderr** | **Critical:** a stdio MCP server must never write to stdout. Configure `consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace`. |

No Docker, no external services, no API keys. One process, one DB file, one models folder.

## 4. Project layout

```
mcp_case_law/
├── PLAN.md                         (this file)
├── EPDecisions_March2026.xml       (source data — read-only)
├── Schema_and_dtd/                 (source schemas — read-only)
├── en-case-law-of-the-boards-of-appeal-2025.pdf
├── en-epc-guidelines-2026-hyperlinked.pdf
├── .mcp.json                       (Claude Code project-scope server registration)
├── .gitignore                      (epo.db*, models/, bin/, obj/, publish/)
└── EpoCaseLaw/
    ├── EpoCaseLaw.csproj
    ├── Program.cs                  (arg routing: "index" / "embed" → jobs, default → MCP server)
    ├── Db/
    │   └── DbBootstrap.cs          (schema DDL, pragmas, sqlite-vec loading, connection factory)
    ├── Indexing/
    │   ├── DecisionXmlIndexer.cs   (streaming XML → SQLite, incl. chunk rows)
    │   ├── PdfBookIndexer.cs       (outline-driven PDF → SQLite)
    │   ├── Chunker.cs              (~400-token chunks, paragraph-aligned, small overlap)
    │   └── LegalRefNormalizer.cs   (REE_Art_008(b) → "Art. 8(b) REE" etc.)
    ├── Embeddings/
    │   ├── ModelDownloader.cs      (HF direct URLs → models/, with checksum + progress)
    │   ├── E5Embedder.cs           (tokenize → ONNX → mean-pool → normalize; "query: "/"passage: " prefixes)
    │   └── EmbedJob.cs             (two-stage, batched, resumable: processes chunks WHERE embedding IS NULL)
    ├── Search/
    │   ├── SearchService.cs        (FTS query building, escaping, snippets, filters)
    │   └── HybridSearch.cs         (BM25 top-100 + vec KNN top-100 → RRF k=60 → merged page)
    └── Tools/
        ├── DecisionTools.cs        ([McpServerToolType] — decision search/read/citations)
        └── LegalTextTools.cs       ([McpServerToolType] — book + guidelines search/read)
```

Single console project, three modes:

- `dotnet run --project EpoCaseLaw -- index` → builds `epo.db` lexical index + chunk rows from the three source files (~10–15 min), prints progress to stderr, exits. **Server usable (BM25-only) after this.**
- `dotnet run --project EpoCaseLaw -- embed` → downloads the model if absent, then fills `chunks.embedding` in two stages (essence first, then reasons). Batched commits; safe to Ctrl-C and re-run (resumes at NULL embeddings). Optional `--stage essence` to stop after stage 1.
- `dotnet run --project EpoCaseLaw` (no args) → starts the MCP server on stdio. Hybrid search activates automatically for whatever has embeddings so far.

DB location: `EPO_DB_PATH` env var if set, else `epo.db` in the same directory as the source data (resolve relative to the executable's location falling back to CWD). If the DB is missing when the server starts, **still start successfully** but have every tool return a clear instruction: *"Index not built. Run: dotnet run --project EpoCaseLaw -- index"*. (Don't auto-index on first tool call — it takes minutes and would look like a hang.)

## 5. Database schema

```sql
PRAGMA journal_mode=WAL;             -- at query time; use MEMORY + synchronous=OFF during bulk indexing

CREATE TABLE decisions (
  id INTEGER PRIMARY KEY,
  case_number TEXT NOT NULL,         -- "T 0664/16"
  code TEXT NOT NULL,                -- G/T/J/D/W/R
  appeal_num TEXT, year INTEGER,
  ecli TEXT UNIQUE,
  board TEXT,                        -- "3.3.01", "EBA", "DBA"
  decision_date TEXT,                -- ISO yyyy-MM-dd
  language TEXT,                     -- original procedure language
  distribution TEXT,                 -- A/B/C/D or NULL
  title TEXT,                        -- invention title
  application_number TEXT,
  ipc TEXT,                          -- space-separated "A61K9/22 A61K9/20"
  headword TEXT,
  keywords TEXT,                     -- newline-joined keyword phrases (original language doc)
  oj_reference TEXT,
  has_headnote INTEGER NOT NULL DEFAULT 0,
  available_languages TEXT           -- "en,de" (original + translations present)
);

CREATE TABLE decision_texts (        -- one row per (decision, language); is_original flags procedure language
  decision_id INTEGER REFERENCES decisions(id),
  lang TEXT, is_original INTEGER,
  headnote TEXT, catchwords TEXT, facts TEXT, reasons TEXT, orders TEXT,
  PRIMARY KEY (decision_id, lang)
);

CREATE TABLE decision_provisions (   -- normalized legal refs, for filtering/lookup
  decision_id INTEGER, provision TEXT, raw TEXT
);
CREATE INDEX idx_prov ON decision_provisions(provision);

CREATE TABLE decision_citations (    -- citation graph
  citing_id INTEGER, cited_case_number TEXT   -- canonical form; may cite decisions outside corpus
);
CREATE INDEX idx_cit_cited ON decision_citations(cited_case_number);

-- FTS over decisions: one row per decision_texts row, external content to avoid duplication
CREATE VIRTUAL TABLE decisions_fts USING fts5(
  case_number, headword, keywords, catchwords, headnote, title, facts, reasons, orders,
  content='', tokenize='unicode61 remove_diacritics 2'
);
-- rowid = decision_texts rowid; weight via bm25(decisions_fts, 10,8,8,8,8,4,1,2,1) at query time
-- (boost case_number/headword/keywords/catchwords/headnote over body text)

CREATE TABLE book_sections (
  id INTEGER PRIMARY KEY,
  source TEXT NOT NULL,              -- 'clb' (case law book) | 'gl' (guidelines)
  section_id TEXT NOT NULL,          -- derived label: "II.A.3.1" / "A-II, 1.1" (parse from bookmark titles; fall back to a synthetic outline path)
  title TEXT, breadcrumb TEXT,       -- "I. PATENTABILITY > A. Patentable inventions > 1. ..."
  page_from INTEGER, page_to INTEGER,
  part INTEGER DEFAULT 1,            -- for split chunks
  body TEXT
);
CREATE VIRTUAL TABLE book_fts USING fts5(
  section_id, title, breadcrumb, body,
  content='book_sections', content_rowid='id', tokenize='unicode61 remove_diacritics 2'
);

-- Chunks for embedding. Rows are created during `index`; embedding stays NULL until `embed` fills it.
CREATE TABLE chunks (
  id INTEGER PRIMARY KEY,
  kind TEXT NOT NULL,                -- 'essence' | 'reasons' | 'book'
  decision_id INTEGER,               -- for decision chunks
  book_section_id INTEGER,           -- for book chunks
  lang TEXT, seq INTEGER,
  text TEXT NOT NULL,
  embedding BLOB                     -- 384 × float32 (little-endian), L2-normalized; NULL = not yet embedded
);
CREATE INDEX idx_chunks_pending ON chunks(id) WHERE embedding IS NULL;

-- sqlite-vec KNN index, populated from chunks as embeddings land (same transaction)
CREATE VIRTUAL TABLE chunks_vec USING vec0(
  chunk_id INTEGER PRIMARY KEY,
  embedding float[384]               -- if scan latency disappoints: switch to int8[384] (4× smaller/faster, tiny quality cost)
);
-- query: SELECT chunk_id, distance FROM chunks_vec WHERE embedding MATCH :query_vec AND k = 100
--        [AND chunk_id IN (SELECT id FROM chunks WHERE ...)] for filtered search

CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);  -- data version "2026/001", counts, build timestamp, model name+revision
```

**Chunking & embedding policy (keeps volume sane, ~400–600k chunks total):**

- `essence` chunks: one per decision *original-language* document — headword + keywords + catchwords + headnote concatenated. ~48.6k chunks. Embedded first (stage 1).
- `reasons` chunks: reasons-for-decision of the **original-language document only** (~400-token chunks, paragraph-aligned, 1–2 sentence overlap). Translations are *not* embedded — e5 is cross-lingual, so an English query matches German chunks in the same vector space; translations remain covered by the BM25 leg. Facts and order are not embedded in v1 (BM25 covers them; reasons carry the law).
- `book` chunks: every `book_sections` row (split parts count separately). ~3–6k chunks.
- Vectors at 384 × float32 ≈ 0.6–0.9 GB on top of the ~2.5–3.5 GB lexical DB.

**Indexing mechanics:** transactions of ~500 decisions; progress every 1,000 (stderr). Plain-text extraction: concatenate `<p>` descendants with `\n\n`, preserving list/table content as text. FTS query safety: quote/escape user input (support `"exact phrases"`, strip FTS operators) — never pass raw input as FTS5 MATCH syntax. Detect case-number patterns in queries (`T 664/16`, `G 1/19`) and normalise to canonical zero-padded form, searching `case_number`/citations as well as text. Snippets via FTS5 `snippet()`, ~40 tokens.

**Hybrid query flow (`HybridSearch.cs`):** run BM25 leg (top 100 after filters) and vector leg (embed query with `"query: "` prefix → KNN top 100, filter via `chunk_id IN` subquery, collapse chunks to their parent decision/section keeping best rank) → RRF with k=60 → return requested page. If no embeddings exist yet (or the model folder is absent), skip the vector leg silently. Expose `mode` (`hybrid` | `lexical`) as an optional tool parameter defaulting to auto, so behaviour is testable.

## 6. MCP tool surface

Use `[McpServerToolType]` / `[McpServerTool]` attributes with `[Description]` on every tool and parameter — the descriptions are the agent's UX; write them carefully. Register via `.WithToolsFromAssembly()`. Set the server's `Instructions` (ServerInfo) to a short usage guide: what the corpus is, the data cut-off (March 2026), citation conventions, and a hint to iterate (search → refine with filters → fetch parts → follow citations).

Keep responses **compact JSON**, default `limit` 10, max ~25. Every search result must carry enough to cite: case number, ECLI, date, board, language — or section id + breadcrumb. Search responses should state which mode answered (`"mode": "hybrid"` / `"lexical"`) — invaluable when verifying the embedding rollout.

1. **`search_decisions`** — `query` (required), optional `board`, `type_code` (G/T/J/D/W/R), `date_from`, `date_to`, `language`, `provision` (normalized, e.g. "Art. 56 EPC"), `ipc_prefix`, `only_with_headnote` (bool), `mode`, `limit`, `offset`.
   Returns ranked hits: case_number, ecli, date, board, language, headword, top keywords, snippet (from the best-matching chunk or FTS field), plus total match count (so the agent knows to narrow).
2. **`get_decision`** — `case_number` (accepts `T 664/16`, `T 0664/16`, or ECLI), `part` enum: `summary` (default: full bib + keywords + headnote + catchwords + order), `facts`, `reasons`, `order`, `full`; optional `lang` to pick a translation. Long parts are returned whole (reasons can be tens of KB — that's fine, the agent asked for it) but `summary` stays small.
3. **`get_decision_citations`** — `case_number`, `direction`: `cites` (what it relied on) | `cited_by` (later decisions citing it, with date/board/keywords — effectively "is this still good law / how was it followed").
4. **`search_legal_texts`** — `query`, `source`: `case_law_book` | `guidelines` | `both` (default), `mode`, `limit`. Returns section_id, source, title, breadcrumb, pages, snippet.
5. **`get_legal_text_section`** — `source`, `section_id`. Returns full section text + breadcrumb + neighbouring/child section ids (navigation without another search).
6. **`lookup_provision`** — `provision` (e.g. "Art. 123(2) EPC", "Art. 13(2) RPBA"). Returns: count of citing decisions, the most recent ~10 and most-cited ~5 citing decisions (favouring ones with headnotes / distribution A–B), plus matching case-law-book and Guidelines sections. One call answers "what's the law on X provision".

## 7. Claude integration & testing (the "no extra steps" part)

Check in a project-scope **`.mcp.json`** at the folder root so Claude Code picks the server up automatically when started in this folder (one-time approval prompt, nothing else):

```json
{
  "mcpServers": {
    "epo-case-law": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "EpoCaseLaw", "-c", "Release", "--no-build"]
    }
  }
}
```

Build flow the implementing agent should leave the user with:

```bash
dotnet build EpoCaseLaw -c Release                     # once
dotnet run --project EpoCaseLaw -c Release -- index    # once, ~10–15 min → BM25 search works
dotnet run --project EpoCaseLaw -c Release -- embed    # once, background/overnight OK; resumable; hybrid switches on as it progresses
claude                                                 # in this folder — server auto-loads from .mcp.json
```

The user can start testing in Claude immediately after `index`, with `embed` still running in another terminal (readers don't block the writer under WAL; the embed job must take write transactions in short batches).

Alternatives to document in the README: user-scope registration `claude mcp add --scope user epo-case-law -- dotnet run --project /Users/matthew/Developer/mcp_case_law/EpoCaseLaw -c Release --no-build` (available in every folder), and a Claude Desktop `claude_desktop_config.json` snippet with absolute paths. If startup latency of `dotnet run` annoys, switch `.mcp.json` to the published binary: `dotnet publish EpoCaseLaw -c Release -o publish` → command `./publish/EpoCaseLaw`.

**Verification checklist for the implementing agent (run all before declaring done):**

1. **Embedding stack sanity (do this first, it's the riskiest part):** tokenizer round-trip matches HF reference token IDs for an EN + DE + FR sentence; embedding of "query: inventive step" vs "passage: erfinderische Tätigkeit" has high cosine similarity, vs an unrelated passage low.
2. `index` completes; logs ≈51,299 documents parsed / ≈48,618 unique decisions / both PDFs sectioned (≈2,000+ sections, chunk rows created); `meta` populated.
3. Direct DB sanity: `T 0664/16` exists with board `3.3.01`, date `2019-11-21`, ECLI `ECLI:EP:BA:2019:T066416.20191121`.
4. `embed --stage essence` completes; `chunks_vec` row count matches embedded chunks; KNN query returns sensible neighbours.
5. Tool-level smoke tests via a tiny stdio harness or `npx @modelcontextprotocol/inspector --cli` against the built server:
   - `search_decisions("problem solution approach inventive step", type_code:"T")` returns results with snippets;
   - **semantic test:** `search_decisions("can a computer simulation be patented?", mode:"hybrid")` surfaces G 1/19 / simulation decisions that pure BM25 ranks poorly; same query with `mode:"lexical"` for comparison;
   - **cross-language test:** an English query returns a relevant German-language decision via the vector leg;
   - `get_decision("G 1/19", part:"summary")`; `lookup_provision("Art. 123(2) EPC")`; `search_legal_texts("clarity of claims", source:"guidelines")` → an F-Part section → `get_legal_text_section` returns text.
6. Confirm the server starts in < ~3 s with the DB present, works mid-`embed`, and **stdout carries only MCP JSON-RPC** (no stray logging — including from ONNX Runtime; set its log severity to error/stderr).
7. End-to-end: start `claude` in the folder, approve the server, ask *"Using the EPO tools, what did G 1/19 decide about computer-implemented simulations?"* and check the agent uses the tools and cites correctly.

## 8. Phase 2 — optional enhancements (deferred deliberately)

1. **Cross-encoder reranker** over the fused top-50 (e.g. `bge-reranker-v2-m3` via ONNX, or a hosted rerank API). Biggest single quality jump now that hybrid is in Phase 1.
2. **Bigger/better embeddings** if quality warrants: `multilingual-e5-base`/`-large` (same code, slower + larger), a hosted embeddings API (corpus ≈ 150M tokens ≈ a few dollars with small models; legal-tuned options like Voyage exist — adds an API key + per-query dependency), or re-embedding facts/orders/translations for fuller coverage.
3. **Scale-out search infrastructure** — see §10: Qdrant (Docker) when latency/corpus growth demands HNSW; or Lucene.NET, PostgreSQL + pgvector, Azure AI Search. The tool layer is the stable contract; swap behind `SearchService`/`HybridSearch`.
4. **EN/DE/FR legal synonym expansion** at query time ("inventive step" ⇄ "erfinderische Tätigkeit" ⇄ "activité inventive", "auxiliary request" ⇄ "Hilfsantrag", …) — still useful for the BM25 leg even with embeddings.
5. **MCP resources & prompts:** expose decisions/sections as MCP resources (`epo://decision/T0664-16`) and ship prompt templates ("research question → search strategy"). Tools alone are sufficient for Claude; resources help other clients.
6. **Data refresh command** — the EPO publishes this dataset via its Bulk Data Download Service (free since 2025); `-- index` already rebuilds from a newer XML drop, but a `--download` step could fetch it. Re-running `embed` after a refresh only processes new/changed chunks if chunk hashing is added.
7. **HTTP transport** (`ModelContextProtocol.AspNetCore`) if the server should be shared across machines/users rather than spawned per-session.
8. **Deep links** — construct EPO website/Register URLs from ECLI/application number for "open in browser" answers.

## 9. Risks / gotchas for the implementer

- **Never** load the whole XML into memory; never write logs to stdout in stdio mode (this includes ONNX Runtime's default logger).
- **e5 prefix discipline:** documents embed as `"passage: {text}"`, queries as `"query: {text}"`. Omitting prefixes silently degrades quality. Store the model name + revision in `meta` and refuse to mix vectors from different models.
- **Tokenizer is the highest-risk integration point.** Verify `Microsoft.ML.Tokenizers` against HF reference outputs before building anything on it; fall back to `Tokenizers.DotNet` (loads `tokenizer.json` via the Rust tokenizers binding) if it doesn't load/match.
- **sqlite-vec loading:** `Microsoft.Data.Sqlite` requires `EnableExtensions()` before `LoadExtension(...)`; on macOS pass the full dylib path (don't rely on `DYLD_LIBRARY_PATH`). Pin a sqlite-vec release version. If KNN scan latency at full corpus disappoints, switch `chunks_vec` to `int8[384]` (quantize at insert).
- `ep-legal-ref-presentation` coding must be surveyed empirically (distinct-prefix scan) before writing the normalizer; keep raw values so nothing is lost if normalisation misses a pattern.
- Some bib fields are empty (e.g. `ep-distribution-code` on old D-decisions); all parsing must be null-tolerant. A few decisions cite cases not in the corpus — `get_decision_citations` must distinguish "cites X (not in corpus)" from a broken link.
- PDF text extraction will have artifacts (running headers, soft hyphens). Strip repeated header/footer lines per page and de-hyphenate line-break hyphens before storing/embedding.
- FTS5 `unicode61` does no stemming (deliberate: multilingual corpus). The vector leg compensates for vocabulary mismatch; mention in server `Instructions` that exact-term queries (article numbers, case numbers) and concept queries are both fine.
- The `embed` job must commit in small batches (~256 chunks) and keep `chunks` + `chunks_vec` in sync in the same transaction, so it is resumable and the live server sees consistent data.

## 10. Docker escape hatch (prepared, not default)

Switch to this if: (a) the in-process ONNX/tokenizer route stalls during implementation, (b) hybrid query latency at full corpus is annoying despite int8 quantization, or (c) the corpus/usage outgrows one machine.

`docker-compose.yml` (to be added only if needed):

- **`qdrant/qdrant`** — vectors with HNSW + payload filters; C# client `Qdrant.Client`. Replaces `chunks_vec`; `chunks` table and RRF logic stay.
- **`ghcr.io/huggingface/text-embeddings-inference:cpu-latest`** serving `intfloat/multilingual-e5-small` — replaces in-process ONNX with a plain HTTP call (CPU-only on macOS Docker; same speed as in-process, but removes the tokenizer/ONNX code entirely).

Costs to accept: Docker Desktop must be running for every Claude session; two services to manage; ingestion writes to two stores. Keep SQLite as the system of record either way — Qdrant holds only vectors + ids and can be rebuilt from `chunks` at any time.
