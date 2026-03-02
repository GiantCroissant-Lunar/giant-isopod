"""Codebase indexing: read source files, chunk, embed with FastEmbed, store in SQLite."""

from __future__ import annotations

import logging
import os
from pathlib import Path

from memory_sidecar.chunking import CHUNKER_VERSION, chunk_file
from memory_sidecar.config import CODE_EXTENSIONS, DEFAULT_CHUNK_OVERLAP, DEFAULT_CHUNK_SIZE, EXCLUDED_PATTERNS
from memory_sidecar.embed import embed_texts
from memory_sidecar.storage import (
    connect,
    delete_stale_chunks,
    get_metadata,
    init_codebase_schema,
    init_metadata_schema,
    purge_all_code_chunks,
    set_metadata,
    upsert_code_chunk,
)

logger = logging.getLogger(__name__)

# Map file extensions to Tree-sitter language names
_EXT_MAP = {
    ".py": "python",
    ".cs": "c_sharp",
    ".rs": "rust",
    ".ts": "typescript",
    ".tsx": "tsx",
    ".js": "javascript",
    ".jsx": "javascript",
    ".md": "markdown",
    ".mdx": "markdown",
    ".json": "json",
    ".toml": "toml",
    ".yaml": "yaml",
    ".yml": "yaml",
    ".gdscript": "gdscript",
}


def _should_include(path: Path, source_root: Path) -> bool:
    """Check if a file should be included in indexing."""
    rel = path.relative_to(source_root)
    for part in rel.parts:
        if part in EXCLUDED_PATTERNS or part.startswith("."):
            return False
    return path.suffix.lower() in CODE_EXTENSIONS


def _walk_source_files(source_root: Path) -> list[Path]:
    """Walk source tree, pruning excluded directories in-place for performance."""
    result: list[Path] = []
    for dirpath, dirnames, filenames in os.walk(source_root):
        # Prune excluded dirs in-place to prevent descent
        dirnames[:] = sorted(d for d in dirnames if d not in EXCLUDED_PATTERNS and not d.startswith("."))
        for fname in sorted(filenames):
            file_path = Path(dirpath) / fname
            if _should_include(file_path, source_root):
                result.append(file_path)
    return result


def index_codebase(
    source_path: str,
    db_path: str,
    chunk_size: int = DEFAULT_CHUNK_SIZE,
    chunk_overlap: int = DEFAULT_CHUNK_OVERLAP,
    batch_size: int = 32,
) -> dict:
    """Index a codebase directory into SQLite with vector embeddings.

    Returns stats dict with files_processed, chunks_indexed, chunks_deleted.
    """
    source_root = Path(source_path).resolve()
    if not source_root.is_dir():
        raise FileNotFoundError(f"Source path not found: {source_root}")

    conn = connect(Path(db_path))
    init_metadata_schema(conn)
    init_codebase_schema(conn)

    stats = {"files_processed": 0, "chunks_indexed": 0, "chunks_deleted": 0, "chunks_purged": 0}

    # Check chunker version — purge all chunks on mismatch to avoid stale vec0 entries
    stored_version = get_metadata(conn, "chunker_version")
    if stored_version != CHUNKER_VERSION:
        purged = purge_all_code_chunks(conn)
        if purged:
            logger.info(
                "Chunker version changed (%s -> %s), purged %d stale chunks", stored_version, CHUNKER_VERSION, purged
            )
            stats["chunks_purged"] = purged
        set_metadata(conn, "chunker_version", CHUNKER_VERSION)
        conn.commit()

    files = _walk_source_files(source_root)

    pending: list[tuple[str, str, str | None, str]] = []  # (filename, location, lang, code)
    pending_texts: list[str] = []

    for file_path in files:
        rel = str(file_path.relative_to(source_root)).replace("\\", "/")
        lang = _EXT_MAP.get(file_path.suffix.lower(), file_path.suffix.lstrip("."))
        try:
            content = file_path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        if not content.strip():
            continue

        ts_lang = _EXT_MAP.get(file_path.suffix.lower())
        chunks = chunk_file(content, ts_lang, chunk_size, chunk_overlap)
        keep = {c["location"] for c in chunks}
        stats["chunks_deleted"] += delete_stale_chunks(conn, rel, keep)
        stats["files_processed"] += 1

        for c in chunks:
            pending.append((rel, c["location"], lang, c["text"]))
            pending_texts.append(c["text"])

        if len(pending_texts) >= batch_size:
            _flush(conn, pending, pending_texts)
            stats["chunks_indexed"] += len(pending_texts)
            pending.clear()
            pending_texts.clear()

    if pending_texts:
        _flush(conn, pending, pending_texts)
        stats["chunks_indexed"] += len(pending_texts)

    conn.commit()
    conn.close()
    return stats


def _flush(conn, chunks, texts):
    embeddings = embed_texts(texts)
    for (fn, loc, lang, code), emb in zip(chunks, embeddings, strict=True):
        upsert_code_chunk(conn, fn, loc, lang, code, emb)


def search_codebase(db_path: str, query: str, top_k: int = 10) -> list[dict]:
    """Search the codebase index by semantic similarity."""
    from memory_sidecar.embed import embed_one
    from memory_sidecar.storage import search_code

    conn = connect(Path(db_path))
    results = search_code(conn, embed_one(query), top_k)
    conn.close()
    return results
