# STATUS

_Last updated: 2026-06-23_

## What was built

Backstory v1 — a local-first personal data-export explorer in **C# / .NET 10**. Complete, building,
**23 tests passing**, benchmark runnable, published at https://github.com/magna-nz/backstory.

- **Solution** (`Backstory.slnx`): Core, Adapters, Storage, Embeddings, Query, Mcp, Cli + Eval + Tests.
- **Adapters**: `TelegramAdapter` (full + single-chat JSON), `GoogleTakeoutAdapter` (Search, YouTube,
  Maps saved places, Semantic Location History). Defensive parsing.
- **Storage**: SQLite — events + FTS5 keyword index, entities + aliases, `BruteForceVectorStore`.
- **Embeddings**: two services behind `IEmbeddingService` (both 384-dim) —
  - `HashingEmbeddingService` (default, offline, deterministic, zero assets)
  - `OnnxEmbeddingService` (all-MiniLM-L6-v2 via ONNX Runtime + BERT tokenizer, mean-pooled),
    selected automatically by `EmbeddingFactory` when the model is present.
  - `ModelDownloader` + `backstory model fetch` (opt-in ~90 MB download — the only network access).
- **Pipeline + query**: `IngestionPipeline` (idempotent), `HybridSearch` (semantic + keyword via RRF).
- **CLI**: import / search / timeline / entity / stats / serve / eval / model fetch / fetch / watch.
- **Onboarding**: `fetch google|telegram` (guided export instructions + opens the page) and `watch`
  (FileSystemWatcher auto-imports a `result.json` or Takeout `.zip` as it lands in ~/Downloads;
  zips are auto-extracted). `import` now also accepts a `.zip`. Multi-part Takeout zips
  (`-001.zip`, `-002.zip`, …) are grouped and merged into one import. Verified live end-to-end.
- **README** rewritten in plain language (no em-dashes), leading with a "What it can do" list.
- **MCP server**: `ModelContextProtocol` 1.4.0 over stdio, 5 tools.
- **Benchmark** (`backstory eval`):
  - Hashing: **100% coverage, 87.5% Recall@5**
  - ONNX MiniLM: **100% coverage, 100% Recall@5** (validated end-to-end with a real model download)

## Decisions made

- **Two embedders, factory-selected.** Hashing is the zero-setup default so the project works offline
  with no assets; ONNX MiniLM is the opt-in quality upgrade (`model fetch`). Same 384-dim interface,
  so they're interchangeable. ONNX inference verified live — R@5 went 87.5% → 100%.
- **Hand-rolled CLI arg parser** instead of `System.CommandLine` — avoids a beta dependency.
- **Brute-force vectors** — right for personal scale; `IVectorStore` allows sqlite-vec/HNSW later.
- **SQLitePCLRaw pinned to 3.0.3** to clear a security advisory (NU1903) under warnings-as-errors.
- **Model source**: model.onnx from `Xenova/all-MiniLM-L6-v2`, vocab from `sentence-transformers/...`.
- **.NET 10**, `.slnx` solution format.

## Where we left off

ONNX embedder built, wired into CLI/MCP/eval, and validated against a live model download. README +
STATUS updated with the two-embedder benchmark. All on `main`, pushed to GitHub. Building the
technical docs site for GitHub Pages next.

## What's next

1. **MCP server lifecycle** — exit cleanly on stdin EOF (currently lingers until killed).
2. **More Takeout types** (Gmail, Calendar, Photos metadata) and **more adapters** (Instagram, Spotify).
3. **"What do they know about you" audit report** and **"your life in <period>"** views.
4. **Batched ONNX inference** during import for speed (currently one text at a time).
5. Smarter cross-source entity resolution (v1 is deterministic alias/name/phone matching).

## Gotchas

- **Google Location History** is often thin/absent in Takeout now (moved on-device).
- **Telegram message text** can be a string or an array of parts; `JsonX.FlattenText` handles both.
- **ONNX model** lives at `~/.backstory/models/all-MiniLM-L6-v2/`; delete it to fall back to hashing.
  Re-import after fetching to re-embed existing events with the semantic model.
- **MCP stdout** is block-buffered when redirected; drive it with a real MCP client to see responses.
- Gemini reviews were skipped throughout — no `GEMINI_API_KEY` set.
