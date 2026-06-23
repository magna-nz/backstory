# Backstory — SPEC

> **Status:** Draft v1 (awaiting sign-off before planning)
> **Last updated:** 2026-06-23

---

## 1. One-liner

Point Backstory at your Google Takeout and Telegram exports and get one local, searchable timeline of your own life — queryable in natural language or over MCP. Zero cloud, zero API calls.

## 2. Problem

Every major service now offers "download your data," but what comes back is an unbrowsable pile of JSON/CSV/HTML in a hundred different shapes that no human actually reads. Your most personal data is technically in your hands and practically inaccessible.

The only comparable prior tool — [Dogsheep](https://dogsheep.github.io/) — is a fragmented family of per-service command-line importers: Apple/Twitter-centric, no semantic search, no cross-source joins, and expert-only. It's a weak, semi-dormant incumbent in a category that matters.

Because this is the most sensitive data a person owns, **local-first is the premise, not a feature.** Nobody sane uploads their full Takeout to a cloud service for indexing.

## 3. Goals / Non-goals

### Goals (v1)
- Ingest **Google Takeout** and **Telegram** exports into one normalized local store.
- Provide **hybrid search** (semantic + full-text + structured filters) over a unified timeline.
- Expose the store over **MCP** so any agent can answer questions about the user's life.
- Run **100% locally** — no network calls at query/ingest time, no telemetry.
- Ship with a **reproducible benchmark** (ingestion coverage + retrieval recall) as the credibility hook, mirroring how MemPalace led with its R@5 number.
- Make adapters a **clean, extensible interface** so new sources can be added without touching the core.

### Non-goals (v1)
- No GUI (CLI + MCP only).
- No cloud, sync, accounts, or hosted version.
- No **live API pulling** from services — Backstory works on *exports* by design (live API wrappers are a saturated, commodity space).
- No data write-back or editing of source data.
- No advanced cross-source entity resolution — v1 keeps resolution to simple deterministic matching (name / email / phone / geo proximity).
- No WhatsApp adapter in v1 (deliberately deferred; see §13).

## 4. Target users

Technical / prosumer users initially — same audience profile as MemPalace. Distributed as a self-contained CLI + MCP server. A GUI and broader audience are post-v1 considerations, not v1 gates.

## 5. Differentiation (why this beats Dogsheep)

| | Dogsheep | Backstory |
|---|---|---|
| Sources | Per-service silos | Unified store + adapter SDK |
| Search | Keyword (Datasette) | Hybrid: semantic + keyword + filters |
| Cross-source | None | Shared timeline + entity resolution |
| Agent access | None | First-class MCP server |
| Benchmark | None | Ingestion coverage + retrieval R@5 |
| Approachability | Expert-only, Python CLIs | Single binary, simple commands |

The moat is the **unified cross-source timeline + entity resolution** — the thing Dogsheep can't do and the thing an agent can't do ad-hoc against raw export files.

## 6. v1 scope — two adapters

### 6.1 Google Takeout (anchor source)
The richest single export. v1 ingests a curated subset of data types; the adapter is built so more types slot in later.

| Data type | Source path (approx.) | Event subtype | Notes |
|---|---|---|---|
| Search history | `My Activity/Search/MyActivity.json` | `search_query` | May be HTML in some exports — handle both. |
| Maps saved places | `Maps (your places)/Saved.json` + `Saved/*.csv` | `maps_save` | Coordinates appear as a geometry array **or** lat/lon fields — handle both. Creates place entities. |
| Semantic Location History | `Location History/Semantic Location History/YYYY/YYYY_MONTH.json` | `location_visit` | **Caveat:** Google moved Timeline on-device recently; this may be thin/absent for many accounts. Do not depend on it. |
| YouTube history | `YouTube and YouTube Music/history/watch-history.json` | `youtube_watch` | Watch + search history. |

### 6.2 Telegram (clean structured source)
Telegram Desktop → Export chat history → JSON produces `result.json`.

| Data type | Source | Event subtype | Notes |
|---|---|---|---|
| Messages | `result.json` → chats → messages | `telegram_message` | Text, sender, timestamp, reply links. |
| Contacts | `result.json` → contacts | (entities) | Creates person entities. |
| Chats | `result.json` → chats list | (entities) | Group/channel as org entities. |

## 7. Core data model

Two normalized shapes. Original records are kept **verbatim** on disk and referenced, never discarded (lossless principle borrowed from MemPalace).

```csharp
// One thing that happened, at a time, from a source.
record Event {
    string Id;                 // content hash (stable, dedupe-friendly)
    DateTimeOffset Timestamp;  // UTC
    string Source;             // "google_takeout" | "telegram"
    string SubType;            // "search_query" | "maps_save" | "telegram_message" | ...
    string Text;               // searchable content
    double? Latitude;
    double? Longitude;
    IReadOnlyList<string> ActorIds;  // entity ids of people involved
    string? PlaceId;           // entity id of a place
    RawRef Raw;                // pointer to the original blob
    JsonElement Metadata;      // source-specific extras, untyped
}

// A person, place, or org referenced across events.
record Entity {
    string Id;
    EntityKind Kind;           // Person | Place | Org
    string CanonicalName;
    IReadOnlyList<string> Aliases;
    JsonElement Metadata;      // phone/email for people, coords for places, etc.
}

// Pointer back to the untouched source.
record RawRef {
    string FilePath;           // path within the import store
    string Locator;            // json-pointer / line number / offset
}
```

## 8. Architecture

Pipeline (mess is quarantined at the edges so everything downstream works on one clean schema):

```
exports → adapters → normalizer → entity resolution → store → hybrid query → CLI + MCP
```

1. **Adapters** — one per source. Tiny interface: detect + parse. Each adapter fully encapsulates one export's quirks (locale dates, coordinate-format variance, HTML-vs-JSON) and emits normalized `Event`/`Entity` records. Adding a source = one new adapter, nothing else changes.
2. **Normalizer / ingestion** — assigns content-hash ids, dedupes, attaches `RawRef`, persists raw blobs.
3. **Entity resolution** — links the same person/place across sources. v1: deterministic (exact name/email/phone match; geo proximity for places). Pluggable for smarter resolution later.
4. **Store** — three stores, one job each:
   - **SQLite + FTS5** — timeline, structured filters, keyword search.
   - **Vector store** — embeddings for semantic search (see §10).
   - **Raw blob store** — untouched originals on disk.
5. **Hybrid query** — fuses vector similarity + FTS keyword + structured filters (date/source/entity), with optional re-rank.
6. **Interfaces** — CLI for the user, MCP server for agents.

## 9. Storage design

- **Engine:** SQLite via `Microsoft.Data.Sqlite`.
- **Tables:** `events`, `entities`, `event_actors` (join), `raw_blobs` (or files on disk referenced by path), `embeddings`, plus an FTS5 virtual table `events_fts` over `Event.Text`.
- **Vectors:** stored as `BLOB` (float32 array) alongside `events`. See §10 for search strategy.
- **One database file per "vault"** (default `~/.backstory/backstory.db`), config-overridable.

## 10. Embeddings & vector search

- **Local embeddings via ONNX Runtime** (`Microsoft.ML.OnnxRuntime`) running a sentence-transformer model (default: `all-MiniLM-L6-v2`, 384-dim). No API calls. Model bundled or downloaded once on first run (the only optional network access — documented and opt-in).
- **`IEmbeddingService` abstraction** so the model is swappable (e.g. a larger multilingual model later).
- **Search strategy:** v1 uses **brute-force cosine** over in-memory float32 vectors. At personal scale (one person's exports ≈ 10k–1M vectors) this is milliseconds and removes a dependency. An `IVectorStore` abstraction allows dropping in `sqlite-vec` or an HNSW index if scale ever demands it.

## 11. Interfaces

### 11.1 CLI (`System.CommandLine`)
```
backstory import <path> [--source auto|google_takeout|telegram]
backstory search "<query>" [--from <date>] [--to <date>] [--source <s>] [--limit <n>]
backstory timeline [--from <date>] [--to <date>] [--source <s>]
backstory entity "<name>"
backstory serve                 # start MCP server (stdio)
backstory stats                 # counts per source, coverage summary
backstory eval [--set <path>]   # run the benchmark harness
```

### 11.2 MCP server (`ModelContextProtocol` NuGet, stdio)
Tools exposed to agents:
- `search_timeline(query, from?, to?, source?, limit?)` → ranked events
- `get_events(ids[])` → full event records incl. raw reference
- `lookup_entity(name)` → entity + linked events
- `summarize_period(from, to, source?)` → events in range for the agent to summarize
- `list_sources()` / `stats()` → what's ingested

## 12. Success metrics / benchmark (the credibility hook)

Two reproducible numbers, published in the README:

1. **Ingestion coverage** — per adapter, `records_emitted / records_present_in_source`, measured against fixture exports with known counts. Surfaces silent data loss. Target ≥ 95%.
2. **Retrieval Recall@5** — a hand-built personal-data eval set of `(natural-language question → gold event id(s))` pairs; measure R@5 of the hybrid query. This is the headline number, MemPalace-style.

The `backstory eval` command runs both against fixtures and prints the scores. The eval set and fixtures live in the repo.

## 13. Privacy & security principles

- **Local-only by default.** No network calls during import, search, or serve. The single exception is an optional, one-time, opt-in embedding-model download, clearly documented.
- **No telemetry, ever.**
- Raw data and database stay in a user-controlled directory.
- This is a deliberate, load-bearing constraint — it is the product's reason to exist, not a setting.

## 14. Proposed tech stack

- **.NET 10**, C#
- `System.CommandLine` — CLI
- `System.Text.Json` (source-generated) — fast, AOT-friendly parsing
- `Microsoft.Data.Sqlite` + FTS5 — storage, keyword search
- `Microsoft.ML.OnnxRuntime` — local embeddings
- `ModelContextProtocol` — MCP server
- `xUnit` — tests
- Distribution: single self-contained binary (native AOT a stretch goal)

## 15. Proposed solution structure

```
Backstory.sln
  src/
    Backstory.Core/         # Event, Entity, interfaces (ISourceAdapter, IEmbeddingService, IVectorStore)
    Backstory.Adapters/     # GoogleTakeoutAdapter, TelegramAdapter
    Backstory.Storage/      # SQLite repos, FTS, blob store, vector store
    Backstory.Embeddings/   # ONNX embedding service
    Backstory.Query/        # hybrid search
    Backstory.Mcp/          # MCP server + tools
    Backstory.Cli/          # System.CommandLine entrypoint
  tests/
    Backstory.Tests/        # xUnit
  eval/
    Backstory.Eval/         # benchmark harness
    fixtures/               # sample exports + gold eval set
```

## 16. Out of scope (v1) — explicit

GUI; cloud/sync/accounts; live API pulling; data write-back; WhatsApp adapter; ML-based entity resolution; mobile; multi-user.

## 17. Future roadmap (post-v1)

- More Takeout data types (Gmail, Calendar, Photos metadata, Chrome history).
- More adapters: Instagram/Meta, Spotify, WhatsApp, bank CSVs, ChatGPT/Claude history.
- **"What do they know about you" audit report** — high first-run payoff.
- **"Your life in [period]"** nostalgic timeline view.
- Smarter cross-source entity resolution.
- Optional GUI.

## 18. Open questions / risks

- **Format churn** — export schemas drift; adapters rot. Mitigation: adapter SDK + fixture-based coverage tests catch regressions early. This is the core maintenance tax and the main reason the category is underserved.
- **Stickiness** — exports are a one-time curiosity dump. The MCP agent Q&A is the intended retention hook; the audit/nostalgia views (roadmap) are the stronger long-term pull.
- **Location History availability** — Google's on-device Timeline shift means this data type may be empty for many users; don't anchor demos on it.
- **Embedding model size vs. quality** — MiniLM is small/fast but English-leaning; multilingual messages (Telegram) may need a larger model. Pluggable interface defers this decision.
```
