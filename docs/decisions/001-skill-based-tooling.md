# ADR-001: Skill-Based Tooling Over Actor-Per-Tool

Date: 2026-02-28
Status: Accepted

## Context

The original architecture (`docs/_inbox/architecture-discussion.md`) placed shared tooling
inside the Akka.NET actor tree — notably `/user/memory/shared` for QMD project search and
`/user/a2a` for inter-agent protocol. This meant each external tool required:

- A dedicated actor (lifecycle, supervision, message routing)
- A CliWrap client in `Plugin.Process`
- Message types in `Contracts.Core`
- Wiring in `AgentWorldSystem`

Meanwhile, the architecture already defines a capability-based skill system where agents gain
abilities through skill bundles rather than fixed roles. External tools fit naturally into this
model — they are capabilities an agent either has or doesn't.

## Decision

External tools (QMD, FastEmbed, CocoIndex, MCP servers, A2A) are exposed as **agent skills**
rather than dedicated actors in the Akka.NET tree.

The actor tree is reserved for concerns that genuinely need Akka supervision: agent lifecycle,
message routing, dispatch, and the viewport observer bridge.

## Tools as Skills

### QMD — Project Search

QMD is an on-device search engine (BM25 + vector + LLM re-ranking) for markdown, docs, and
transcripts. It manages its own state in SQLite — no actor supervision needed.

| | |
|-|-|
| Skill name | `project-search` |
| Capability | `project_search` |
| How it works | QMD exposes an MCP server with tools: `qmd_search`, `qmd_vector_search`, `qmd_deep_search`, `qmd_get`, `qmd_multi_get`, `qmd_status` |
| Agent integration | Pi RPC session loads QMD MCP server; agent searches during normal reasoning |
| Setup | `qmd collection add` is a one-time admin step outside the app |
| Runtime | Node.js ≥ 22, ~1.9GB local GGUF models (auto-downloaded) |

Reference: https://github.com/tobi/qmd

### FastEmbed — Embedding Generation

FastEmbed generates text, image, and sparse embeddings via ONNX Runtime. Lightweight, no GPU
required, suitable for serverless.

| | |
|-|-|
| Skill name | `embedding` |
| Capabilities | `embed_text`, `embed_image` |
| How it works | Python library or wrapped as MCP server; agent calls to generate embeddings for semantic tasks |
| Use cases | Enriching agent memory with vector search, building similarity indexes, content classification |
| Runtime | Python, ONNX Runtime |

Reference: https://github.com/qdrant/fastembed

### CocoIndex — Data Indexing Pipelines

CocoIndex is an incremental data transformation framework (Rust core, Python API). It handles
source → transform → embed → store pipelines with automatic change detection.

| | |
|-|-|
| Skill name | `indexing` |
| Capabilities | `index_data`, `transform_data` |
| How it works | Agent defines or triggers indexing pipelines; CocoIndex handles incremental recomputation |
| Use cases | Building and maintaining vector indexes, knowledge graphs, document processing |
| Runtime | Rust core, Python API |

Reference: https://github.com/cocoindex-io/cocoindex

### MCP — Generic Tool Protocol

MCP (Model Context Protocol) itself becomes a skill. An agent with this skill can discover and
use any MCP-compatible server at runtime.

| | |
|-|-|
| Skill name | `mcp-connect` |
| Capability | `mcp_tool_use` |
| How it works | Agent connects to configured MCP servers (stdio or HTTP), discovers available tools, calls them as part of reasoning |
| Why this matters | New MCP servers become available without code changes — assign the skill and provide server config |
| Subsumes | QMD MCP, future database MCP, API MCP servers |

Since QMD, and potentially FastEmbed and CocoIndex, can all expose MCP servers, the
`mcp-connect` skill acts as a universal adapter. Specific skills like `project-search` layer
domain knowledge on top (when to search, what queries to use, how to interpret results).

## Resulting Actor Tree

```
ActorSystem "agent-world"
│
├── /user/registry                     ← Skills + capabilities
├── /user/memory/{agent}               ← Memvid only (per-agent)
├── /user/agents/{name}                ← Agent lifecycle + pi RPC
│   └── /user/agents/{name}/tasks
├── /user/dispatch                     ← Capability-based routing
└── /user/viewport                     ← Godot observer bridge
```

Removed from the tree:
- ~~/user/memory/shared~~ — QMD is a skill, not an actor
- ~~/user/a2a~~ — A2A protocol is a skill (agent-to-agent discovery via MCP)

## Skill Composition

An agent's skill bundle determines what tools it can access:

```
Agent "architect"
├── project-search    → can query QMD for specs and docs
├── mcp-connect       → can use any configured MCP server
└── code_review       → can review code (domain skill)

Agent "indexer"
├── indexing           → can run CocoIndex pipelines
├── embedding          → can generate FastEmbed vectors
└── project-search     → can verify indexed content via QMD

Agent "worker"
├── code_edit          → can edit code
├── shell_run          → can run shell commands
└── (no search skills) → relies on dispatch for knowledge tasks
```

## Trade-offs

### Why skills win for external tools

- **Self-managing state**: QMD (SQLite), CocoIndex (incremental cache), FastEmbed (ONNX models)
  all manage their own persistence. No actor supervision needed.
- **Composable**: Agents gain capabilities through assignment, not wiring.
- **Extensible**: New MCP servers need zero code changes — just a skill definition and config.
- **Simpler tree**: Fewer actors, fewer message types, fewer CliWrap clients.

### When actors are still right

- **Agent lifecycle**: Spawn, crash recovery, restart strategies need Akka supervision.
- **Message routing**: Dispatch and registry need to be always-available tree nodes.
- **Viewport bridge**: Thread-safe observer between Akka threads and Godot main thread.
- **Coordinated state**: If multiple agents need synchronized access to a shared mutable
  resource, an actor serializes access.

### Risk

- Each agent manages its own MCP connections — no shared connection pooling.
- If QMD's SQLite gets corrupted, no actor will auto-restart it. Mitigation: QMD is robust
  and agents can report tool failures through normal error handling.

## References

- Architecture discussion: `docs/_inbox/architecture-discussion.md`
- QMD: https://github.com/tobi/qmd
- FastEmbed: https://github.com/qdrant/fastembed
- CocoIndex: https://github.com/cocoindex-io/cocoindex
- MCP: https://modelcontextprotocol.io
- Capability-based agents spec: `swimming-tuna/.kiro/specs/capability-based-agents/requirements.md`
