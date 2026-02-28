# Giant Isopod — Architecture Discussion

Date: 2026-02-28

## Project Overview

A Godot 4.6 C# exported windowed app that visualizes AI agents as animated characters. Borrows concepts from:
- [Overstory](https://github.com/jayminwest/overstory) — multi-agent orchestration for AI coding agents
- [Pixel Agents](https://github.com/pablodelucca/pixel-agents) — VS Code extension showing agents as pixel art characters

Primary coding language: C#. Godot engine at `C:\lunar-horse\tools\Godot_v4.6.1-stable_mono_win64`.

## Technology Stack

| Layer | Technology | Purpose |
|-------|-----------|---------|
| Wire types | [Quicktype](https://quicktype.io) (codegen) | JSON schema → C# models |
| Object mapping | [Mapperly](https://github.com/riok/mapperly) (source gen) | Compile-time object-to-object mapping, no reflection |
| Actor system | [Akka.NET](https://github.com/akkadotnet/akka.net) 1.5.x | Message tree, supervision, lifecycle |
| Process mgmt | [CliWrap](https://github.com/Tyrrrz/CliWrap) | Spawn pi, memvid-cli, qmd processes |
| Agent identity | [AIEOS v1.2](https://github.com/entitai/aieos) | Persona, physicality, psychology, linguistics |
| Agent skills | [Agent Skills spec](https://agentskills.io) | Skills → capabilities → dispatch |
| Agent memory | [Memvid](https://github.com/memvid/memvid) (.mv2) | Per-agent persistent rewindable memory |
| Project knowledge | [QMD](https://github.com/tobi/qmd) (optional) | Shared searchable doc index |
| ECS | [Friflo.Engine.ECS](https://github.com/friflo/friflo.engine.ecs) | Game simulation (positions, animations, rendering) |
| Agent backend | [Pi (pi-mono)](https://github.com/badlogic/pi-mono) --mode rpc | LLM interaction, tool use |
| Agent-to-agent | [A2A protocol](https://github.com/a2aproject/A2A) | Discovery, task delegation |
| Agent-to-UI | [AG-UI](https://github.com/ag-ui-protocol/ag-ui) + [A2UI](https://a2ui.org) | Event streaming + generative UI |
| Generative UI | GenUI for Godot (inspired by [Flutter genui](https://github.com/flutter/genui)) | A2UI JSON → Godot .tscn/Control nodes |
| Rendering | Godot 4.6 C# | Viewport, controls, scene tree |
| Hot-reload UI | Godot PCK files | Reloadable assets + GDScript, never C# |
| Orchestration patterns | [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/overview/) | Agent abstractions, workflows, middleware |

## Key Architecture Decisions

### 1. Godot is NOT the orchestrator

The Godot app is a viewport/renderer/interaction surface. It observes and displays what agents are doing, and provides a UI for humans to interact with agents. It doesn't decide who does what. The agents themselves form an autonomous system via the Akka.NET actor tree.

### 2. Akka.NET actor tree IS the backbone

Agents are actors. Messages flow through the tree. The tree structure gives natural supervision, scoping, and message routing. Godot's viewport actor subscribes to events but never commands agents.

```
ActorSystem "agent-world"
│
├── /user/registry                         ← Skill_Registry + AIEOS discovery
│   ├── Indexes loaded skills (SKILL.md files)
│   ├── Tracks agent capability sets (derived from skill bundles)
│   ├── Resolves Capability_Requirements → agent matches
│   └── A2A Agent Card publishing
│
├── /user/memory                           ← Memory supervisor
│   ├── /user/memory/{agent}               ← Memvid actor: owns {agent}.mv2
│   └── /user/memory/shared                ← QMD actor (optional): project docs
│
├── /user/agents                           ← Agent supervisor (restart strategy)
│   ├── /user/agents/{name}                ← AgentActor
│   │   ├── AIEOS profile + Skill_Bundle + derived capabilities
│   │   ├── /user/agents/{name}/rpc        ← CliWrap: pi --mode rpc
│   │   └── /user/agents/{name}/tasks      ← Active task tracking
│   └── (agents spawn/die dynamically)
│
├── /user/dispatch                         ← Task routing
│   ├── Receives task requests
│   ├── Extracts Capability_Requirements
│   └── Queries /user/registry for matching agents
│
├── /user/a2a                              ← A2A protocol (inter-agent)
│
└── /user/viewport                         ← Godot bridge (OBSERVER only)
    ├── /user/viewport/agui                ← AG-UI event stream → ECS
    └── /user/viewport/genui               ← A2UI render requests → Godot Controls
```

### 3. Skills, not roles

No fixed role identities. Each agent has a set of capabilities derived from assigned skills (Agent Skills spec from agentskills.io). Dispatch matches tasks to agents by capability requirements, not role labels.

- **Skill**: SKILL.md package with `metadata.capabilities` list
- **Skill_Bundle**: Named set of skills (replaces role enum)
- **Capability_Identifier**: Atomic tag (code_edit, shell_run, test_run, etc.)
- **Capability_Requirement**: Set of identifiers needed for a task
- **Dispatch**: Find agents whose derived capabilities ⊇ requirement

Reference design: `swimming-tuna/.kiro/specs/capability-based-agents/requirements.md`

### 4. Memvid for agent memory

Each agent gets a `.mv2` file — single-file, portable, append-only memory with time-travel debugging. QMD stays available for project-level doc search.

```
Per-Agent (Memvid .mv2)
├── agent-alpha.mv2    ← work decisions, code changes, reasoning
├── agent-beta.mv2     ← work history
└── Each file = complete rewindable memory timeline

Shared Project (QMD - optional)
├── collection: "project-docs"
└── collection: "meeting-notes"
```

### 5. PCK for hot-reloadable UI, never C#

PCK files contain .tscn scenes, GDScript, sprites, themes. C# lives in compiled DLLs only. GenUI component catalog is a PCK — update UI components without recompiling C#.

### 6. Contracts vs Plugins separation

- `project/contracts/` — C# projects for interfaces and model types only
- `project/plugins/` — C# projects for implementation and behavior
- `project/hosts/` — Godot project (viewport only), references plugin DLLs
- Dependency flow: hosts → plugins → contracts (strictly one-way)

### 7. Quicktype + Mapperly pipeline

- Quicktype generates C# serialization classes from JSON schemas at build time
- Mapperly generates object mapping code at compile time (source generator, no reflection, AOT-friendly)
- Wire types (quicktype) live in contracts; mappers (Mapperly) live in plugins

## Project Structure

```
project/
├── contracts/                              # Interfaces + Models (NO behavior)
│   ├── Contracts.Core/                     # Core interfaces
│   ├── Contracts.Protocol/                 # Wire format models (quicktype-generated)
│   │   ├── Aieos/                          # AIEOS v1.2 types
│   │   ├── A2A/                            # A2A protocol types
│   │   ├── AgUi/                           # AG-UI event types
│   │   ├── A2UI/                           # A2UI component types
│   │   ├── PiRpc/                          # Pi RPC message types
│   │   └── Memvid/                         # Memvid response types
│   ├── Contracts.Skills/                   # Skill system interfaces
│   └── Contracts.ECS/                      # ECS component definitions
│
├── plugins/                                # Implementations (ALL behavior)
│   ├── Plugin.Actors/                      # Akka.NET actor implementations
│   ├── Plugin.Process/                     # CliWrap process management
│   ├── Plugin.Protocol/                    # Protocol adapters
│   ├── Plugin.ECS/                         # Friflo ECS systems
│   └── Plugin.Mapping/                     # Mapperly mappers
│
└── hosts/
    └── complete-app/                       # Godot project (viewport only)
        ├── project.godot
        ├── complete-app.csproj
        ├── Scenes/
        ├── Scripts/                        # Thin Godot glue only
        └── PCK/                            # Hot-reloadable asset packs
```

## Naming Convention

- Folder/project names: short (e.g., `Contracts.Core`, `Plugin.Actors`)
- Namespace prefix: `GiantIsopod.*` (e.g., `GiantIsopod.Contracts.Core`)

## References

- Overstory: https://github.com/jayminwest/overstory
- Pixel Agents: https://github.com/pablodelucca/pixel-agents
- Pi (pi-mono): https://github.com/badlogic/pi-mono
- AIEOS: https://github.com/entitai/aieos
- Memvid: https://github.com/memvid/memvid
- QMD: https://github.com/tobi/qmd
- A2A: https://github.com/a2aproject/A2A
- AG-UI: https://github.com/ag-ui-protocol/ag-ui
- A2UI: https://a2ui.org / https://github.com/flutter/genui (Flutter reference)
- CliWrap: https://github.com/Tyrrrz/CliWrap
- Akka.NET: https://github.com/akkadotnet/akka.net
- Friflo.Engine.ECS: https://github.com/friflo/friflo.engine.ecs
- Mapperly: https://github.com/riok/mapperly
- Quicktype: https://github.com/glideapps/quicktype
- Microsoft Agent Framework: https://learn.microsoft.com/en-us/agent-framework/overview/
- Agent Skills spec: https://agentskills.io
- Godot PCK: https://docs.godotengine.org/en/stable/tutorials/export/exporting_pcks.html
- Swimming-tuna capability-based agents: swimming-tuna/.kiro/specs/capability-based-agents/requirements.md
