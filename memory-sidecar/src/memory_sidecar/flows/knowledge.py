"""Knowledge store: per-agent long-term memory with vector search."""

from __future__ import annotations

from pathlib import Path

from memory_sidecar.embed import embed_one
from memory_sidecar.storage import connect, init_knowledge_schema, insert_knowledge, search_knowledge


def store(
    db_path: str,
    content: str,
    category: str,
    tags: dict[str, str] | None = None,
) -> int:
    """Store a knowledge entry with embedding. Returns the row id."""
    conn = connect(Path(db_path))
    init_knowledge_schema(conn)
    embedding = embed_one(content)
    row_id = insert_knowledge(conn, content, category, tags, embedding)
    conn.commit()
    conn.close()
    return row_id


def query(
    db_path: str,
    query_text: str,
    category: str | None = None,
    top_k: int = 10,
) -> list[dict]:
    """Search knowledge entries by semantic similarity."""
    conn = connect(Path(db_path))
    init_knowledge_schema(conn)
    results = search_knowledge(conn, embed_one(query_text), category, top_k)
    conn.close()
    return results
