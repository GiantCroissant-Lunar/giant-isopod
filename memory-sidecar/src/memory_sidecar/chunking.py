"""Code chunking strategies for codebase indexing."""

from __future__ import annotations

# Bump this when the chunking algorithm changes to force re-indexing.
CHUNKER_VERSION = "ts1"


def split_simple(content: str, chunk_size: int, chunk_overlap: int) -> list[dict]:
    """Simple recursive text splitter — fallback for languages without tree-sitter support."""
    if len(content) <= chunk_size:
        return [{"text": content, "location": "0:0"}]
    chunks: list[dict] = []
    start = 0
    idx = 0
    while start < len(content):
        end = min(start + chunk_size, len(content))
        if end < len(content):
            nl = content.rfind("\n", start, end)
            if nl > start:
                end = nl + 1
        text = content[start:end]
        if text.strip():
            chunks.append({"text": text, "location": f"{idx}:{start}"})
            idx += 1
        advance = end - chunk_overlap if end < len(content) else end
        start = max(advance, start + 1)  # ensure forward progress
    return chunks
