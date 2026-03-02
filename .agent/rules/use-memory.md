# Use Memory

## Rule

Agents MUST consult the memory sidecar before and after non-trivial tasks.

### Before Starting Work

Search for relevant context:

```bash
memory-sidecar search "topic" --db data/memory/codebase.sqlite --top-k 5 --json-output
memory-sidecar query "topic" --agent shared --db data/memory/knowledge/shared.sqlite --top-k 5 --json-output
```

Skip for trivial tasks (typo fixes, single-line changes).

### After Completing Work

Store outcomes and learnings:

```bash
memory-sidecar store "what was done and why" --agent shared --category outcome --db data/memory/knowledge/shared.sqlite
```

Store pitfalls if something was tricky or non-obvious:

```bash
memory-sidecar store "description of the gotcha" --agent shared --category pitfall --db data/memory/knowledge/shared.sqlite
```

### Full Details

See the memory skill (`@.agent/skills/memory/SKILL.md`) for all commands and categories.
