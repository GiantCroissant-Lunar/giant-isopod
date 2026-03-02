"""Document indexing: convert rich documents (PDF, DOCX, etc.) to markdown, chunk, embed, store."""

from __future__ import annotations

import logging
from pathlib import Path

from memory_sidecar.chunking import split_simple
from memory_sidecar.config import DEFAULT_CHUNK_OVERLAP, DEFAULT_CHUNK_SIZE, DOC_EXTENSIONS
from memory_sidecar.embed import embed_texts
from memory_sidecar.storage import connect, delete_stale_chunks, init_codebase_schema, upsert_code_chunk

logger = logging.getLogger(__name__)


def _convert_document(path: Path) -> str:
    """Convert a document to markdown using Docling.

    Raises ImportError if docling is not installed.
    """
    from docling.document_converter import DocumentConverter

    converter = DocumentConverter()
    result = converter.convert(str(path))
    return result.document.export_to_markdown()


def _should_include_doc(path: Path) -> bool:
    """Check if a file has a supported document extension."""
    return path.suffix.lower() in DOC_EXTENSIONS


def index_documents(
    docs_path: str,
    db_path: str,
    chunk_size: int = DEFAULT_CHUNK_SIZE,
    chunk_overlap: int = DEFAULT_CHUNK_OVERLAP,
    batch_size: int = 32,
) -> dict:
    """Index a documents directory into SQLite with vector embeddings.

    Converts each document to markdown via Docling, then chunks and embeds.
    Uses the same code_chunks table with language='document'.

    Returns stats dict with files_processed, files_skipped, chunks_indexed, chunks_deleted.
    """
    docs_root = Path(docs_path).resolve()
    if not docs_root.is_dir():
        raise FileNotFoundError(f"Docs path not found: {docs_root}")

    conn = connect(Path(db_path))
    init_codebase_schema(conn)

    stats = {"files_processed": 0, "files_skipped": 0, "chunks_indexed": 0, "chunks_deleted": 0}

    files = sorted(p for p in docs_root.rglob("*") if p.is_file() and _should_include_doc(p))

    pending: list[tuple[str, str, str, str]] = []  # (filename, location, lang, text)
    pending_texts: list[str] = []

    for file_path in files:
        rel = str(file_path.relative_to(docs_root)).replace("\\", "/")
        try:
            content = _convert_document(file_path)
        except Exception:
            logger.warning("Failed to convert %s, skipping", rel, exc_info=True)
            stats["files_skipped"] += 1
            continue

        if not content.strip():
            stats["files_skipped"] += 1
            continue

        chunks = split_simple(content, chunk_size, chunk_overlap)
        keep = {c["location"] for c in chunks}
        stats["chunks_deleted"] += delete_stale_chunks(conn, rel, keep)
        stats["files_processed"] += 1

        for c in chunks:
            pending.append((rel, c["location"], "document", c["text"]))
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
