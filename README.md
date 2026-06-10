# EPO Case Law MCP Server

.NET MCP server for searching EPO Boards of Appeal decisions, the Case Law book (11th ed. 2025), and the Guidelines for Examination 2026.

## Setup

```bash
dotnet build EpoCaseLaw -c Release
dotnet run --project EpoCaseLaw -c Release -- index    # ~10–15 min, BM25 search works after
dotnet run --project EpoCaseLaw -c Release -- embed    # resumable; enables hybrid search as stages finish
```

Start Claude Code in this folder — the server loads from `.mcp.json`.
The build step is required: `.mcp.json` starts the server with `--no-build` so that no
MSBuild output can leak onto stdout and corrupt the MCP stdio protocol.

`embed` runs three stages in value order — `essence` (headwords/keywords/catchwords/headnotes),
`book` (Case Law book + Guidelines sections), then `reasons` (the bulk; takes hours on CPU).
Hybrid search switches on automatically for whatever is embedded so far; you can stop after any
stage with `-- embed --stage essence` etc., and re-running resumes where it left off.
Run `dotnet run --project EpoCaseLaw -c Release -- smoke` to verify the index, the embeddings
(including a semantic-discrimination check), and tool latency.

Provisions are canonicalized with era variants collapsed: query `Art. 123(2) EPC`, `Rule 103(1)(a)`,
or `Art. 13(2) RPBA` — EPC 1973/2000 and RPBA 2007/2020 citations all match (sentence-level
qualifiers like `Sent 3` are folded into the article; the raw code is kept alongside).

Long decision texts are paginated. Use `get_decision` with `part: "summary"` first, then
`get_decision_passages` for bounded chunks of `facts`, `reasons`, `order`, or `full`, and
`search_decision_passages` to find targeted passages inside one decision.

## Updating with a new EPO batch

```bash
dotnet run --project EpoCaseLaw -c Release -- index --incremental   # add new decisions, keep embeddings
dotnet run --project EpoCaseLaw -c Release -- embed                 # vectorize only the new chunks
```

Incremental indexing adds decisions with new ECLIs and new language versions; existing rows —
and their embeddings — are untouched, so `embed` only has the new chunks to do. It does not pick
up corrections to already-indexed decision texts; do a full `index` (rebuild) for those.

Other maintenance commands:

- `index --books` — re-extract the Case Law book and Guidelines PDFs (e.g. a new edition) without
  touching decisions; re-run `embed` afterwards for the new book chunks.
- `fix-provisions` — re-normalize provision references stored raw, after normalizer improvements.

## Environment

- `EPO_DB_PATH` — override database file location (default: `epo.db` next to source data)
- `EPO_DATA_ROOT` — override folder containing the XML/PDF source files

## Alternatives

User-scope registration:

```bash
claude mcp add --scope user epo-case-law -- dotnet run --project /path/to/mcp_case_law/EpoCaseLaw -c Release --no-build
```

For faster startup, publish and point `.mcp.json` at the binary:

```bash
dotnet publish EpoCaseLaw -c Release -o publish
# command: ./publish/EpoCaseLaw
```
