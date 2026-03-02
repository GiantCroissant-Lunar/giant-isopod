# Test Coverage Analysis

**Date:** 2026-03-01
**Scope:** Full codebase — .NET solution (10 projects, ~70 C# files) and Python memory-sidecar (~8 files)

## Executive Summary

The giant-isopod codebase currently has **zero test projects and zero test files** across both the .NET solution and the Python memory-sidecar. There are no xUnit/NUnit/MSTest references, no pytest configuration, and no test runner tasks in `Taskfile.yml`. This document identifies the highest-value areas for introducing tests, ordered by risk and complexity.

---

## 1. Current State

| Layer | Projects | Source files | Test files | Coverage |
|-------|----------|-------------|------------|----------|
| Contracts (C#) | 4 | 26 | 0 | 0% |
| Plugins (C#) | 5 | 34 | 0 | 0% |
| Hosts (C#) | 1 | 11 | 0 | 0% |
| memory-sidecar (Python) | 1 | 8 | 0 | 0% |
| **Total** | **11** | **~79** | **0** | **0%** |

Quality tooling that does exist: Ruff linter/formatter (Python), pre-commit hooks, detect-secrets, .editorconfig.

---

## 2. Recommended Test Projects to Create

### .NET

| Test project | Tests for | Framework suggestion |
|---|---|---|
| `Contracts.Core.Tests` | Messages, ModelSpec, record equality | xUnit |
| `Plugin.Actors.Tests` | All Akka actors | xUnit + Akka.TestKit.Xunit2 |
| `Plugin.Process.Tests` | RuntimeFactory, RuntimeRegistry, CliAgentRuntime, MemorySidecarClient | xUnit |
| `Plugin.Protocol.Tests` | AgUiAdapter, A2UIRenderer | xUnit |
| `Plugin.Mapping.Tests` | ProtocolMapper, AieosMapper | xUnit |
| `Plugin.ECS.Tests` | MovementSystem, WanderSystem, AnimationSystem | xUnit + Friflo.Engine.ECS |
| `Hosts.CompleteApp.Tests` | MarkdownBBCode | xUnit |

### Python

| Test module | Tests for | Framework |
|---|---|---|
| `tests/test_config.py` | config.py path helpers | pytest |
| `tests/test_storage.py` | storage.py CRUD, serialization | pytest |
| `tests/test_codebase.py` | flows/codebase.py chunking, filtering | pytest |
| `tests/test_knowledge.py` | flows/knowledge.py store/query | pytest |
| `tests/test_cli.py` | CLI argument parsing, output formatting | pytest + click.testing.CliRunner |

---

## 3. Priority Areas (ordered by impact)

### Priority 1 — Critical: Task dispatch and graph orchestration

These modules contain the most complex stateful logic and are the most likely source of subtle bugs.

#### 3a. `TaskGraphActor` — DAG validation, cycle detection, dispatch ordering
**File:** `project/plugins/Plugin.Actors/TaskGraphActor.cs`

| What to test | Why |
|---|---|
| `GraphState.TryCreate` — cycle detection via TopologicalSort | A bug here silently accepts cyclic graphs, causing infinite waits |
| `GraphState.TryCreate` — duplicate TaskId rejection | Untested duplicate IDs would cause `ToDictionary` crashes |
| `GraphState.TryCreate` — edges referencing unknown tasks | Bad edges are currently skipped silently; verify this behavior |
| `DispatchReadyNodes` — only dispatches nodes whose deps are all completed | Incorrect dispatch ordering breaks the entire DAG contract |
| `CancelDependents` — BFS cancellation propagation | Must cancel the full transitive closure, not just direct children |
| `HandleGraphTimedOut` — Dispatched nodes → Failed, Pending/Ready → Cancelled | Status transition correctness on timeout |
| `CheckGraphCompletion` — terminal-state detection | Must recognize all of Completed/Failed/Cancelled as terminal |

**Test approach:** Unit-test `GraphState.TryCreate` directly (it's `internal`). Use Akka.TestKit for the actor message-handling paths with `TestProbe` for dispatch and viewport.

#### 3b. `DispatchActor` — bid-based task routing with risk approval
**File:** `project/plugins/Plugin.Actors/DispatchActor.cs`

| What to test | Why |
|---|---|
| Bid winner selection: highest fitness → lowest load → shortest duration | Incorrect ranking assigns tasks to the wrong agent |
| First-match fallback when no bids arrive | Zero-bid path must still assign a task |
| Late/duplicate/non-capable bid rejection | Prevents spurious assignments |
| Risk approval gate for `RiskLevel.Critical` | Critical tasks must NOT be assigned without viewport approval |
| `IsFromTrustedApprover` — rejects non-viewport senders | Security-relevant: prevents spoofed approvals |
| Approval timeout → TaskFailed | Indefinite pending state would deadlock the system |

**Test approach:** Akka.TestKit with `TestProbe` for registry, agentSupervisor, and sender.

#### 3c. `AgentActor` — task lifecycle, knowledge retrieval, bidding
**File:** `project/plugins/Plugin.Actors/AgentActor.cs`

| What to test | Why |
|---|---|
| `EvaluateAndBid` — capacity check, fitness calculation, threshold | Wrong bids waste resources or starve capable agents |
| `TaskAssigned` → pre-task knowledge retrieval via `Ask` + `PipeTo` | Async retrieval with timeout is the most fragile pattern in Akka |
| `RetrievalComplete` → context-enriched prompt construction | Malformed prompts degrade agent output quality |
| `RetrievalFailed` → graceful degradation (proceed without context) | Must not block the task pipeline on retrieval failure |
| `TaskCompleted` → post-task write-back to knowledge + memory | Data loss if write-back is skipped |
| `MapTextToState` — heuristic text classification | Validates the keyword → state mapping |
| Working memory CRUD (Set/Get/Clear) | Simple but foundational for cross-message state |

---

### Priority 2 — High: Runtime process management

#### 3d. `RuntimeFactory.MergeModel` — model spec merging
**File:** `project/plugins/Plugin.Process/RuntimeFactory.cs`

| What to test | Why |
|---|---|
| `MergeModel(null, default)` → returns default | |
| `MergeModel(explicit, null)` → returns explicit | |
| `MergeModel(explicit, default)` — explicit fields override, nulls fall through | Most important: partial override semantics |
| `MergeParameters` — same override semantics for the Parameters dictionary | |
| `Create` — pattern match returns correct runtime type | Incorrect match throws at startup |

**Test approach:** Pure unit tests — no mocking needed. This is the easiest high-value target.

#### 3e. `RuntimeRegistry` — JSON deserialization and resolution
**File:** `project/plugins/Plugin.Process/RuntimeRegistry.cs`

| What to test | Why |
|---|---|
| `LoadFromJson` — round-trip with known JSON fixtures | Deserialization errors fail silently or crash at startup |
| `LoadFromLegacyCliProviders` — legacy format conversion | Backward compatibility must be verified |
| `Resolve` — case-insensitive lookup | |
| `ResolveOrDefault` — fallback to first runtime | |
| `ResolveOrDefault` with no runtimes → throws | |

#### 3f. `CliAgentRuntime` — placeholder resolution
**File:** `project/plugins/Plugin.Process/CliAgentRuntime.cs`

| What to test | Why |
|---|---|
| `ResolvePlaceholders` — `{prompt}`, `{provider}`, `{model}` substitution | Unresolved placeholders pass literal `{prompt}` to the CLI |
| `ResolveArgs` — merging defaults, explicit model, and config defaults | Priority order: explicit > config default > fallback |

---

### Priority 3 — High: Protocol mapping and adaptation

#### 3g. `AgUiAdapter` — stateful RPC-to-AG-UI event mapping
**File:** `project/plugins/Plugin.Protocol/AgUiAdapter.cs`

| What to test | Why |
|---|---|
| First event → auto-starts a run (RunStartedEvent) | Missing RunStarted breaks AG-UI clients |
| `tool_use` → ends active text message, starts tool call | Interleaving must be correct |
| `tool_result` → ends tool call | Dangling tool calls confuse UI |
| `exit` → closes all active message/tool/run | Leak of open runs/messages |
| Text content → starts new message if needed, appends content | |
| `ExtractToolName` — JSON substring parsing | Fragile string parsing; edge cases with malformed input |
| `MapRpcEventToActivity` — static keyword mapping | |

**Test approach:** Pure unit tests on a fresh `AgUiAdapter` instance per test. No external dependencies.

#### 3h. `ProtocolMapper` — tool event to activity state, AIEOS mapping
**File:** `project/plugins/Plugin.Mapping/ProtocolMapper.cs`

| What to test | Why |
|---|---|
| `MapToolEventToState` — tool name and status branching | Two-level switch: tool name first, then status fallback |
| `MapAieosToVisualInfo` — null coalescing chain for display name | Falls through: Identity.Names.First → Metadata.Alias → agentId |
| `MapAieosToCapabilities` — null Skills, null Name filtering | |

---

### Priority 4 — Medium: ECS systems (game simulation)

#### 3i. `MovementSystem` — position interpolation and arrival
**File:** `project/plugins/Plugin.ECS/MovementSystem.cs`

| What to test | Why |
|---|---|
| Movement toward target: velocity direction and magnitude | Incorrect normalization causes diagonal speed boost |
| Arrival within threshold: snaps to target, clears HasTarget | Off-by-one on threshold causes oscillation |
| Zero-distance case (already at target) | Division by zero in normalization |
| No target (HasTarget=false) → no movement | |

#### 3j. `WanderSystem` — random target assignment
**File:** `project/plugins/Plugin.ECS/WanderSystem.cs`

| What to test | Why |
|---|---|
| Only assigns targets to Idle agents without existing target | Overwriting in-progress movement causes teleporting |
| Target clamping to valid tile bounds (4..56, 4..30) | Out-of-bounds targets crash the renderer |
| Timer reset — wander check fires periodically, not every frame | |

---

### Priority 5 — Medium: Blackboard and skill registry actors

#### 3k. `BlackboardActor` — pub/sub shared memory
**File:** `project/plugins/Plugin.Actors/BlackboardActor.cs`

| What to test | Why |
|---|---|
| PublishSignal → notifies direct subscribers AND EventStream | Missed notifications break cross-agent coordination |
| SubscribeSignal → sends current value immediately | Late subscriber must catch up |
| QuerySignal → returns current value or null SignalValue | |
| ListSignals with prefix filter | |
| Terminated → cleans up dead subscriber refs | Memory leak if not cleaned |

#### 3l. `SkillRegistryActor` — capability matching
**File:** `project/plugins/Plugin.Actors/SkillRegistryActor.cs`

| What to test | Why |
|---|---|
| Register → query returns the agent | |
| Unregister → query no longer returns the agent | |
| `IsSubsetOf` matching: agent must have ALL required capabilities | Superset agents should match; partial matches should not |
| Multiple agents with overlapping capabilities | |

---

### Priority 6 — Medium: MarkdownBBCode converter

#### 3m. `MarkdownBBCode.Convert` — Markdown to BBCode
**File:** `project/hosts/complete-app/Scripts/MarkdownBBCode.cs`

| What to test | Why |
|---|---|
| Headings (h1-h4) → `[font_size=N][b][color]` | |
| Bold/italic → `[b]`/`[i]` tags | |
| Inline code → `[code][color]` | |
| Fenced code blocks → `[code]` | |
| Links → `[url=...]` | |
| Lists (ordered and unordered) | Bug: both ordered and unordered use `"  • "` |
| Nested blocks (quotes, nested lists) | Recursive rendering may break on deep nesting |
| Empty/null input → empty string | |

**Test approach:** Pure unit tests with markdown input → expected BBCode output. No dependencies to mock.

---

### Priority 7 — High: Python memory-sidecar

#### 3n. `config.py` — path resolution
**File:** `memory-sidecar/src/memory_sidecar/config.py`

| What to test | Why |
|---|---|
| `data_dir()` respects `MEMORY_SIDECAR_DATA_DIR` env var | |
| `data_dir()` falls back to `"data/memory"` | |
| `knowledge_db_path(agent_id)` → agent-specific path | |
| `knowledge_db_path(None)` and `knowledge_db_path("")` → `shared.sqlite` | Empty string is falsy — verify this is intentional |

#### 3o. `storage.py` — SQL operations and vector serialization
**File:** `memory-sidecar/src/memory_sidecar/storage.py`

| What to test | Why |
|---|---|
| `_serialize_vec` — round-trip with `struct.unpack` | Binary format correctness |
| `connect` — sets WAL mode, creates parent dirs | |
| Schema initialization — branching on `_has_vec` | Tables created with or without vec extension |
| `upsert_code_chunk` — insert and update-on-conflict | |
| `search_knowledge` — over-fetch (3x) when category is set, post-filter | Incorrect multiplier returns too few results |
| `delete_stale_chunks` — empty `keep_locations` deletes all for file | |
| `delete_stale_chunks` — non-empty `keep_locations` uses NOT IN | Dynamic SQL placeholder construction |

**Test approach:** Use an in-memory SQLite database (`:memory:`) with `_has_vec = False` for unit tests. Separate integration tests with the real `sqlite-vec` extension if available.

#### 3p. `flows/codebase.py` — chunking algorithm and file filtering
**File:** `memory-sidecar/src/memory_sidecar/flows/codebase.py`

| What to test | Why |
|---|---|
| `_should_include` — excludes hidden dirs, `node_modules`, `__pycache__`, etc. | |
| `_should_include` — includes only files with valid CODE_EXTENSIONS | |
| `_split_simple` — content smaller than chunk_size → single chunk | |
| `_split_simple` — large content split with overlap, newline-aware boundaries | Most algorithmically complex function in the Python codebase |
| `_split_simple` — whitespace-only chunks are skipped | |
| `_split_simple` — location format `"{idx}:{start}"` | |
| `index_codebase` — raises FileNotFoundError for nonexistent path | |
| `index_codebase` — batching: flush at batch_size, final flush for remainder | |

**Test approach:** `_split_simple` and `_should_include` are pure functions — test directly with no mocking. For `index_codebase`, use a temporary directory with sample files and mock `embed_texts`.

#### 3q. `cli.py` — CLI interface
**File:** `memory-sidecar/src/memory_sidecar/cli.py`

| What to test | Why |
|---|---|
| `store --tag key:value` parsing (split on first `:`) | Tags without `:` are silently dropped |
| `search` human-readable output truncates to 3 lines | |
| `query` human-readable output truncates content to 120 chars | |
| `--db` option overrides default path | |
| Each command invokes the correct flow function | |

**Test approach:** `click.testing.CliRunner` with mocked flow functions.

---

## 4. Quick Wins (highest value per effort)

These can be implemented immediately with no infrastructure changes:

1. **`RuntimeFactory.MergeModel`** — 5 pure unit tests, zero dependencies
2. **`ProtocolMapper.MapToolEventToState`** — 6 pure unit tests, table-driven
3. **`AgUiAdapter.MapRpcEventToAgUiEvents`** — 10+ pure unit tests, stateful but no external deps
4. **`MarkdownBBCode.Convert`** — 8+ pure unit tests, string-in/string-out
5. **`_split_simple` (Python)** — 6+ pure unit tests, algorithmic logic
6. **`_should_include` (Python)** — 5+ pure unit tests, path filtering
7. **`config.py` path helpers (Python)** — 4 pure unit tests with env var patching
8. **`_serialize_vec` (Python)** — 2 round-trip tests

---

## 5. Architectural Concerns Found During Analysis

| Issue | Location | Impact |
|---|---|---|
| No `try/finally` for DB connections | `flows/knowledge.py`, `flows/codebase.py` | Connection leak on exception |
| Ordered list renders same bullet as unordered | `MarkdownBBCode.cs:81` | Visual bug: ordered lists show `•` instead of `1.`, `2.`, etc. |
| Global mutable state for singletons | `embed.py` (`_model`), `storage.py` (`_has_vec`) | Tests must reset globals between runs |
| `AgUiAdapter.ExtractToolName` uses fragile string parsing | `AgUiAdapter.cs:122-139` | Will break on minified JSON or escaped quotes |
| `AgentActor.LoadVisualInfo` deserializes the _path string_ as JSON | `AgentActor.cs:305-306` | Should read the file first, then deserialize; currently passes the path as JSON content |

---

## 6. Suggested Test Infrastructure Setup

### .NET
```xml
<!-- Directory.Build.props or each test .csproj -->
<PackageReference Include="xunit" Version="2.*" />
<PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
<PackageReference Include="NSubstitute" Version="5.*" />        <!-- mocking -->
<PackageReference Include="Akka.TestKit.Xunit2" Version="1.5.*" /> <!-- actor tests -->
<PackageReference Include="FluentAssertions" Version="7.*" />   <!-- readable assertions -->
```

Add a `test` task to `Taskfile.yml`:
```yaml
test:
  desc: Run all .NET tests
  cmds:
    - dotnet test {{.SLN}} --nologo --verbosity minimal
```

### Python
Add to `pyproject.toml`:
```toml
[project.optional-dependencies]
dev = ["pytest>=8.0", "pytest-cov>=5.0"]

[tool.pytest.ini_options]
testpaths = ["tests"]
```

Add a `test:py` task to `Taskfile.yml`:
```yaml
test:py:
  desc: Run Python tests
  dir: memory-sidecar
  cmds:
    - pytest --tb=short -q
```
