"""Tests for memory_sidecar.storage — schema init, serialization, CRUD."""

from __future__ import annotations

import sqlite3
import struct

import pytest

from memory_sidecar.storage import (
    _serialize_vec,
    delete_stale_chunks,
    init_codebase_schema,
    init_knowledge_schema,
)


class TestSerializeVec:
    def test_round_trip(self):
        vec = [1.0, 2.5, -3.0, 0.0, 4.125]
        serialized = _serialize_vec(vec)

        # Deserialize manually
        count = len(serialized) // 4
        deserialized = list(struct.unpack(f"{count}f", serialized))

        assert deserialized == vec

    def test_output_size(self):
        vec = [0.0] * 384  # EMBED_DIMENSIONS
        serialized = _serialize_vec(vec)
        assert len(serialized) == 384 * 4  # 4 bytes per float32

    def test_empty_vector(self):
        vec: list[float] = []
        serialized = _serialize_vec(vec)
        assert serialized == b""

    def test_single_element(self):
        vec = [42.0]
        serialized = _serialize_vec(vec)
        deserialized = struct.unpack("1f", serialized)
        assert deserialized[0] == 42.0


class TestInitCodebaseSchema:
    def test_creates_code_chunks_table(self):
        conn = sqlite3.connect(":memory:")
        init_codebase_schema(conn)

        # Verify table exists
        tables = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='code_chunks'"
        ).fetchall()
        assert len(tables) == 1

    def test_creates_filename_index(self):
        conn = sqlite3.connect(":memory:")
        init_codebase_schema(conn)

        indexes = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='index' AND name='idx_code_chunks_filename'"
        ).fetchall()
        assert len(indexes) == 1

    def test_idempotent(self):
        conn = sqlite3.connect(":memory:")
        init_codebase_schema(conn)
        init_codebase_schema(conn)  # should not raise

    def test_unique_constraint_on_filename_location(self):
        conn = sqlite3.connect(":memory:")
        init_codebase_schema(conn)

        conn.execute(
            "INSERT INTO code_chunks (filename, location, language, code, updated_at) "
            "VALUES ('f.py', '0:0', 'python', 'code', '2024-01-01')"
        )
        # Duplicate should fail
        with pytest.raises(sqlite3.IntegrityError):
            conn.execute(
                "INSERT INTO code_chunks (filename, location, language, code, updated_at) "
                "VALUES ('f.py', '0:0', 'python', 'other', '2024-01-01')"
            )


class TestInitKnowledgeSchema:
    def test_creates_knowledge_table(self):
        conn = sqlite3.connect(":memory:")
        init_knowledge_schema(conn)

        tables = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='knowledge'"
        ).fetchall()
        assert len(tables) == 1

    def test_creates_category_index(self):
        conn = sqlite3.connect(":memory:")
        init_knowledge_schema(conn)

        indexes = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='index' AND name='idx_knowledge_category'"
        ).fetchall()
        assert len(indexes) == 1


class TestDeleteStaleChunks:
    def _setup_db(self) -> sqlite3.Connection:
        conn = sqlite3.connect(":memory:")
        init_codebase_schema(conn)
        # Insert some test chunks (skip vec table since _has_vec is False in test)
        for loc in ["0:0", "1:100", "2:200"]:
            conn.execute(
                "INSERT INTO code_chunks (filename, location, language, code, updated_at) "
                "VALUES (?, ?, 'python', 'code', '2024-01-01')",
                ("test.py", loc),
            )
        conn.commit()
        return conn

    def test_deletes_chunks_not_in_keep_set(self):
        conn = self._setup_db()

        # Keep only "0:0", delete "1:100" and "2:200"
        deleted = delete_stale_chunks(conn, "test.py", {"0:0"})

        assert deleted == 2
        remaining = conn.execute("SELECT location FROM code_chunks WHERE filename='test.py'").fetchall()
        assert len(remaining) == 1
        assert remaining[0][0] == "0:0"

    def test_empty_keep_set_deletes_all(self):
        conn = self._setup_db()

        deleted = delete_stale_chunks(conn, "test.py", set())

        assert deleted == 3
        remaining = conn.execute("SELECT location FROM code_chunks WHERE filename='test.py'").fetchall()
        assert len(remaining) == 0

    def test_all_kept_deletes_none(self):
        conn = self._setup_db()

        deleted = delete_stale_chunks(conn, "test.py", {"0:0", "1:100", "2:200"})

        assert deleted == 0

    def test_different_filename_not_affected(self):
        conn = self._setup_db()
        conn.execute(
            "INSERT INTO code_chunks (filename, location, language, code, updated_at) "
            "VALUES ('other.py', '0:0', 'python', 'code', '2024-01-01')"
        )
        conn.commit()

        deleted = delete_stale_chunks(conn, "test.py", set())

        assert deleted == 3
        remaining = conn.execute("SELECT COUNT(*) FROM code_chunks WHERE filename='other.py'").fetchone()
        assert remaining[0] == 1  # other.py untouched
