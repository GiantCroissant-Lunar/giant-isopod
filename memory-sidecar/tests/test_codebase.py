"""Tests for memory_sidecar.flows.codebase — chunking and filtering logic."""

from __future__ import annotations

from pathlib import Path

from memory_sidecar.chunking import split_simple as _split_simple
from memory_sidecar.flows.codebase import _should_include


class TestShouldInclude:
    """Tests for the _should_include file filter."""

    def _path(self, rel: str, root: str = "/project") -> tuple[Path, Path]:
        return Path(root) / rel, Path(root)

    def test_includes_python_file(self):
        path, root = self._path("src/main.py")
        assert _should_include(path, root) is True

    def test_includes_cs_file(self):
        path, root = self._path("contracts/Core.cs")
        assert _should_include(path, root) is True

    def test_excludes_non_code_extension(self):
        path, root = self._path("image.png")
        assert _should_include(path, root) is False

    def test_excludes_hidden_directory(self):
        path, root = self._path(".git/config")
        assert _should_include(path, root) is False

    def test_excludes_dotfile(self):
        path, root = self._path(".hidden/script.py")
        assert _should_include(path, root) is False

    def test_excludes_node_modules(self):
        path, root = self._path("node_modules/package/index.js")
        assert _should_include(path, root) is False

    def test_excludes_pycache(self):
        path, root = self._path("src/__pycache__/module.py")
        assert _should_include(path, root) is False

    def test_excludes_bin_directory(self):
        path, root = self._path("bin/Debug/net8.0/App.dll")
        assert _should_include(path, root) is False

    def test_excludes_obj_directory(self):
        path, root = self._path("obj/project.assets.json")
        assert _should_include(path, root) is False

    def test_case_insensitive_extension(self):
        path, root = self._path("Main.CS")
        assert _should_include(path, root) is True

    def test_deeply_nested_valid_file(self):
        path, root = self._path("src/core/utils/helpers.ts")
        assert _should_include(path, root) is True


class TestSplitSimple:
    """Tests for the _split_simple text chunker."""

    def test_small_content_single_chunk(self):
        content = "hello world"
        chunks = _split_simple(content, chunk_size=100, chunk_overlap=10)

        assert len(chunks) == 1
        assert chunks[0]["text"] == "hello world"
        assert chunks[0]["location"] == "0:0"

    def test_exact_chunk_size_single_chunk(self):
        content = "x" * 100
        chunks = _split_simple(content, chunk_size=100, chunk_overlap=10)

        assert len(chunks) == 1

    def test_large_content_produces_multiple_chunks(self):
        content = "line\n" * 300  # 1500 chars
        chunks = _split_simple(content, chunk_size=500, chunk_overlap=100)

        assert len(chunks) > 1

    def test_chunks_have_sequential_indices(self):
        content = "word " * 500  # 2500 chars
        chunks = _split_simple(content, chunk_size=500, chunk_overlap=100)

        for i, chunk in enumerate(chunks):
            idx = int(chunk["location"].split(":")[0])
            assert idx == i

    def test_chunks_preserve_all_content(self):
        # Verify no content is lost (allowing for overlap)
        content = "abcdefghij\n" * 100  # 1100 chars
        chunks = _split_simple(content, chunk_size=200, chunk_overlap=50)

        # Each character should appear in at least one chunk
        all_text = "".join(c["text"] for c in chunks)
        for char in set(content):
            if char.strip():  # skip whitespace-only
                assert char in all_text

    def test_whitespace_only_chunks_skipped(self):
        content = "text\n" + " " * 500 + "\nmore text"
        chunks = _split_simple(content, chunk_size=100, chunk_overlap=10)

        for chunk in chunks:
            assert chunk["text"].strip(), "Whitespace-only chunks should be skipped"

    def test_newline_aware_splitting(self):
        # With newlines, chunks should try to break at newline boundaries
        lines = ["x" * 80 + "\n" for _ in range(20)]
        content = "".join(lines)
        chunks = _split_simple(content, chunk_size=500, chunk_overlap=100)

        # At least one chunk should end with a newline (newline-aware boundary)
        any_newline_boundary = any(c["text"].endswith("\n") for c in chunks[:-1])
        assert any_newline_boundary, "Splitter should prefer newline boundaries"

    def test_overlap_is_applied(self):
        content = "a" * 1000
        chunks_no_overlap = _split_simple(content, chunk_size=300, chunk_overlap=0)
        chunks_with_overlap = _split_simple(content, chunk_size=300, chunk_overlap=100)

        # With overlap, we expect more chunks (since each step advances less)
        assert len(chunks_with_overlap) >= len(chunks_no_overlap)

    def test_location_format(self):
        content = "x" * 500
        chunks = _split_simple(content, chunk_size=200, chunk_overlap=50)

        for chunk in chunks:
            parts = chunk["location"].split(":")
            assert len(parts) == 2
            assert parts[0].isdigit()
            assert parts[1].isdigit()

    def test_empty_content(self):
        chunks = _split_simple("", chunk_size=100, chunk_overlap=10)
        assert len(chunks) == 1
        assert chunks[0]["text"] == ""
