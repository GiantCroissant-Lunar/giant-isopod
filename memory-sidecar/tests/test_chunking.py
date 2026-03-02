"""Tests for memory_sidecar.chunking — split_simple, split_ast, and chunk_file."""

from __future__ import annotations

import pytest

from memory_sidecar.chunking import chunk_file, split_ast, split_simple


class TestSplitSimple:
    """Tests for the split_simple text chunker."""

    def test_small_content_single_chunk(self):
        content = "hello world"
        chunks = split_simple(content, chunk_size=100, chunk_overlap=10)

        assert len(chunks) == 1
        assert chunks[0]["text"] == "hello world"
        assert chunks[0]["location"] == "0:0"

    def test_exact_chunk_size_single_chunk(self):
        content = "x" * 100
        chunks = split_simple(content, chunk_size=100, chunk_overlap=10)

        assert len(chunks) == 1

    def test_large_content_produces_multiple_chunks(self):
        content = "line\n" * 300  # 1500 chars
        chunks = split_simple(content, chunk_size=500, chunk_overlap=100)

        assert len(chunks) > 1

    def test_chunks_have_sequential_indices(self):
        content = "word " * 500  # 2500 chars
        chunks = split_simple(content, chunk_size=500, chunk_overlap=100)

        for i, chunk in enumerate(chunks):
            idx = int(chunk["location"].split(":")[0])
            assert idx == i

    def test_chunks_preserve_all_content(self):
        content = "abcdefghij\n" * 100  # 1100 chars
        chunks = split_simple(content, chunk_size=200, chunk_overlap=50)

        all_text = "".join(c["text"] for c in chunks)
        for char in set(content):
            if char.strip():
                assert char in all_text

    def test_whitespace_only_chunks_skipped(self):
        content = "text\n" + " " * 500 + "\nmore text"
        chunks = split_simple(content, chunk_size=100, chunk_overlap=10)

        for chunk in chunks:
            assert chunk["text"].strip(), "Whitespace-only chunks should be skipped"

    def test_newline_aware_splitting(self):
        lines = ["x" * 80 + "\n" for _ in range(20)]
        content = "".join(lines)
        chunks = split_simple(content, chunk_size=500, chunk_overlap=100)

        any_newline_boundary = any(c["text"].endswith("\n") for c in chunks[:-1])
        assert any_newline_boundary, "Splitter should prefer newline boundaries"

    def test_overlap_is_applied(self):
        content = "a" * 1000
        chunks_no_overlap = split_simple(content, chunk_size=300, chunk_overlap=0)
        chunks_with_overlap = split_simple(content, chunk_size=300, chunk_overlap=100)

        assert len(chunks_with_overlap) >= len(chunks_no_overlap)

    def test_location_format(self):
        content = "x" * 500
        chunks = split_simple(content, chunk_size=200, chunk_overlap=50)

        for chunk in chunks:
            parts = chunk["location"].split(":")
            assert len(parts) == 2
            assert parts[0].isdigit()
            assert parts[1].isdigit()

    def test_empty_content(self):
        chunks = split_simple("", chunk_size=100, chunk_overlap=10)
        assert len(chunks) == 1
        assert chunks[0]["text"] == ""


class TestSplitAst:
    """Tests for the split_ast tree-sitter chunker."""

    def test_python_single_function(self):
        code = "def hello():\n    print('hi')\n"
        result = split_ast(code, "python", chunk_size=1000, chunk_overlap=0)
        assert result is not None
        assert len(result) == 1
        assert "def hello" in result[0]["text"]

    def test_python_multiple_functions_fit_in_one_chunk(self):
        code = "def foo():\n    pass\n\ndef bar():\n    pass\n"
        result = split_ast(code, "python", chunk_size=1000, chunk_overlap=0)
        assert result is not None
        assert len(result) == 1
        assert "def foo" in result[0]["text"]
        assert "def bar" in result[0]["text"]

    def test_python_functions_split_when_exceeding_size(self):
        func1 = "def foo():\n" + "    x = 1\n" * 50  # ~350 chars
        func2 = "def bar():\n" + "    y = 2\n" * 50  # ~350 chars
        code = func1 + "\n" + func2
        result = split_ast(code, "python", chunk_size=400, chunk_overlap=0)
        assert result is not None
        assert len(result) == 2
        assert "def foo" in result[0]["text"]
        assert "def bar" in result[1]["text"]

    def test_python_class_kept_intact(self):
        code = "class MyClass:\n    def method(self):\n        pass\n"
        result = split_ast(code, "python", chunk_size=1000, chunk_overlap=0)
        assert result is not None
        assert len(result) == 1
        assert "class MyClass" in result[0]["text"]

    def test_python_imports_grouped(self):
        code = "import os\nimport sys\n\ndef main():\n    pass\n"
        result = split_ast(code, "python", chunk_size=1000, chunk_overlap=0)
        assert result is not None
        assert len(result) == 1
        assert "import os" in result[0]["text"]

    def test_returns_none_for_unsupported_language(self):
        result = split_ast("some content", "brainfuck", chunk_size=100, chunk_overlap=0)
        assert result is None

    def test_typescript_function(self):
        code = "function greet(name: string): string {\n  return `Hello, ${name}`;\n}\n"
        result = split_ast(code, "typescript", chunk_size=1000, chunk_overlap=0)
        assert result is not None
        assert len(result) >= 1
        assert "function greet" in result[0]["text"]

    def test_gdscript_functions(self):
        code = "func _ready():\n\tpass\n\nfunc _process(delta):\n\tpass\n"
        result = split_ast(code, "gdscript", chunk_size=1000, chunk_overlap=0)
        assert result is not None
        assert len(result) >= 1

    def test_location_format_start_byte(self):
        code = "def foo():\n    pass\n\ndef bar():\n    pass\n"
        result = split_ast(code, "python", chunk_size=20, chunk_overlap=0)
        assert result is not None
        for chunk in result:
            parts = chunk["location"].split(":")
            assert len(parts) == 2
            assert parts[0].isdigit()
            assert parts[1].isdigit()

    def test_empty_file_falls_back_to_simple(self):
        result = split_ast("", "python", chunk_size=100, chunk_overlap=0)
        assert result is not None
        # Empty content → split_simple returns single chunk
        assert len(result) == 1

    def test_large_single_definition_not_split(self):
        # A single large function should stay as one chunk even if > chunk_size
        body = "    x = 1\n" * 200  # ~2000 chars
        code = f"def big():\n{body}"
        result = split_ast(code, "python", chunk_size=500, chunk_overlap=0)
        assert result is not None
        assert len(result) == 1
        assert "def big" in result[0]["text"]

    @pytest.mark.parametrize("language", ["python", "rust", "typescript", "javascript", "gdscript"])
    def test_available_languages_parse_without_error(self, language):
        # Smoke test: parsing trivial code shouldn't crash
        code = "x = 1\n"
        result = split_ast(code, language, chunk_size=100, chunk_overlap=0)
        # Should either return chunks or None (if language unavailable)
        if result is not None:
            assert isinstance(result, list)


class TestChunkFile:
    """Tests for the chunk_file dispatch function."""

    def test_uses_ast_for_python(self):
        code = "def hello():\n    print('hi')\n"
        result = chunk_file(code, "python", chunk_size=1000, chunk_overlap=0)
        assert len(result) >= 1
        assert "def hello" in result[0]["text"]

    def test_falls_back_for_none_language(self):
        text = "just some plain text content"
        result = chunk_file(text, None, chunk_size=1000, chunk_overlap=0)
        assert len(result) == 1
        assert result[0]["text"] == text

    def test_falls_back_for_unsupported_language(self):
        text = "some content"
        result = chunk_file(text, "brainfuck", chunk_size=1000, chunk_overlap=0)
        assert len(result) == 1
        assert result[0]["text"] == text

    def test_falls_back_for_c_sharp(self):
        # c_sharp is not in tree-sitter-language-pack, should fall back to simple
        code = "class Foo { void Bar() {} }"
        result = chunk_file(code, "c_sharp", chunk_size=1000, chunk_overlap=0)
        assert len(result) >= 1
