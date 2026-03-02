"""Configuration defaults and path helpers."""

from __future__ import annotations

import os
from pathlib import Path

# ── Embedding model ──
DEFAULT_EMBED_MODEL = "BAAI/bge-small-en-v1.5"
EMBED_DIMENSIONS = 384

# ── Chunking ──
DEFAULT_CHUNK_SIZE = 1000
DEFAULT_CHUNK_OVERLAP = 300

# ── Code file patterns ──
CODE_EXTENSIONS = frozenset(
    {
        ".cs",
        ".py",
        ".rs",
        ".ts",
        ".js",
        ".tsx",
        ".jsx",
        ".md",
        ".mdx",
        ".toml",
        ".json",
        ".yaml",
        ".yml",
        ".gdscript",
        ".tscn",
        ".cfg",
        ".csproj",
        ".sln",
    }
)

# ── Document file patterns (for Docling conversion) ──
DOC_EXTENSIONS = frozenset({".pdf", ".docx", ".pptx", ".xlsx", ".html"})

EXCLUDED_PATTERNS = frozenset(
    {
        ".*",
        "bin",
        "obj",
        "node_modules",
        "target",
        "__pycache__",
        ".git",
        ".godot",
        "build/_artifacts",
        "addons",
    }
)


def data_dir() -> Path:
    """Resolve the memory data directory. Respects MEMORY_SIDECAR_DATA_DIR env var."""
    return Path(os.environ.get("MEMORY_SIDECAR_DATA_DIR", "data/memory"))


def codebase_db_path() -> Path:
    return data_dir() / "codebase.sqlite"


def knowledge_db_path(agent_id: str | None = None) -> Path:
    if agent_id:
        return data_dir() / "knowledge" / f"{agent_id}.sqlite"
    return data_dir() / "knowledge" / "shared.sqlite"
