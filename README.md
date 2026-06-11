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

## Remote HTTP mode (Copilot Studio / M365 Copilot)

M365 Copilot consumes MCP servers over Streamable HTTP at a publicly reachable HTTPS endpoint;
it cannot launch local stdio processes. `serve-http` runs the same 8 tools over HTTP without
touching the stdio default:

```bash
dotnet build EpoCaseLaw -c Release
export EPO_MCP_API_KEY=$(openssl rand -hex 32) && echo "$EPO_MCP_API_KEY"
dotnet run --project EpoCaseLaw -c Release --no-build -- serve-http --port 5234
```

- The MCP endpoint is `http://localhost:5234/mcp` (Streamable HTTP, stateless). Requests must
  carry the API key in the `x-api-key` header; anything else gets a 401.
- The server refuses to start if `EPO_MCP_API_KEY` is unset. `--no-auth` disables the check for
  local testing only — never use it behind a tunnel.
- `GET /health` is unauthenticated and reports `{ status, db }` for tunnel/Azure probes.
- `--port <n>` changes the port (default 5234); `ASPNETCORE_URLS`, if set, wins (e.g. in Docker).
- Quick check (the dual `Accept` header is mandatory; single-event `text/event-stream`
  responses are normal for the stateless transport):

```bash
curl -s http://localhost:5234/mcp -H "x-api-key: $EPO_MCP_API_KEY" \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

Or use MCP Inspector: `npx @modelcontextprotocol/inspector`, transport "Streamable HTTP",
URL `http://localhost:5234/mcp`, custom header `x-api-key`.

### Expose with a dev tunnel

Dev Tunnels keep a stable URL across restarts (ngrok free rotates URLs, which breaks the
registered Copilot Studio tool):

```bash
devtunnel user login
devtunnel create epo-mcp --allow-anonymous
devtunnel port create epo-mcp -p 5234
devtunnel host epo-mcp        # → https://<id>-5234.<region>.devtunnels.ms
```

`--allow-anonymous` makes the URL world-reachable — the API key is the only gate. Rotate the
key after testing. Verify `tools/list` against `https://<tunnel-host>/mcp` before touching
Copilot Studio.

### Register in Copilot Studio

1. Open or create an agent (generative orchestration).
2. **Tools → + Add a tool → New tool → Model Context Protocol**.
3. Server URL `https://<tunnel-host>/mcp`, Authentication **API key**, header name **`x-api-key`**.
4. Create the connection with your key, then **Add to agent**.
5. Test in the test pane (e.g. "Find decisions on added subject-matter under Art. 123(2) EPC").
6. **Publish** → Channels → **Teams and Microsoft 365 Copilot** (tenant admin approval may be
   needed in the M365 admin center → Integrated apps).

M365 Copilot truncates large tool outputs; the defaults (limit ≤ 25, `max_chars` ≤ 20k) are
fine — prefer `get_decision_passages` over `part: "full"` for long decisions.

### Azure Container Apps (Phase B)

The repo includes a `Dockerfile` that bakes `data/epo.db`, `data/models/`, and
`data/native/vec0.so` into an `aspnet:10.0` image (the corpus is read-only and versioned, so
an immutable image per corpus release beats SQLite over an Azure Files mount). Outline:

```bash
az acr build -r <registry> -t epo-mcp:1 .     # cloud build avoids uploading multi-GB layers twice
az containerapp create ... --min-replicas 1 --cpu 2 --memory 4Gi \
  --secrets epo-mcp-api-key=<key> --env-vars EPO_MCP_API_KEY=secretref:epo-mcp-api-key
```

Use `--min-replicas 1` to keep the image pulled and the ONNX model warm, point liveness/readiness
probes at `/health`, then repoint the Copilot Studio tool URL at the Container Apps host.

## Environment

- `EPO_DB_PATH` — override database file location (default: `epo.db` next to source data)
- `EPO_DATA_ROOT` — override folder containing the XML/PDF source files (honored even when no
  source XML is present, e.g. a container shipping only `epo.db` + `models/`)
- `EPO_MCP_API_KEY` — required by `serve-http`; clients send it in the `x-api-key` header

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
