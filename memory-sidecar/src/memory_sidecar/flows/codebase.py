"""Codebase indexing: read source files, chunk, embed with FastEmbed, store in SQLite."""

from __future__ import annotations

from pathlib import Path

from memory_sidecar.chunking import split_simple
from memory_sidecar.config import CODE_EXTENSIONS, DEFAULT_CHUNK_OVERLAP, DEFAULT_CHUNK_SIZE, EXCLUDED_PATTERNS
from memory_sidecar.embed import embed_texts
from memory_sidecar.storage import connect, delete_stale_chunks, init_codebase_schema, upsert_code_chunk

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
    init_codebase_schema(conn)

    stats = {"files_processed": 0, "chunks_indexed": 0, "chunks_deleted": 0}
    files = sorted(p for p in source_root.rglob("*") if p.is_file() and _should_include(p, source_root))

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

        chunks = split_simple(content, chunk_size, chunk_overlap)
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
