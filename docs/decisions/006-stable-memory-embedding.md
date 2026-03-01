# ADR-006: Stable Memory with CocoIndex, QMD, Tree-sitter, and FastEmbed

Date: 2026-03-01
Status: Proposed
Depends On: ADR-001, ADR-003

## Context

Giant-isopod's four-layer memory architecture (ADR-003) has two unfinished layers:

- **Layer 3 (Episodic)**: `MemvidActor` is stubbed — no actual CliWrap integration.
- **Layer 4 (Long-term)**: `KnowledgeStoreActor` is future/unimplemented.

The current system has no embedding generation, no code-aware chunking, no incremental
indexing, and no persistent vector search. Agents can't build or query a semantic
understanding of the codebase or their accumulated knowledge.

ADR-001 already designates CocoIndex, QMD, FastEmbed, and Tree-sitter as skill-based
tools rather than actors. This ADR defines the concrete implementation plan to wire
them together into a stable, embedded (no Docker, no containers) memory pipeline.

## Decision

Build a Python-based memory sidecar (`memory-sidecar/`) that provides embedding,
indexing, and search capabilities to the C# actor system via CLI and MCP interfaces.
All storage is local — SQLite + sqlite-vec for vectors, filesystem for .mv2 files.
No Docker, no containers, no cloud dependencies.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│  C# Actor System (Akka.NET)                             │
│                                                         │
│  MemvidActor ──CliWrap──► memvid CLI (.mv2 files)       │
│  KnowledgeStoreActor ──CliWrap──► memory-sidecar CLI    │
│  AgentActor (skills) ──MCP──► QMD MCP server            │
│                        ──MCP──► memory-sidecar MCP      │
└─────────────────────────────────────────────────────────┘
         │                    │                │
         ▼                    ▼                ▼
┌─────────────┐  ┌──────────────────┐  ┌─────────────┐
│ Memvid .mv2 │  │ memory-sidecar/  │  │  QMD        │
│ (episodic)  │  │                  │  │  (project   │
│             │  │ CocoIndex flows  │  │   search)   │
│             │  │ FastEmbed models │  │             │
│             │  │ Tree-sitter AST  │  │  BM25 +     │
│             │  │ SQLite + vec     │  │  vector +   │
│             │  │                  │  │  LLM rerank │
└─────────────┘  └──────────────────┘  └─────────────┘
```

## Components

### 1. memory-sidecar/ (New Python Package)

A Python package at `memory-sidecar/` in the repo root. Single `pyproject.toml`,
managed with `uv`. Provides both a CLI and an MCP server.

**Dependencies**:
- `cocoindex` — incremental indexing pipelines with Tree-sitter code chunking
- `fastembed` — ONNX-based embedding generation (no GPU required)
- `tree-sitter` + language grammars — AST-aware code splitting (used via CocoIndex)
- `sqlite-vec` — vector similarity search in SQLite (no Postgres needed)
- `click` — CLI interface
- `mcp` — MCP server for agent integration

**Why a sidecar, not in-process**: The C# actor system runs in Godot's .NET runtime.
Python ML libraries (ONNX, tree-sitter native bindings) can't load there. A sidecar
process communicates via CLI (CliWrap) and MCP (stdio transport), matching the existing
pattern used for `pi --mode rpc` and `memvid`.

### 2. CocoIndex Flows (Indexing Pipelines)

CocoIndex provides incremental data transformation with built-in Tree-sitter support.
We define two flows:

#### Flow A: Codebase Index

```
Source: LocalFile (project source files)
  → Extract language from file extension
  → SplitRecursively (Tree-sitter aware chunking)
  → FastEmbed embedding generation
  → Export to SQLite + sqlite-vec
```

- Incremental: only reprocesses changed files
- Tree-sitter splits code at function/class boundaries, not arbitrary line counts
- Supports: .cs, .py, .rs, .md, .toml, .json, .gdscript, .tscn

#### Flow B: Knowledge Index

```
Source: Agent knowledge entries (from Layer 4 long-term memory)
  → Categorize (pattern, pitfall, codebase, preference, outcome)
  → FastEmbed embedding generation
  → Export to SQLite + sqlite-vec with metadata columns
```

- Stores distilled agent knowledge with structured tags
- Supports semantic search + category/tag filtering
- Promotion pipeline: episodic (Memvid) → summarize → knowledge index

### 3. FastEmbed Integration

FastEmbed replaces the `SentenceTransformerEmbed` in CocoIndex examples with a
lighter-weight ONNX-based alternative.

**Model selection**:
- Text: `BAAI/bge-small-en-v1.5` (33M params, 384 dims) — good balance of
  quality and speed for code + natural language
- Sparse: `Qdrant/bm42-all-minilm-l6-v2-attentions` — hybrid sparse+dense search
- Future: `jinaai/jina-embeddings-v2-base-code` for code-specific embeddings

**Custom CocoIndex function** wrapping FastEmbed:

```python
@cocoindex.op.function()
def fastembed_encode(text: str) -> list[float]:
    """Generate embedding using FastEmbed ONNX runtime."""
    return _model.embed([text])[0].tolist()
```

### 4. SQLite + sqlite-vec (Storage)

CocoIndex supports SQLite as a target via its connector. Combined with `sqlite-vec`
for vector similarity:

```
data/
├── memory/
│   ├── codebase.sqlite      ← Flow A: code chunks + embeddings
│   ├── knowledge/
│   │   ├── {agent-id}.sqlite ← Flow B: per-agent long-term knowledge
│   │   └── shared.sqlite     ← Flow B: shared knowledge base
│   └── episodic/
│       └── {agent-id}.mv2    ← Existing Memvid files (Layer 3)
```

**Why SQLite over Postgres**: The requirement is no Docker/containers. SQLite is
embedded, zero-config, single-file. `sqlite-vec` provides cosine/L2/inner-product
similarity search. CocoIndex's SQLite connector handles the integration.

**Why not Postgres**: CocoIndex's default examples use Postgres for lineage tracking.
For our embedded use case, we use CocoIndex's SQLite connector for both lineage and
vector storage. This avoids any external database dependency.

### 5. QMD Integration (Project Search Skill)

QMD is already designated as the `project-search` skill (ADR-001). It provides:
- BM25 full-text search via SQLite FTS5
- Vector semantic search using local GGUF models
- LLM re-ranking for quality sorting
- MCP server mode for agent integration

**Setup**: `qmd collection add` indexes project docs (markdown, transcripts, specs).
Agents access it via MCP (`qmd --mcp`). No code changes needed in the actor system —
agents with the `project-search` skill connect to QMD's MCP server through their
pi RPC session.

QMD complements the memory-sidecar: QMD handles document-level search (specs, ADRs,
meeting notes), while the sidecar handles code-level and knowledge-level search.

### 6. Tree-sitter Role

Tree-sitter is used in two places:

1. **Via CocoIndex** (primary): `SplitRecursively` uses Tree-sitter internally for
   AST-aware code chunking. We pass the file extension as the `language` parameter.
   CocoIndex handles grammar loading.

2. **Direct usage** (optional, future): For extracting structured metadata from code
   (function signatures, class hierarchies, import graphs) to enrich knowledge entries.
   This would use `py-tree-sitter` directly with language-specific queries.

### 7. C# Integration Points

#### MemvidActor (Layer 3 — Complete the Stub)

Wire the existing `MemvidClient` (CliWrap) into `MemvidActor`:

```csharp
case StoreMemory store:
    var client = new MemvidClient(store.AgentId, _mv2Path);
    await client.PutAsync(store.Content, store.Title, store.Tags);
    break;

case SearchMemory search:
    var client = new MemvidClient(search.AgentId, _mv2Path);
    var hits = await client.SearchAsync(search.Query, search.TopK);
    Sender.Tell(new MemorySearchResult(search.AgentId, search.TaskRunId, hits));
    break;
```

#### KnowledgeStoreActor (Layer 4 — New)

New actor at `/user/knowledge/{agentId}`. Delegates to memory-sidecar CLI:

```csharp
// memory-sidecar store --agent {agentId} --category {cat} --content "..."
// memory-sidecar search --agent {agentId} --query "..." --top-k 10
```

#### Memory Sidecar Process Management

New `MemorySidecarClient` in `Plugin.Process` (CliWrap), similar to `MemvidClient`:

```csharp
public sealed class MemorySidecarClient
{
    Task StoreKnowledgeAsync(string agentId, string content, string category, ...);
    Task<IReadOnlyList<KnowledgeEntry>> SearchKnowledgeAsync(string agentId, string query, ...);
    Task IndexCodebaseAsync(string sourcePath);
    Task<IReadOnlyList<CodeSearchResult>> SearchCodeAsync(string query, int topK);
}
```

## Implementation Phases

### Phase 1: Foundation (This Branch)

1. Create `memory-sidecar/` Python package with `pyproject.toml`
2. Implement FastEmbed wrapper (embed CLI command)
3. Implement CocoIndex codebase flow (Flow A) with Tree-sitter chunking
4. SQLite + sqlite-vec storage target
5. CLI interface: `memory-sidecar index`, `memory-sidecar search`, `memory-sidecar embed`
6. Wire `MemvidActor` to actual `MemvidClient` (complete the stub)

### Phase 2: Knowledge Layer

7. Implement CocoIndex knowledge flow (Flow B)
8. Create `KnowledgeStoreActor` in C# actor tree
9. Create `MemorySidecarClient` in `Plugin.Process`
10. CLI: `memory-sidecar store`, `memory-sidecar query`
11. Episodic → long-term promotion pipeline

### Phase 3: MCP & Skills

12. MCP server mode for memory-sidecar (`memory-sidecar --mcp`)
13. QMD collection setup for project docs
14. Skill definitions: `embedding`, `indexing`, `project-search`, `knowledge-search`
15. Agent skill bundle composition for memory-capable agents

### Phase 4: Stability & Polish

16. Incremental re-indexing on file change (CocoIndex watches)
17. Knowledge decay/pruning (staleness scoring)
18. Cross-agent knowledge sharing via shared.sqlite
19. Taskfile integration (`task memory:index`, `task memory:search`)

## Data Flow: End-to-End Example

```
1. Agent "architect" gets task: "refactor payment module"

2. Agent queries codebase index (memory-sidecar search):
   → Tree-sitter-chunked code snippets from payment/*.cs
   → Ranked by FastEmbed cosine similarity

3. Agent queries QMD (project-search skill):
   → ADRs, specs, meeting notes mentioning "payment"
   → BM25 + vector + LLM re-ranked

4. Agent queries long-term knowledge (KnowledgeStoreActor):
   → "Payment module: avoid Strategy pattern (circular dep)" — from previous agent
   → "Payment tests require mock gateway setup" — accumulated knowledge

5. Agent works on task, recording episodic notes (MemvidActor):
   → "Tried extracting PaymentProcessor interface — works"
   → "Found dead code in LegacyGateway.cs — removed"

6. Task completes successfully. Promotion pipeline:
   → Episodic summary → KnowledgeStoreActor:
     "Payment refactor: interface extraction works. LegacyGateway had dead code."
   → Published to blackboard: "payment:refactored" = "success"
```

## File Structure

```
memory-sidecar/
├── pyproject.toml              ← uv-managed, deps: cocoindex, fastembed, etc.
├── src/
│   └── memory_sidecar/
│       ├── __init__.py
│       ├── cli.py              ← Click CLI: index, search, embed, store, query
│       ├── embed.py            ← FastEmbed wrapper
│       ├── flows/
│       │   ├── __init__.py
│       │   ├── codebase.py     ← CocoIndex Flow A (code → chunks → embeddings)
│       │   └── knowledge.py    ← CocoIndex Flow B (knowledge entries → embeddings)
│       ├── storage.py          ← SQLite + sqlite-vec helpers
│       ├── mcp_server.py       ← MCP server mode (future Phase 3)
│       └── config.py           ← Paths, model names, defaults
└── tests/
    └── ...
```

## Trade-offs

### Why FastEmbed over SentenceTransformers

- FastEmbed uses ONNX Runtime — no PyTorch dependency (~2GB saved)
- Lighter memory footprint, faster cold start
- Good enough quality for code search (bge-small-en-v1.5 scores well on MTEB)
- CocoIndex's `SentenceTransformerEmbed` works but pulls in torch; we wrap FastEmbed
  as a custom CocoIndex function instead

### Why SQLite over Postgres for CocoIndex

- No Docker/container requirement (explicit constraint)
- SQLite is embedded, zero-config, portable
- `sqlite-vec` provides adequate vector search for our scale (thousands of chunks,
  not millions)
- CocoIndex's SQLite connector handles lineage tracking
- Trade-off: no concurrent write access (acceptable — sidecar is single-writer)

### Why a Python sidecar over pure C#

- CocoIndex, FastEmbed, Tree-sitter all have Python-first APIs
- ONNX Runtime has C# bindings, but FastEmbed's model management and CocoIndex's
  pipeline framework don't
- Matches existing pattern: `pi --mode rpc` and `memvid` are already external processes
- CliWrap + MCP provide clean integration boundaries

### Why keep QMD separate from memory-sidecar

- QMD is a mature, standalone tool with its own MCP server
- It handles document search (BM25 + vector + LLM rerank) better than we'd build
- Different concern: QMD = project docs, memory-sidecar = code + agent knowledge
- Agents can use both via separate skills

### Risk

- **Cold start latency**: FastEmbed ONNX model loading takes ~2-3s on first call.
  Mitigate by keeping the sidecar process warm (long-running MCP server mode).
- **SQLite write contention**: Single-writer limitation. Acceptable since the sidecar
  serializes writes. If multiple agents need concurrent writes, shard by agent ID
  (already the plan for knowledge/*.sqlite).
- **CocoIndex SQLite maturity**: The SQLite connector is newer than Postgres. May hit
  edge cases. Mitigate by pinning CocoIndex version and testing incrementality.
- **Model size**: bge-small-en-v1.5 is ~67MB on disk. Acceptable for a desktop app.
  Auto-downloaded on first use by FastEmbed.

## References

- ADR-001: Skill-Based Tooling Over Actor-Per-Tool
- ADR-003: Four-Layer Memory Architecture
- CocoIndex code indexing: https://cocoindex.io/docs/examples/code_index
- CocoIndex SQLite connector: https://cocoindex.io/docs-v1/connectors/sqlite
- FastEmbed: https://github.com/qdrant/fastembed
- QMD: https://github.com/tobi/qmd
- Tree-sitter: https://tree-sitter.github.io/tree-sitter/
- sqlite-vec: https://github.com/asg017/sqlite-vec
- Memvid: https://github.com/memvid/memvid
