# ADR-008: Typed Artifact Registry

Date: 2026-03-02
Status: Proposed
Depends On: ADR-004

## Context

Giant-isopod agents currently return results as plain Akka messages (`TaskCompleted` with
a string `Result`). This works for single-agent text outputs but breaks down when:

1. **Multi-modal outputs** — The swarm will produce code, images, 3D models, audio, docs,
   and app builds. A string result cannot express type, format, validation status, or storage
   location uniformly.
2. **Provenance tracking** — There is no way to trace which task produced which output, or
   which outputs fed into downstream tasks. Debugging a failed pipeline requires grepping logs.
3. **Deduplication and caching** — Without content-addressable references, identical outputs
   get regenerated. Expensive artifacts (3D renders, large builds) waste compute.
4. **Downstream consumption** — Tasks that depend on earlier outputs must know the artifact's
   URI, type, and whether it passed validation. Today this is implicit.

The discussion in `docs/_inbox/discussion-001.md` proposes treating everything as a typed
artifact with a URI, hash, and validators. This ADR formalizes that into an actor.

## Decision

Introduce an `ArtifactRegistryActor` that stores typed artifact references with uniform
metadata, content hashes, provenance, and validator results. All task outputs flow through
the registry. Downstream tasks reference artifacts by ID, not by copying blobs.

## Design

### Artifact Model

```csharp
public record ArtifactRef(
    string ArtifactId,           // unique, e.g. "art-2026-03-02-001"
    ArtifactType Type,           // Code, Doc, Image, Audio, Model3D, App, Dataset, Config
    string Format,               // "cs", "md", "png", "glb", "wav", "zip", etc.
    string Uri,                  // storage pointer: file path, git ref, S3 URI, etc.
    string ContentHash,          // SHA-256 of content for dedup/verification
    ArtifactProvenance Provenance,
    IReadOnlyDictionary<string, string> Metadata,  // dimensions, duration, polycount, etc.
    IReadOnlyList<ValidatorResult> Validators
);

public enum ArtifactType { Code, Doc, Image, Audio, Model3D, App, Dataset, Config }

public record ArtifactProvenance(
    string TaskId,               // which task produced this
    string AgentId,              // which agent produced this
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> InputArtifactIds  // artifacts consumed to produce this
);

public record ValidatorResult(
    string ValidatorName,        // "compile", "unit-tests", "lint", "resolution-check"
    bool Passed,
    string Details               // error messages, metrics, etc.
);
```

### Messages

```csharp
// ── Artifact registration ──
public record RegisterArtifact(ArtifactRef Artifact);
public record ArtifactRegistered(string ArtifactId);

// ── Queries ──
public record GetArtifact(string ArtifactId);
public record GetArtifactsByTask(string TaskId);
public record GetArtifactsByType(ArtifactType Type);
public record ArtifactResult(ArtifactRef? Artifact);
public record ArtifactListResult(IReadOnlyList<ArtifactRef> Artifacts);

// ── Validation updates ──
public record UpdateValidation(string ArtifactId, ValidatorResult Result);
public record ArtifactValidationUpdated(string ArtifactId);

// ── Blessing (approved for downstream use) ──
public record BlessArtifact(string ArtifactId);
public record ArtifactBlessed(string ArtifactId);
```

### ArtifactRegistryActor

```
/user/artifacts — ArtifactRegistryActor
    State: Dictionary<ArtifactId, ArtifactRef>
    Indexes: by TaskId, by Type, by ContentHash (dedup)

    OnReceive:
      RegisterArtifact → store, check dedup by ContentHash, reply ArtifactRegistered
      GetArtifact → lookup by ID, reply ArtifactResult
      GetArtifactsByTask → lookup by index, reply ArtifactListResult
      UpdateValidation → append validator result, reply ArtifactValidationUpdated
      BlessArtifact → mark as blessed, publish ArtifactBlessed to EventStream
```

### Integration with Task Flow

`TaskCompleted` gains an optional artifact list:

```csharp
public record TaskCompleted(
    string TaskId,
    string Result,                           // existing text result (kept for compat)
    IReadOnlyList<ArtifactRef>? Artifacts    // NEW: typed outputs
);
```

When `TaskGraphActor` processes `TaskCompleted`:
1. For each artifact in the list, send `RegisterArtifact` to the registry.
2. Make artifact IDs available to downstream tasks via dependency resolution.
3. Downstream `TaskAssigned` includes `InputArtifacts: List<ArtifactRef>`.

### Storage Strategy

The registry stores **references**, not content. Actual storage is pluggable:

| Artifact Type | Default Storage | URI Format |
|---------------|----------------|------------|
| Code | Git commit | `git:<repo>#<sha>` |
| Doc | File system | `file:<path>` |
| Image/Audio/3D | File system or blob store | `file:<path>` or `blob:<key>` |
| App build | File system | `file:<path>` |

Content hash ensures the reference is still valid (detect tampering or stale refs).

## Trade-offs

### Why a dedicated actor (not inline in TaskGraphActor)

- **Single responsibility**: TaskGraphActor manages DAG scheduling. Artifact storage,
  dedup, and validation are orthogonal concerns.
- **Queryable**: Other actors (A2A, ViewportActor for UI) need artifact lookups without
  coupling to the task graph.
- **Persistence-ready**: When we add persistence, the registry can snapshot independently.

### Why not a database directly

- Keeping it in-actor-memory first is consistent with the rest of giant-isopod (working
  memory in actors, persistence layered later).
- The artifact count per session is small (hundreds, not millions). Dictionary is fine.

### Risk

- **Schema evolution**: Adding new artifact types or metadata fields. Mitigate with
  `IReadOnlyDictionary<string, string> Metadata` as a flexible extension point.
- **Large artifact lists**: If a task produces many small artifacts (e.g., code files),
  the message size grows. Mitigate by batching or referencing a manifest artifact.

## References

- ADR-004: Market-First Coordination (TaskCompleted flow)
- Discussion: `docs/_inbox/discussion-001.md` (artifact model, Section 4)
- Current `TaskGraphActor`: `Plugin.Actors/TaskGraphActor.cs`
