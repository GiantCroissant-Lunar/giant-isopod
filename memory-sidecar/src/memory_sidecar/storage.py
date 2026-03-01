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
    """Create the knowledge entries table with virtual vec0 table for vectors."""
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
    conn.execute("CREATE INDEX IF NOT EXISTS idx_knowledge_category ON knowledge(category)")
    conn.commit()


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
    # sqlite-vec does not support arbitrary WHERE predicates alongside MATCH + k = ?,
    # so we over-fetch and post-filter by category in Python.
    effective_top_k = top_k * 3 if category else top_k
    rows = conn.execute(
        """SELECT v.id, v.distance, k.content, k.category, k.tags, k.stored_at
           FROM knowledge_vec v
           JOIN knowledge k ON k.id = v.id
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
    conn.execute(f"DELETE FROM code_chunks_vec WHERE id IN ({id_ph})", ids)  # noqa: S608
    conn.execute(f"DELETE FROM code_chunks WHERE id IN ({id_ph})", ids)  # noqa: S608
    return len(ids)
