# Handover — Session 9: Tree-sitter AST Chunking & Indexer Hardening

## What was done

PR #13 squash-merged to main — added tree-sitter AST-aware code chunking, fixed indexer performance and correctness issues, added chunker versioning with automatic vec0 purge, and documented the full memory sidecar architecture.

### Changes

1. **Extract chunking module**: Moved `_split_simple` out of `codebase.py` into standalone `chunking.py` with `split_simple`, `split_ast`, and `chunk_file` dispatch. Fixed infinite loop bug when `chunk_overlap >= chunk_size` caused backward progress.

2. **os.walk with directory pruning**: Replaced `rglob("*")` + post-filter with `os.walk` that prunes excluded directories in-place (`dirnames[:] = ...`). Prevents descending into `node_modules`, `bin`, `obj`, `addons`, `.git`, etc. Fixes the performance hang reported during codebase indexing.

3. **EXCLUDED_PATTERNS fixes**: Removed dead `".*"` entry (hidden dirs handled by `startswith(".")`). Replaced multi-component `"build/_artifacts"` with `"_artifacts"` (multi-part paths never match a single path part in `rel.parts`).

4. **Tree-sitter AST-aware chunking**: New `split_ast()` uses `tree-sitter-language-pack` to parse source files and chunk at AST boundaries (functions, classes, imports). Groups consecutive nodes up to `chunk_size`, keeping definitions intact. Single definitions exceeding `chunk_size` get their own chunk rather than being split mid-AST-node.

   Supported: Python, Rust, TypeScript, TSX, JavaScript, GDScript, Markdown, JSON, TOML, YAML.
   Fallback: C# (no tree-sitter-language-pack binding) and unknown languages use `split_simple`.

5. **Chunker version tracking**: New `metadata` table in SQLite stores `chunker_version`. On `index_codebase`, if stored version differs from `CHUNKER_VERSION` in `chunking.py`, all `code_chunks` + `code_chunks_vec` rows are purged before re-indexing. Prevents orphaned vector entries when switching chunking strategies.

6. **Input validation**: Added `_validate_chunk_params` rejecting `chunk_size <= 0` and `chunk_overlap < 0` upfront.

7. **AST node filtering safety**: `_collect_top_level_nodes` now includes all named nodes as fallback, not just whitelisted types, preventing silent content loss.

8. **Hidden file exclusion**: `_walk_source_files` now uses `_should_include` (which checks filename parts for `.` prefix) instead of extension-only filtering.

9. **Purge safety**: `purge_all_code_chunks` always clears `code_chunks_vec` even when `code_chunks` is empty, preventing orphan vectors.

10. **Documentation**: Added `docs/memory-sidecar.md` covering the 4-layer memory model, indexing pipeline, hybrid search, pre-task retrieval loop, post-task write-back, storage schemas, and CLI usage.

### Files modified/added

| File | Change |
|------|--------|
| `memory-sidecar/src/memory_sidecar/chunking.py` | New — split_simple, split_ast, chunk_file, validation |
| `memory-sidecar/src/memory_sidecar/flows/codebase.py` | Rewired to use chunk_file, os.walk pruning, version check |
| `memory-sidecar/src/memory_sidecar/storage.py` | Added metadata table, get/set_metadata, purge_all_code_chunks |
| `memory-sidecar/src/memory_sidecar/config.py` | Fixed EXCLUDED_PATTERNS |
| `memory-sidecar/pyproject.toml` | Added tree-sitter-language-pack dep |
| `memory-sidecar/tests/test_chunking.py` | New — 36 tests (validation, simple, AST, dispatch) |
| `memory-sidecar/tests/test_codebase.py` | Refactored — removed split_simple tests, added walk tests |
| `memory-sidecar/tests/test_storage.py` | Added metadata and purge tests |
| `docs/memory-sidecar.md` | New — architecture and usage guide |

### Commits (squashed into one on merge)

1. `refactor(memory): extract chunking module and fix infinite loop bug`
2. `fix(memory): replace rglob with os.walk pruning and fix EXCLUDED_PATTERNS`
3. `feat(memory): add tree-sitter AST-aware chunking for codebase indexing`
4. `fix(memory): purge stale vec0 entries on chunker version change`
5. `docs(memory): add memory sidecar architecture and usage guide`
6. `fix(memory): address PR review — validation, node filtering, hidden files, purge safety`

## Current state

- **Branch**: all merged to `main` (squash commit `e48e3ce`)
- **Tests**: 92 passing across 4 test files
- **Worktrees**: `tree-sitter-chunking` worktree still exists at `giant-isopod-worktrees/tree-sitter-chunking/` (remote branch deleted, can be cleaned up)

## Follow-up tasks

### High priority

1. **Run full codebase re-index** — the chunker version changed from none to `ts1`, so first run will purge and re-index everything with AST-aware chunks. Run `memory-sidecar index <project-path>` and verify stats.

2. **End-to-end retrieval test** — spawn agent, populate knowledge DB, dispatch task, verify pre-task retrieval returns AST-chunked code results.

3. **Importance scoring + access-driven decay** — track access count on knowledge entries, decay unused ones. From session 8 follow-ups, still open.

### Medium priority

4. **Configurable retrieval timeout** — still hardcoded 5s in AgentActor (from session 8).

5. **Incremental re-indexing** — currently re-indexes all files every run. Could track file mtime in metadata to skip unchanged files.

6. **Add `.gd` extension** — GDScript files use `.gd` extension but `_EXT_MAP` only maps `.gdscript`. Need to verify correct extension and add mapping.

### Lower priority

7. **Memory MCP server** (Phase 3) — expose memory operations as MCP tools.

8. **Knowledge decay/pruning** — auto-expire low-relevance entries.

9. **Cross-agent knowledge sharing** — shared.sqlite for common knowledge.

## Key decisions made

- **tree-sitter-language-pack over raw tree-sitter**: Bundles pre-compiled grammars, avoids build-from-source complexity. Trade-off: C# not available (falls back to simple splitting).
- **Named node fallback in AST collection**: Rather than strictly whitelisting node types, unrecognized named nodes are included. Prevents silent content loss for languages with unusual top-level constructs.
- **Purge-on-version-change over incremental migration**: Simpler than trying to diff old vs new chunk locations. Full re-index is fast enough for our codebase scale.
- **Validation at entry points**: `_validate_chunk_params` catches bad inputs early rather than relying on loop safeguards to prevent pathological behavior.
