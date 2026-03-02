# Memory

Search and store knowledge using the giant-isopod memory sidecar. Use when Claude needs to: search the codebase for code patterns or implementations, recall past decisions or pitfalls, store new knowledge after completing tasks, or index code and documents.

## Quick Reference

All commands run from the giant-isopod project root.

| Action | Command |
|--------|---------|
| Search codebase | `memory-sidecar search "query" --db data/memory/codebase.sqlite` |
| Search knowledge | `memory-sidecar query "query" --agent shared --db data/memory/knowledge/shared.sqlite --json-output` |
| Store knowledge | `memory-sidecar store "content" --agent shared --category <cat> --db data/memory/knowledge/shared.sqlite` |
| Index codebase | `memory-sidecar index project/ --db data/memory/codebase.sqlite` |
| Index documents | `memory-sidecar index-docs docs/ --db data/memory/codebase.sqlite` |

## Knowledge Categories

| Category | When to use |
|----------|-------------|
| `pattern` | Reusable approaches, conventions, architectural patterns |
| `pitfall` | Bugs, gotchas, things that didn't work |
| `codebase` | Facts about the codebase structure or behavior |
| `preference` | User preferences for tooling, style, workflow |
| `outcome` | Results of completed tasks, what was done and why |

## Search Workflow

1. **Codebase search** — find implementations, patterns, or code by semantic similarity:
   ```bash
   memory-sidecar search "actor message handling" --db data/memory/codebase.sqlite --json-output
   ```

2. **Knowledge search** — recall past decisions, pitfalls, or stored insights:
   ```bash
   memory-sidecar query "memory architecture" --agent shared --db data/memory/knowledge/shared.sqlite --json-output
   ```

3. Optionally filter knowledge by category:
   ```bash
   memory-sidecar query "build issues" --agent shared --category pitfall --db data/memory/knowledge/shared.sqlite --json-output
   ```

## Store Workflow

After completing a task, store relevant knowledge:

```bash
memory-sidecar store "Docling converts PDF/DOCX to markdown via DocumentConverter. Lazy-imported to keep module loadable without the heavy dep." --agent shared --category pattern --db data/memory/knowledge/shared.sqlite
```

Use `--tag` for extra metadata:
```bash
memory-sidecar store "content" --agent shared --category pitfall --tag area:build --tag severity:high --db data/memory/knowledge/shared.sqlite
```

## Index Workflow

Re-index when source files change significantly:

- **Code**: `task memory:index` or `memory-sidecar index project/ --db data/memory/codebase.sqlite`
- **Documents**: `task memory:index-docs -- docs/` or `memory-sidecar index-docs docs/ --db data/memory/codebase.sqlite`
- **Install Docling** (first time): `task memory:install-docs`

## Database Paths

| Database | Path | Contents |
|----------|------|----------|
| Codebase | `data/memory/codebase.sqlite` | Code chunks + document chunks (vec0 embeddings) |
| Knowledge | `data/memory/knowledge/shared.sqlite` | Stored knowledge entries (vec0 + FTS5 hybrid) |
