"""Tests for memory_sidecar.flows.documents — document conversion and indexing."""

from __future__ import annotations

from pathlib import Path
from unittest.mock import patch

import pytest

from memory_sidecar.flows.documents import _should_include_doc, index_documents


class TestShouldIncludeDoc:
    """Tests for the _should_include_doc file filter."""

    def test_includes_pdf(self):
        assert _should_include_doc(Path("report.pdf")) is True

    def test_includes_docx(self):
        assert _should_include_doc(Path("spec.docx")) is True

    def test_includes_pptx(self):
        assert _should_include_doc(Path("slides.pptx")) is True

    def test_includes_xlsx(self):
        assert _should_include_doc(Path("data.xlsx")) is True

    def test_includes_html(self):
        assert _should_include_doc(Path("page.html")) is True

    def test_excludes_python(self):
        assert _should_include_doc(Path("script.py")) is False

    def test_excludes_markdown(self):
        assert _should_include_doc(Path("readme.md")) is False

    def test_excludes_image(self):
        assert _should_include_doc(Path("photo.png")) is False

    def test_case_insensitive(self):
        assert _should_include_doc(Path("Report.PDF")) is True


class TestConvertDocumentImportError:
    """Test graceful handling when docling is not installed."""

    def test_convert_raises_import_error(self):
        from memory_sidecar.flows.documents import _convert_document

        # Ensure ImportError propagates when docling is missing
        with patch.dict("sys.modules", {"docling": None, "docling.document_converter": None}):
            with pytest.raises(ImportError):
                _convert_document(Path("test.pdf"))


class TestIndexDocuments:
    """Tests for the index_documents flow with mocked Docling."""

    def test_nonexistent_path_raises(self, tmp_path):
        with pytest.raises(FileNotFoundError):
            index_documents(str(tmp_path / "nonexistent"), str(tmp_path / "test.sqlite"))

    @patch("memory_sidecar.flows.documents._convert_document")
    @patch("memory_sidecar.flows.documents.embed_texts")
    def test_skips_non_doc_files(self, mock_embed, mock_convert, tmp_path):
        """Non-document files in the docs folder should be ignored."""
        (tmp_path / "script.py").write_text("print('hello')")
        (tmp_path / "readme.md").write_text("# Hello")

        db = str(tmp_path / "test.sqlite")
        stats = index_documents(str(tmp_path), db)

        mock_convert.assert_not_called()
        assert stats["files_processed"] == 0

    @patch("memory_sidecar.flows.documents._convert_document")
    @patch("memory_sidecar.flows.documents.embed_texts")
    def test_index_documents_stats(self, mock_embed, mock_convert, tmp_path):
        """Verify stats dict from a successful indexing run."""
        (tmp_path / "doc1.pdf").write_bytes(b"fake pdf")
        (tmp_path / "doc2.docx").write_bytes(b"fake docx")

        mock_convert.side_effect = [
            "# Document 1\n\nSome content here for the first document.",
            "# Document 2\n\nContent for the second document.",
        ]
        mock_embed.return_value = [[0.1] * 384] * 2  # enough for 2 chunks (1 per doc)

        db = str(tmp_path / "test.sqlite")
        stats = index_documents(str(tmp_path), db)

        assert stats["files_processed"] == 2
        assert stats["files_skipped"] == 0
        assert stats["chunks_indexed"] == 2
        assert mock_convert.call_count == 2

    @patch("memory_sidecar.flows.documents._convert_document")
    @patch("memory_sidecar.flows.documents.embed_texts")
    def test_conversion_failure_counted_as_skipped(self, mock_embed, mock_convert, tmp_path):
        """Files that fail conversion should be counted in files_skipped."""
        (tmp_path / "corrupt.pdf").write_bytes(b"not a real pdf")

        mock_convert.side_effect = RuntimeError("corrupt file")

        db = str(tmp_path / "test.sqlite")
        stats = index_documents(str(tmp_path), db)

        assert stats["files_processed"] == 0
        assert stats["files_skipped"] == 1

    @patch("memory_sidecar.flows.documents._convert_document")
    @patch("memory_sidecar.flows.documents.embed_texts")
    def test_empty_conversion_counted_as_skipped(self, mock_embed, mock_convert, tmp_path):
        """Files that convert to empty/whitespace content should be skipped."""
        (tmp_path / "empty.pdf").write_bytes(b"fake")

        mock_convert.return_value = "   \n  "

        db = str(tmp_path / "test.sqlite")
        stats = index_documents(str(tmp_path), db)

        assert stats["files_processed"] == 0
        assert stats["files_skipped"] == 1
