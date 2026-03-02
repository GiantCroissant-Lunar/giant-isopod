# Memory Sidecar

Python-based embedding, indexing, and search service for giant-isopod's 4-layer memory architecture. Communicates with the C# actor system via CLI (CliWrap).

## Quick Start

```bash
cd memory-sidecar
pip install -e ".[dev]"

# Index a codebase
memory-sidecar index C:\lunar-horse\yokan-projects\giant-isopod

# Search code
memory-sidecar search "task graph dispatch" --top-k 5

# Store knowledge
memory-sidecar store "Avoid Strategy pattern in payment — circular dep" \
  --agent architect --category pitfall

# Query knowledge (hybrid vector + FTS5)
memory-sidecar query "payment module patterns" --agent architect --top-k 5
```

## Architecture

```
C# Actor System (Akka.NET)
  AgentActor
    ├── pre-task:  QueryKnowledge(description) → KnowledgeSupervisor
    └── post-task: StoreKnowledge(summary, category) → KnowledgeSupervisor
                   StoreMemory(summary) → MemorySupervisor

  KnowledgeSupervisor → KnowledgeStoreActor (per-agent)
    └── MemorySidecarClient (CliWrap) → memory-sidecar CLI

  MemorySupervisor → MemvidActor (per-agent)
    └── MemvidClient (CliWrap) → memvid CLI → .mv2 files
```

### 4-Layer Memory Model

| Layer | Actor | Storage | Lifetime | Access Pattern |
|-------|-------|---------|----------|----------------|
| **Working** | AgentActor dict | In-memory | Per-task | Key-value |
| **Shared** | BlackboardActor | In-memory | Per-session | Key-value + pub/sub |
| **Episodic** | MemvidActor | `.mv2` files | Archivable | Semantic search |
| **Long-term** | KnowledgeStoreActor | SQLite + sqlite-vec | Persistent | Hybrid search (vector + FTS5 RRF) |

The memory sidecar powers **Layer 4 (long-term)** and **codebase indexing**.

## Codebase Indexing

Indexes source files into SQLite with vector embeddings for semantic code search.

### Pipeline

```
Source files → filter by extension → prune excluded dirs (os.walk)
  → chunk (tree-sitter AST or simple text fallback)
  → embed (FastEmbed BAAI/bge-small-en-v1.5, 384 dims)
  → upsert into code_chunks + code_chunks_vec (sqlite-vec)
```

### Tree-sitter AST Chunking

When tree-sitter-language-pack supports the file's language, chunks are split at AST boundaries — functions, classes, imports stay intact rather than being split at arbitrary character positions.

Supported languages: Python, Rust, TypeScript, TSX, JavaScript, GDScript, Markdown, JSON, TOML, YAML.

Fallback: C# and unsupported languages use simple text splitting (line-boundary aware, configurable chunk size/overlap).

### Chunker Versioning

A `metadata` table tracks `chunker_version`. When the chunking algorithm changes (e.g., simple text -> AST-aware), all code chunks and vec0 embeddings are purged on the next index run to prevent stale/orphaned vector entries.

### File Filtering

**Included extensions**: `.cs`, `.py`, `.rs`, `.ts`, `.js`, `.tsx`, `.jsx`, `.md`, `.mdx`, `.toml`, `.json`, `.yaml`, `.yml`, `.gdscript`, `.tscn`, `.cfg`, `.csproj`, `.sln`

**Excluded directories** (pruned during os.walk, never descended into): `bin`, `obj`, `node_modules`, `target`, `__pycache__`, `_artifacts`, `addons`, and any directory starting with `.`

## Knowledge Store

Per-agent persistent knowledge with structured categories and hybrid search.

### Categories

| Category | Written when | Example |
|----------|-------------|---------|
| `outcome` | Task succeeds | "Payment refactor: interface extraction works" |
| `pitfall` | Task fails | "Circular dep when using Strategy pattern in payment" |
| `pattern` | Agent discovers reusable approach | "Async/await preferred over task continuations" |
| `codebase` | Structural insight | "SwarmHud uses observer pattern for state updates" |
| `preference` | User/agent preference noted | "Always use conventional commits" |

### Hybrid Search (RRF)

Queries combine two search strategies via Reciprocal Rank Fusion:

1. **Vector search** — sqlite-vec cosine similarity on FastEmbed embeddings
2. **FTS5 search** — SQLite full-text keyword matching

```
score(d) = sum(1 / (k + rank_i))   # k=60, across both result lists
```

Category filtering uses over-fetch (3x) + post-filter as a workaround for sqlite-vec's lack of WHERE clause support alongside MATCH.

## Pre-task Retrieval Loop

When an agent receives `TaskAssigned` with a description:

1. `AgentActor` sends `QueryKnowledge(description)` to `KnowledgeSupervisor` (Ask, 5s timeout)
2. `KnowledgeStoreActor` calls `memory-sidecar query` via CliWrap
3. Results are wrapped in XML by `PromptBuilder`:

```xml
<knowledge-context>
  <entry category="pitfall" relevance="0.87">
    Avoid Strategy pattern in payment — circular dep
  </entry>
</knowledge-context>

<task>Refactor the payment processing module</task>
```

4. Enriched prompt sent to agent's runtime
5. On timeout/failure: agent proceeds without context (graceful degradation)

## Post-task Write-back

- **TaskCompleted** → `StoreKnowledge(summary, "outcome")` + `StoreMemory(summary)`
- **TaskFailed** → `StoreKnowledge(reason, "pitfall")`

## Storage Schema

### Codebase (`codebase.sqlite`)

```sql
-- Source code chunks
CREATE TABLE code_chunks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    filename TEXT NOT NULL,
    location TEXT NOT NULL,     -- "chunk_idx:start_byte"
    language TEXT,
    code TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(filename, location)
);

-- Vector embeddings (sqlite-vec)
CREATE VIRTUAL TABLE code_chunks_vec USING vec0(
    id INTEGER PRIMARY KEY,
    embedding FLOAT[384]
);

-- Chunker version tracking
CREATE TABLE metadata (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
```

### Knowledge (`knowledge/{agent-id}.sqlite`)

```sql
CREATE TABLE knowledge (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    content TEXT NOT NULL,
    category TEXT NOT NULL,
    tags TEXT,                  -- JSON
    stored_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE VIRTUAL TABLE knowledge_vec USING vec0(
    id INTEGER PRIMARY KEY,
    embedding FLOAT[384]
);

CREATE VIRTUAL TABLE knowledge_fts USING fts5(
    content, category,
    content='knowledge', content_rowid='id'
);
-- Triggers keep FTS in sync on INSERT/UPDATE/DELETE
```

## Configuration

| Setting | Default | Env var |
|---------|---------|---------|
| Data directory | `data/memory` | `MEMORY_SIDECAR_DATA_DIR` |
| Embed model | `BAAI/bge-small-en-v1.5` | — |
| Embed dimensions | 384 | — |
| Chunk size | 1000 chars | — |
| Chunk overlap | 300 chars | — |

## Dependencies

- `fastembed` — ONNX-based embeddings (no PyTorch, ~67MB model auto-downloaded on first use)
- `sqlite-vec` — vector similarity search in SQLite
- `tree-sitter-language-pack` — AST parsing for code-aware chunking
- `click` — CLI interface

## File Layout

```
memory-sidecar/
├── pyproject.toml
├── src/memory_sidecar/
│   ├── cli.py              # Click CLI entry points
│   ├── config.py           # Paths, model names, defaults
│   ├── embed.py            # FastEmbed wrapper
│   ├── chunking.py         # split_simple, split_ast, chunk_file
│   ├── storage.py          # SQLite + sqlite-vec schema & CRUD
│   └── flows/
│       ├── codebase.py     # index_codebase, search_codebase
│       └── knowledge.py    # store/query knowledge
└── tests/
    ├── test_chunking.py    # 30 tests (simple, AST, dispatch)
    ├── test_codebase.py    # 19 tests (filtering, walking)
    ├── test_config.py      # 8 tests (paths, constants)
    └── test_storage.py     # 28 tests (schema, CRUD, metadata, purge)
```
