# STATUS

_Last updated: 2026-06-23_

## What was built

Backstory v1 — a local-first personal data-export explorer in **C# / .NET 10**. Complete, building,
15 tests passing, benchmark runnable.

- **Solution** (`Backstory.slnx`): Core, Adapters, Storage, Embeddings, Query, Mcp, Cli + Eval + Tests.
- **Adapters**: `TelegramAdapter` (full + single-chat JSON), `GoogleTakeoutAdapter` (Search, YouTube,
  Maps saved places, Semantic Location History). Defensive parsing — a bad/absent file is skipped.
- **Storage**: SQLite via `Microsoft.Data.Sqlite` — events + FTS5 keyword index, entities + aliases,
  `BruteForceVectorStore` (cosine over in-memory float32).
- **Embeddings**: `HashingEmbeddingService` — offline, deterministic, 384-dim, zero assets.
- **Pipeline + query**: `IngestionPipeline` (idempotent via content-hash ids), `HybridSearch`
  (semantic + keyword fused with Reciprocal Rank Fusion, then filtered).
- **CLI**: import / search / timeline / entity / stats / serve / eval (hand-rolled arg parser).
- **MCP server**: `ModelContextProtocol` 1.4.0 over stdio, 5 tools. Verified booting + handling
  initialize/tools-list.
- **Benchmark** (`backstory eval`): **100% ingestion coverage**, **87.5% Recall@5**.

## Decisions made

- **Hashing embedder as the v1 default** instead of ONNX/MiniLM. Rationale: fully offline, zero model
  assets, deterministic, keeps the build/tests/benchmark self-contained. Lexical not deeply semantic;
  MiniLM is a drop-in behind `IEmbeddingService` at the same dimension. **This is the main deviation
  from SPEC §10** — flagged deliberately, not silent.
- **Hand-rolled CLI arg parser** instead of `System.CommandLine` (SPEC §14) — avoids a beta-API
  dependency for a handful of commands. Easy to swap later.
- **Brute-force vectors** (per SPEC) — right for personal scale; `IVectorStore` allows sqlite-vec/HNSW.
- **SQLitePCLRaw bundle pinned to 3.0.3** to clear a security advisory (NU1903) under warnings-as-errors.
- **.NET 10** (installed SDK), `.slnx` solution format.

## Where we left off

Wave 5 complete. Full v1 built end-to-end, verified via CLI smoke test (cross-source ranked search
working) and MCP boot test. Work is on branch `feature/backstory-v1`, **uncommitted** since the Wave 1
scaffold commit — awaiting the user's call on committing.

## What's next

1. **ONNX/MiniLM embedder** — biggest quality lever; raises semantic recall. Same interface.
2. **MCP server lifecycle** — exit cleanly on stdin EOF (currently lingers until killed).
3. **More Takeout types** (Gmail, Calendar, Photos metadata) and **more adapters** (Instagram, Spotify).
4. **"What do they know about you" audit report** and **"your life in <period>"** views (roadmap pull).
5. Smarter cross-source entity resolution (v1 is deterministic alias/name/phone matching).

## Gotchas

- **Google Location History** is often thin/absent in Takeout now (moved on-device) — don't anchor
  demos on `location_visit`.
- **Telegram message text** can be a string or an array of parts; `JsonX.FlattenText` handles both.
- **Maps saved places** often lack a date → emitted as Place *entities* only (no timeline event).
- **MCP stdout** is block-buffered when redirected; to see responses in a manual test, drive it with a
  real MCP client rather than piping + killing.
- The one Recall@5 miss is an ambiguous gold-marker ("Tokyo" appears in 3 fixtures), not a search bug.
