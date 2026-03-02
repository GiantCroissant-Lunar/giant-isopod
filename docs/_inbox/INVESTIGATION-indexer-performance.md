# Investigation: Codebase Indexer Performance Issues

**Date:** 2026-03-02
**Context:** Session 9 — salvaging stuck worktree `intelligent-ritchie`

## Summary

Running `memory-sidecar index project/` appeared to hang indefinitely, producing no output
after the initial "Indexing project/ → data/memory/codebase.sqlite" line. The process consumed
up to 4.6 GB RAM and never wrote data to the SQLite DB. Three root causes were identified.

## Root Cause 1: `Path.rglob("*")` traverses excluded directories

**Severity:** High — causes multi-minute stalls before any indexing begins.

`index_codebase()` in `flows/codebase.py:79` uses:

```python
files = sorted(p for p in source_root.rglob("*") if p.is_file() and _should_include(p, source_root))
```

`rglob("*")` visits **every file in every subdirectory** before `_should_include` filters them.
The `project/` tree contains large excluded subtrees:

| Directory | Contents |
|-----------|----------|
| `.godot/` | ~15,000+ compiled resources, imported assets |
| `addons/` | Third-party Godot plugins (phantom_camera, debug_draw_3d, gut) |
| `bin/`, `obj/` | .NET build artifacts |

Even though `_should_include` rejects these files, `rglob` still stat()s every one. On Windows
with NTFS, this is particularly slow due to per-file metadata lookups.

**Fix:** Replace `rglob` with `os.walk` + in-place directory pruning:

```python
for dirpath, dirnames, filenames in os.walk(source_root):
    dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS and not d.startswith(".")]
    for fname in filenames:
        if Path(fname).suffix.lower() in CODE_EXTENSIONS:
            files.append(Path(dirpath) / fname)
```

This reduced file discovery from ~60+ seconds to <1 second for the same tree.

## Root Cause 2: Unbounded batch accumulation from large files

**Severity:** Medium — causes OOM or extremely long embedding calls.

The batching logic accumulates chunks until `len(pending_texts) >= batch_size` (default 32),
then flushes. However, a single large file can produce far more chunks than the batch size:

| File | Size | Chunks (1000-char, 300-overlap) |
|------|------|---------------------------------|
| `dd3d_cs_api.generated.cs` | 201 KB | ~287 |
| `PhantomCamera3D.cs` | 19 KB | ~27 |
| `AgentActor.cs` | 16 KB | ~22 |

When a 287-chunk file is processed, all 287 chunks are appended to `pending_texts` before the
`>= batch_size` check triggers. The resulting `embed_texts(287_texts)` call takes ~40 seconds
and uses significant memory for the ONNX runtime's intermediate buffers.

Worse, if the previous batch already had some pending items, the total can exceed 300 chunks
in a single embedding call — potentially enough to trigger OOM on constrained systems.

**Fix options:**
1. Flush inside the chunk accumulation loop, not just at file boundaries
2. Cap `embed_texts` to process in sub-batches internally
3. Add a hard ceiling: `if len(pending_texts) >= batch_size * 4: flush()`

## Root Cause 3: `addons/` directory contains large generated code

**Severity:** Medium — indexing third-party code wastes time and pollutes search results.

The `addons/` directory contains Godot community plugins that are:
- Third-party code (not useful for agent knowledge retrieval)
- Generated API bindings (`dd3d_cs_api.generated.cs` at 201 KB — the single largest file)
- Example scenes and documentation (noise, tweening, limit examples)

Including addons inflated the file count from 107 → 203+ and chunk count proportionally.
The 201 KB generated file alone accounted for ~65% of the stall time.

**Fix applied:** Added `addons` to `EXCLUDED_PATTERNS` in `config.py`. Result:
- Files: 203 → 107
- Chunks: ~800+ → 441
- Index time: hung indefinitely → ~45 seconds

## Root Cause 4: `upsert_code_chunk` fails on re-index with existing vec0 data

**Severity:** Low — only affects re-indexing an existing DB.

`storage.py:139-142` uses `INSERT OR REPLACE INTO code_chunks_vec` for the sqlite-vec virtual
table. However, `vec0` virtual tables do not support `INSERT OR REPLACE` the same way as
regular tables — they raise `UNIQUE constraint failed` when a row with the same primary key
already exists.

This means re-running `memory-sidecar index` on an existing DB fails partway through. The
workaround is to delete the DB and re-index from scratch.

**Fix needed:** Use a two-step approach:
```python
conn.execute("DELETE FROM code_chunks_vec WHERE id = ?", (row_id,))
conn.execute("INSERT INTO code_chunks_vec (id, embedding) VALUES (?, ?)", (row_id, blob))
```

## Observed Behavior Timeline

| Time | Event |
|------|-------|
| T+0s | Process starts, prints "Indexing project/ → ..." |
| T+0s–T+60s | `rglob("*")` traverses entire tree including `.godot/` (no output) |
| T+60s–T+120s | FastEmbed model download/load on first run (~130 MB ONNX model) |
| T+120s+ | Embedding begins; first batches complete in 1-3s each |
| T+~180s | Hits `dd3d_cs_api.generated.cs` — single batch of ~287 chunks |
| T+~220s | Batch completes but process appears to hang (no progress output between batches) |
| T+300s+ | Either OOM kill or stall on next large file batch |

## Recommendations

| Priority | Action | Impact |
|----------|--------|--------|
| **Done** | Exclude `addons/` from indexing | 50% fewer files, eliminated OOM trigger |
| **Done** | Fix FTS5 rebuild on every schema init | Prevents O(n) rebuild on each connection |
| High | Replace `rglob` with `os.walk` + pruning in `codebase.py` | 60x faster file discovery |
| High | Fix `INSERT OR REPLACE` for vec0 re-indexing | Enable incremental re-index |
| Medium | Add progress output to `index_codebase()` | Visibility into long runs |
| Medium | Cap batch size to prevent single large-file OOM | Robustness |
| Low | Skip `.generated.cs` files by convention | Avoid indexing codegen output |

## Measurements

After fixes (excluding addons, fresh DB):

```
Found 107 files to index
  [7/107]   18 chunks (2.3s)
  [14/107]  35 chunks (1.5s)
  [27/107]  51 chunks (1.4s)
  ...
  [105/107] 432 chunks (1.2s)
  [final]   441 chunks (0.6s)
Total: 107 files, 441 chunks, ~45 seconds
```

Codebase search verification:
```
$ memory-sidecar search "actor system" --db data/memory/codebase.sqlite
[0.290] plugins/Plugin.Actors/KnowledgeSupervisorActor.cs
[0.281] plugins/Plugin.Actors/AgentWorldSystem.cs
[0.261] plugins/Plugin.Actors/AgentWorldSystem.cs
[0.254] plugins/Plugin.Actors/AgentActor.cs
```
