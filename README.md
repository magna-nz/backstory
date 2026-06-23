# Backstory

> Your data exports, finally searchable — and entirely on your machine.

Point Backstory at your **Google Takeout** and **Telegram** exports and it builds one local,
searchable timeline of your own life. Query it in natural language from the CLI, or wire it into any
agent over **MCP**. No cloud, no accounts, no API calls.

This is the most personal data you own, so local-first isn't a feature here — it's the whole point.

## Why

Every service offers "download your data," but what comes back is an unbrowsable pile of JSON/CSV.
[Dogsheep](https://dogsheep.github.io/) is the closest prior art, but it's fragmented per-service
CLIs with no semantic search and no cross-source joins. Backstory unifies the exports, adds hybrid
semantic + keyword search, resolves people and places across sources, and exposes it all to agents.

## Benchmark

Run `backstory eval` (or `dotnet run --project eval/Backstory.Eval`) to reproduce:

| Embedder | Ingestion coverage | Recall@5 |
|---|---|---|
| Hashing (default, offline, zero setup) | **100%** | **87.5%** |
| ONNX MiniLM (after `backstory model fetch`) | **100%** | **100%** |

The semantic model resolves paraphrases the lexical embedder can't — e.g. *"japan vacation"* finds
*"flight to Tokyo"* with no shared words.

## Install / build

Requires the **.NET 10 SDK**.

```bash
dotnet build Backstory.slnx
dotnet test  Backstory.slnx
```

## Usage

```bash
# Import an export (adapter auto-detected)
backstory import ~/Downloads/telegram-export/result.json
backstory import ~/Downloads/Takeout

# Search your timeline
backstory search "dinner plans with sarah"
backstory search "trip to japan" --from 2023-01-01 --source telegram

# Browse and inspect
backstory timeline --limit 20
backstory entity "Sarah K"
backstory stats

# Upgrade to semantic embeddings (one-time, opt-in ~90 MB download)
backstory model fetch
# then re-import your exports to re-embed them with the semantic model

# Run the benchmark
backstory eval
```

The vault lives at `$BACKSTORY_DB` or `~/.backstory/backstory.db`.

## Use it from an agent (MCP)

```bash
backstory serve   # speaks MCP over stdio
```

Register it with an MCP client (e.g. Claude Desktop / Claude Code):

```json
{
  "mcpServers": {
    "backstory": { "command": "backstory", "args": ["serve"] }
  }
}
```

Tools exposed: `search_timeline`, `get_events`, `lookup_entity`, `summarize_period`, `list_sources`.

## How it works

The mess of each export format is quarantined inside a per-source **adapter**; everything downstream
works on one normalized `Event` / `Entity` model. Storage is SQLite (timeline + FTS5 keyword search)
plus a brute-force cosine vector index. Search fuses semantic and keyword hits via Reciprocal Rank
Fusion. See [SPEC.md](SPEC.md) for the full design.

```mermaid
flowchart TD
    TG["Telegram<br/>result.json"]:::src
    GT["Google Takeout<br/>JSON / CSV"]:::src

    TG --> AD
    GT --> AD

    AD["Source adapters<br/><i>detect · parse · normalize</i>"]:::ingest
    NR["Normalizer<br/><i>Event + Entity schema</i>"]:::ingest
    ER["Entity resolution<br/><i>link people &amp; places</i>"]:::ingest
    AD --> NR --> ER

    ER --> FTS[("SQLite + FTS5<br/>timeline · keyword")]:::store
    ER --> VEC[("Vector index<br/>cosine")]:::store
    ER --> RAW[("Raw blobs<br/>verbatim")]:::store

    FTS --> HQ
    VEC --> HQ
    HQ["Hybrid query<br/><i>semantic + keyword + filters</i>"]:::query

    HQ --> CLI["CLI"]:::iface
    HQ --> MCP["MCP server → your agent"]:::iface

    classDef src fill:#FAECE7,stroke:#993C1D,color:#4A1B0C;
    classDef ingest fill:#EEEDFE,stroke:#534AB7,color:#26215C;
    classDef store fill:#E1F5EE,stroke:#0F6E56,color:#04342C;
    classDef query fill:#E1F5EE,stroke:#0F6E56,color:#04342C;
    classDef iface fill:#F1EFE8,stroke:#5F5E5A,color:#2C2C2A;
```

A single search fuses the semantic and keyword retrievers — no score calibration needed, since
Reciprocal Rank Fusion ranks by position:

```mermaid
sequenceDiagram
    participant A as Agent / CLI
    participant H as HybridSearch
    participant E as Embedder
    participant V as Vector index
    participant F as FTS5
    A->>H: search("dinner with sarah")
    H->>E: embed(query)
    E-->>H: query vector
    par semantic
        H->>V: nearest vectors
        V-->>H: semantic hits
    and keyword
        H->>F: keyword match (terms OR-ed)
        F-->>H: keyword hits
    end
    H->>H: fuse (RRF) + apply filters
    H-->>A: ranked events (cross-source)
```

## Supported sources (v1)

- **Google Takeout** — Search history, YouTube history, Maps saved places, Semantic Location History
- **Telegram** — messages, contacts (Telegram Desktop JSON export)

Adding a source means implementing one `ISourceAdapter`. Instagram, Spotify, WhatsApp and more are on
the roadmap.

## Embeddings

Backstory ships two embedders behind one `IEmbeddingService` interface (both 384-dim, so they're
interchangeable):

- **Hashing** (default) — dependency-free, fully offline, deterministic, zero model assets. Lexical:
  it matches shared words/characters. Everything works out of the box with this.
- **ONNX MiniLM** (`all-MiniLM-L6-v2`) — true semantic embeddings run locally via ONNX Runtime. Run
  `backstory model fetch` once (~90 MB) and it's selected automatically. Matches *meaning*, not just
  words, which is what lifts Recall@5 to 100% on the benchmark.

Switching is just fetching the model; for a multilingual corpus, drop in a multilingual MiniLM — same
code.

## Privacy

100% local. No telemetry. The only network access ever contemplated is an optional, opt-in, one-time
embedding-model download for the ONNX upgrade — never your data.
