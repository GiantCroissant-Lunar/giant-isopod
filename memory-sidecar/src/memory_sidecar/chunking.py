"""Shared text chunking utilities."""

from __future__ import annotations


def split_simple(content: str, chunk_size: int, chunk_overlap: int) -> list[dict]:
    """Simple sliding-window text splitter with newline-aware boundaries.

    Returns list of dicts with 'text' and 'location' (format: '{chunk_idx}:{byte_offset}').
    """
    if len(content) <= chunk_size:
        return [{"text": content, "location": "0:0"}]
    chunks = []
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
        start = end - chunk_overlap if end < len(content) else end
    return chunks
