"""SQLite + sqlite-vec storage helpers for vector search."""

from __future__ import annotations

import json
import sqlite3
import struct
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from memory_sidecar.config import EMBED_DIMENSIONS

_has_vec = False


def _ensure_vec(conn: sqlite3.Connection) -> bool:
    """Try to load sqlite-vec extension. Returns True if available."""
    global _has_vec
    try:
        import sqlite_vec

        conn.enable_load_extension(True)
        sqlite_vec.load(conn)
        conn.enable_load_extension(False)
        _has_vec = True
        return True
    except Exception:
        return False


def connect(db_path: Path) -> sqlite3.Connection:
    """Open a SQLite connection with sqlite-vec loaded if available."""
    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(db_path))
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA synchronous=NORMAL")
    _ensure_vec(conn)
    return conn


def init_codebase_schema(conn: sqlite3.Connection) -> None:
    """Create the codebase chunks table with virtual vec0 table for vectors."""
    conn.execute("""
        CREATE TABLE IF NOT EXISTS code_chunks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            filename TEXT NOT NULL,
            location TEXT NOT NULL,
            language TEXT,
            code TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(filename, location)
        )
    """)
    if _has_vec:
        conn.execute(f"""
            CREATE VIRTUAL TABLE IF NOT EXISTS code_chunks_vec USING vec0(
                id INTEGER PRIMARY KEY,
                embedding FLOAT[{EMBED_DIMENSIONS}]
            )
        """)
    conn.execute("CREATE INDEX IF NOT EXISTS idx_code_chunks_filename ON code_chunks(filename)")
    conn.commit()


def init_knowledge_schema(conn: sqlite3.Connection) -> None:
    """Create the knowledge entries table with virtual vec0 table for vectors and FTS5 for full-text."""
    conn.execute("""
        CREATE TABLE IF NOT EXISTS knowledge (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            content TEXT NOT NULL,
            category TEXT NOT NULL,
            tags TEXT,
            stored_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        )
    """)
    if _has_vec:
        conn.execute(f"""
            CREATE VIRTUAL TABLE IF NOT EXISTS knowledge_vec USING vec0(
                id INTEGER PRIMARY KEY,
                embedding FLOAT[{EMBED_DIMENSIONS}]
            )
        """)
    # Check if FTS table already exists before creating it
    fts_exists = (
        conn.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='knowledge_fts'").fetchone() is not None
    )
    # FTS5 full-text index for keyword search
    conn.execute("""
        CREATE VIRTUAL TABLE IF NOT EXISTS knowledge_fts USING fts5(
            content, category, content='knowledge', content_rowid='id'
        )
    """)
    # Triggers to keep FTS5 in sync with the knowledge table
    conn.executescript("""
        CREATE TRIGGER IF NOT EXISTS knowledge_ai AFTER INSERT ON knowledge BEGIN
            INSERT INTO knowledge_fts(rowid, content, category)
            VALUES (new.id, new.content, new.category);
        END;
        CREATE TRIGGER IF NOT EXISTS knowledge_ad AFTER DELETE ON knowledge BEGIN
            INSERT INTO knowledge_fts(knowledge_fts, rowid, content, category)
            VALUES ('delete', old.id, old.content, old.category);
        END;
        CREATE TRIGGER IF NOT EXISTS knowledge_au AFTER UPDATE ON knowledge BEGIN
            INSERT INTO knowledge_fts(knowledge_fts, rowid, content, category)
            VALUES ('delete', old.id, old.content, old.category);
            INSERT INTO knowledge_fts(rowid, content, category)
            VALUES (new.id, new.content, new.category);
        END;
    """)
    conn.execute("CREATE INDEX IF NOT EXISTS idx_knowledge_category ON knowledge(category)")
    if not fts_exists:
        # Rebuild FTS5 index only on first creation to pick up any pre-existing rows
        # that were inserted before the FTS5 table/triggers existed.
        conn.execute("INSERT INTO knowledge_fts(knowledge_fts) VALUES ('rebuild')")
    conn.commit()


def init_metadata_schema(conn: sqlite3.Connection) -> None:
    """Create a simple key-value metadata table."""
    conn.execute("""
        CREATE TABLE IF NOT EXISTS metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        )
    """)
    conn.commit()


def get_metadata(conn: sqlite3.Connection, key: str) -> str | None:
    """Get a metadata value by key, or None if not set."""
    row = conn.execute("SELECT value FROM metadata WHERE key = ?", (key,)).fetchone()
    return row[0] if row else None


def set_metadata(conn: sqlite3.Connection, key: str, value: str) -> None:
    """Set a metadata key-value pair (upsert)."""
    conn.execute(
        "INSERT INTO metadata (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
        (key, value),
    )


def purge_all_code_chunks(conn: sqlite3.Connection) -> int:
    """Delete all code chunks and their vec0 embeddings. Returns count deleted."""
    count = conn.execute("SELECT COUNT(*) FROM code_chunks").fetchone()[0]
    if count == 0:
        return 0
    if _has_vec:
        conn.execute("DELETE FROM code_chunks_vec")
    conn.execute("DELETE FROM code_chunks")
    return count


def _serialize_vec(vec: list[float]) -> bytes:
    """Serialize a float vector to bytes for sqlite-vec."""
    return struct.pack(f"{len(vec)}f", *vec)


def upsert_code_chunk(
    conn: sqlite3.Connection,
    filename: str,
    location: str,
    language: str | None,
    code: str,
    embedding: list[float],
) -> None:
    """Insert or update a code chunk and its embedding vector."""
    now = datetime.now(UTC).isoformat()
    cur = conn.execute(
        """INSERT INTO code_chunks (filename, location, language, code, updated_at)
           VALUES (?, ?, ?, ?, ?)
           ON CONFLICT(filename, location) DO UPDATE SET
               language=excluded.language, code=excluded.code, updated_at=excluded.updated_at
           RETURNING id""",
        (filename, location, language, code, now),
    )
    row_id = cur.fetchone()[0]
    if _has_vec:
        conn.execute(
            "INSERT OR REPLACE INTO code_chunks_vec (id, embedding) VALUES (?, ?)",
            (row_id, _serialize_vec(embedding)),
        )


def search_code(
    conn: sqlite3.Connection,
    query_embedding: list[float],
    top_k: int = 10,
) -> list[dict[str, Any]]:
    """Search code chunks by vector similarity."""
    if not _has_vec:
        return []
    rows = conn.execute(
        """SELECT v.id, v.distance, c.filename, c.location, c.language, c.code
           FROM code_chunks_vec v
           JOIN code_chunks c ON c.id = v.id
           WHERE v.embedding MATCH ? AND k = ?
           ORDER BY v.distance""",
        (_serialize_vec(query_embedding), top_k),
    ).fetchall()
    return [{"filename": r[2], "location": r[3], "language": r[4], "code": r[5], "score": 1.0 - r[1]} for r in rows]


def insert_knowledge(
    conn: sqlite3.Connection,
    content: str,
    category: str,
    tags: dict[str, str] | None,
    embedding: list[float],
) -> int:
    """Insert a knowledge entry and its embedding. Returns the row id."""
    now = datetime.now(UTC).isoformat()
    tags_json = json.dumps(tags) if tags else None
    cur = conn.execute(
        """INSERT INTO knowledge (content, category, tags, stored_at, updated_at)
           VALUES (?, ?, ?, ?, ?)""",
        (content, category, tags_json, now, now),
    )
    row_id = cur.lastrowid
    if _has_vec:
        conn.execute(
            "INSERT INTO knowledge_vec (id, embedding) VALUES (?, ?)",
            (row_id, _serialize_vec(embedding)),
        )
    return row_id


def search_knowledge(
    conn: sqlite3.Connection,
    query_embedding: list[float],
    category: str | None = None,
    top_k: int = 10,
) -> list[dict[str, Any]]:
    """Search knowledge entries by vector similarity, optionally filtered by category."""
    if not _has_vec:
        return []
    # sqlite-vec does not support arbitrary WHERE predicates alongside MATCH + k = ?,
    # so we over-fetch and post-filter by category in Python.
    effective_top_k = top_k * 3 if category else top_k
    rows = conn.execute(
        """SELECT v.id, v.distance, kn.content, kn.category, kn.tags, kn.stored_at
           FROM knowledge_vec v
           JOIN knowledge kn ON kn.id = v.id
           WHERE v.embedding MATCH ? AND k = ?
           ORDER BY v.distance""",
        (_serialize_vec(query_embedding), effective_top_k),
    ).fetchall()
    if category:
        rows = [r for r in rows if r[3] == category][:top_k]
    return [
        {
            "content": r[2],
            "category": r[3],
            "tags": json.loads(r[4]) if r[4] else None,
            "stored_at": r[5],
            "relevance": 1.0 - r[1],
        }
        for r in rows
    ]


def search_knowledge_fts(
    conn: sqlite3.Connection,
    query_text: str,
    category: str | None = None,
    top_k: int = 10,
) -> list[dict[str, Any]]:
    """Search knowledge entries using FTS5 full-text search."""
    # Escape double-quotes in query to prevent FTS5 syntax errors
    escaped = query_text.replace('"', '""')
    fts_query = f'"{escaped}"'
    if category:
        rows = conn.execute(
            """SELECT kn.id, rank, kn.content, kn.category, kn.tags, kn.stored_at
               FROM knowledge_fts fts
               JOIN knowledge kn ON kn.id = fts.rowid
               WHERE knowledge_fts MATCH ? AND kn.category = ?
               ORDER BY rank
               LIMIT ?""",
            (fts_query, category, top_k),
        ).fetchall()
    else:
        rows = conn.execute(
            """SELECT kn.id, rank, kn.content, kn.category, kn.tags, kn.stored_at
               FROM knowledge_fts fts
               JOIN knowledge kn ON kn.id = fts.rowid
               WHERE knowledge_fts MATCH ?
               ORDER BY rank
               LIMIT ?""",
            (fts_query, top_k),
        ).fetchall()
    return [
        {
            "id": r[0],
            "content": r[2],
            "category": r[3],
            "tags": json.loads(r[4]) if r[4] else None,
            "stored_at": r[5],
            "relevance": 0.0,  # FTS rank is negative; normalized during RRF merge
        }
        for r in rows
    ]


def search_knowledge_hybrid(
    conn: sqlite3.Connection,
    query_embedding: list[float],
    query_text: str,
    category: str | None = None,
    top_k: int = 10,
    rrf_k: int = 60,
) -> list[dict[str, Any]]:
    """Hybrid search combining vector similarity and FTS5 with reciprocal rank fusion.

    Uses RRF formula: score(d) = sum(1 / (k + rank_i)) across both result lists.
    The rrf_k constant (default 60) controls how much lower-ranked results contribute.
    """
    vec_results = search_knowledge(conn, query_embedding, category, top_k * 2)
    fts_results = search_knowledge_fts(conn, query_text, category, top_k * 2)

    # Build RRF scores keyed by (content, category) as a stable identifier
    scores: dict[str, float] = {}
    entries: dict[str, dict[str, Any]] = {}

    for rank, entry in enumerate(vec_results):
        key = f"{entry['stored_at']}:{entry['content'][:80]}"
        scores[key] = scores.get(key, 0.0) + 1.0 / (rrf_k + rank + 1)
        entries[key] = entry

    for rank, entry in enumerate(fts_results):
        key = f"{entry['stored_at']}:{entry['content'][:80]}"
        scores[key] = scores.get(key, 0.0) + 1.0 / (rrf_k + rank + 1)
        if key not in entries:
            entries[key] = entry

    # Sort by RRF score descending, take top_k
    ranked = sorted(scores.items(), key=lambda x: x[1], reverse=True)[:top_k]
    return [{**entries[key], "relevance": score} for key, score in ranked]


def delete_stale_chunks(conn: sqlite3.Connection, filename: str, keep_locations: set[str]) -> int:
    """Remove chunks for a file that are no longer present. Returns count deleted."""
    if not keep_locations:
        cur = conn.execute("SELECT id FROM code_chunks WHERE filename = ?", (filename,))
    else:
        placeholders = ",".join("?" for _ in keep_locations)
        cur = conn.execute(
            f"SELECT id FROM code_chunks WHERE filename = ? AND location NOT IN ({placeholders})",  # noqa: S608
            (filename, *keep_locations),
        )
    ids = [r[0] for r in cur.fetchall()]
    if not ids:
        return 0
    id_ph = ",".join("?" for _ in ids)
    if _has_vec:
        conn.execute(f"DELETE FROM code_chunks_vec WHERE id IN ({id_ph})", ids)  # noqa: S608
    conn.execute(f"DELETE FROM code_chunks WHERE id IN ({id_ph})", ids)  # noqa: S608
    return len(ids)
