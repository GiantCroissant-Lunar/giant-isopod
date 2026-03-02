"""Code chunking strategies for codebase indexing."""

from __future__ import annotations

import logging

# Bump this when the chunking algorithm changes to force re-indexing.
CHUNKER_VERSION = "ts1"

logger = logging.getLogger(__name__)

# Tree-sitter node types that represent top-level definitions per language.
# When walking the AST we collect these as "semantic units" for chunking.
_DEFINITION_TYPES: dict[str, set[str]] = {
    "python": {
        "function_definition",
        "class_definition",
        "decorated_definition",
        "import_statement",
        "import_from_statement",
    },
    "c_sharp": {
        "class_declaration",
        "struct_declaration",
        "interface_declaration",
        "enum_declaration",
        "method_declaration",
        "namespace_declaration",
        "using_directive",
    },
    "rust": {
        "function_item",
        "struct_item",
        "enum_item",
        "impl_item",
        "trait_item",
        "mod_item",
        "use_declaration",
    },
    "typescript": {
        "function_declaration",
        "class_declaration",
        "interface_declaration",
        "type_alias_declaration",
        "export_statement",
        "import_statement",
        "lexical_declaration",
    },
    "tsx": {
        "function_declaration",
        "class_declaration",
        "interface_declaration",
        "type_alias_declaration",
        "export_statement",
        "import_statement",
        "lexical_declaration",
    },
    "javascript": {
        "function_declaration",
        "class_declaration",
        "export_statement",
        "import_statement",
        "lexical_declaration",
    },
    "gdscript": {
        "function_definition",
        "class_definition",
        "variable_statement",
        "signal_statement",
    },
}

# Cache: languages we've already tried to load.
# Maps language name -> True (available) or False (unavailable).
_LANGUAGE_CACHE: dict[str, bool] = {}


def _get_parser(language: str):
    """Try to get a tree-sitter parser. Returns None if unavailable."""
    if language in _LANGUAGE_CACHE and not _LANGUAGE_CACHE[language]:
        return None
    try:
        from tree_sitter_language_pack import get_parser

        parser = get_parser(language)
        _LANGUAGE_CACHE[language] = True
        return parser
    except Exception:
        _LANGUAGE_CACHE[language] = False
        return None


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


def _collect_top_level_nodes(root_node, language: str) -> list:
    """Collect top-level AST nodes that are semantic definitions."""
    def_types = _DEFINITION_TYPES.get(language, set())
    if not def_types:
        # No definition types configured — treat all top-level children as units
        return list(root_node.children)
    nodes = []
    for child in root_node.children:
        if child.type in def_types:
            nodes.append(child)
        elif child.type == "comment" or child.type == "expression_statement":
            # Include module-level comments and expressions (e.g., docstrings)
            nodes.append(child)
    return nodes


def split_ast(content: str, language: str, chunk_size: int, chunk_overlap: int) -> list[dict] | None:
    """AST-aware chunking using tree-sitter.

    Returns a list of chunk dicts, or None if tree-sitter is unavailable for this language.
    Each chunk dict has 'text' and 'location' (format: "chunk_idx:start_byte").
    Chunks group consecutive AST nodes together up to chunk_size, keeping definitions intact.
    If a single definition exceeds chunk_size, it gets its own chunk (not split mid-AST-node).
    """
    parser = _get_parser(language)
    if parser is None:
        return None

    source = content.encode("utf-8")
    tree = parser.parse(source)
    nodes = _collect_top_level_nodes(tree.root_node, language)

    if not nodes:
        return split_simple(content, chunk_size, chunk_overlap)

    chunks: list[dict] = []
    current_texts: list[str] = []
    current_size = 0
    current_start_byte = nodes[0].start_byte if nodes else 0
    idx = 0

    for node in nodes:
        node_text = source[node.start_byte : node.end_byte].decode("utf-8", errors="replace")
        node_len = len(node_text)

        if current_size > 0 and current_size + node_len > chunk_size:
            # Flush current group
            text = "\n".join(current_texts)
            if text.strip():
                chunks.append({"text": text, "location": f"{idx}:{current_start_byte}"})
                idx += 1
            current_texts = []
            current_size = 0
            current_start_byte = node.start_byte

        current_texts.append(node_text)
        current_size += node_len

    # Flush remaining
    if current_texts:
        text = "\n".join(current_texts)
        if text.strip():
            chunks.append({"text": text, "location": f"{idx}:{current_start_byte}"})

    return chunks if chunks else split_simple(content, chunk_size, chunk_overlap)


def chunk_file(content: str, language: str | None, chunk_size: int, chunk_overlap: int) -> list[dict]:
    """Chunk a file using AST-aware splitting when possible, falling back to simple splitting.

    Args:
        content: File content as string.
        language: Tree-sitter language name (e.g., "python"), or None for plain text.
        chunk_size: Target maximum chunk size in characters.
        chunk_overlap: Overlap between consecutive chunks (used by simple splitter only).

    Returns:
        List of chunk dicts with 'text' and 'location' keys.
    """
    if language:
        result = split_ast(content, language, chunk_size, chunk_overlap)
        if result is not None:
            return result
    return split_simple(content, chunk_size, chunk_overlap)
