"""Tests for memory_sidecar.config path helpers."""

from __future__ import annotations

import os
from pathlib import Path
from unittest import mock

from memory_sidecar.config import (
    CODE_EXTENSIONS,
    EXCLUDED_PATTERNS,
    codebase_db_path,
    data_dir,
    knowledge_db_path,
)


class TestDataDir:
    def test_default_path(self):
        with mock.patch.dict(os.environ, {}, clear=True):
            result = data_dir()
            assert result == Path("data/memory")

    def test_respects_env_var(self):
        with mock.patch.dict(os.environ, {"MEMORY_SIDECAR_DATA_DIR": "/custom/path"}):
            result = data_dir()
            assert result == Path("/custom/path")


class TestCodebaseDbPath:
    def test_returns_sqlite_under_data_dir(self):
        with mock.patch.dict(os.environ, {"MEMORY_SIDECAR_DATA_DIR": "/tmp/test-mem"}):
            result = codebase_db_path()
            assert result == Path("/tmp/test-mem/codebase.sqlite")


class TestKnowledgeDbPath:
    def test_with_agent_id(self):
        with mock.patch.dict(os.environ, {"MEMORY_SIDECAR_DATA_DIR": "/tmp/test-mem"}):
            result = knowledge_db_path("agent-42")
            assert result == Path("/tmp/test-mem/knowledge/agent-42.sqlite")

    def test_none_agent_returns_shared(self):
        with mock.patch.dict(os.environ, {"MEMORY_SIDECAR_DATA_DIR": "/tmp/test-mem"}):
            result = knowledge_db_path(None)
            assert result == Path("/tmp/test-mem/knowledge/shared.sqlite")

    def test_empty_string_returns_shared(self):
        with mock.patch.dict(os.environ, {"MEMORY_SIDECAR_DATA_DIR": "/tmp/test-mem"}):
            result = knowledge_db_path("")
            assert result == Path("/tmp/test-mem/knowledge/shared.sqlite")


class TestConstants:
    def test_code_extensions_contains_cs(self):
        assert ".cs" in CODE_EXTENSIONS

    def test_code_extensions_contains_py(self):
        assert ".py" in CODE_EXTENSIONS

    def test_excluded_patterns_contains_node_modules(self):
        assert "node_modules" in EXCLUDED_PATTERNS

    def test_excluded_patterns_contains_pycache(self):
        assert "__pycache__" in EXCLUDED_PATTERNS
