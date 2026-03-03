"""CLI interface for the memory sidecar."""

from __future__ import annotations

import json
import sys

import click


@click.group()
@click.version_option()
def main():
    """Giant-isopod memory sidecar: embedding, indexing, and knowledge search."""


@main.command()
@click.argument("source_path")
@click.option("--db", default=None, help="SQLite database path")
@click.option("--chunk-size", default=1000, help="Max chunk size in characters")
@click.option("--chunk-overlap", default=300, help="Overlap between chunks")
@click.option("--batch-size", default=32, help="Embedding batch size")
def index(source_path: str, db: str | None, chunk_size: int, chunk_overlap: int, batch_size: int):
    """Index a codebase directory into SQLite with vector embeddings."""
    from memory_sidecar.config import codebase_db_path
    from memory_sidecar.flows.codebase import index_codebase

    db_path = db or str(codebase_db_path())
    click.echo(f"Indexing {source_path} -> {db_path}")
    stats = index_codebase(source_path, db_path, chunk_size, chunk_overlap, batch_size)
    click.echo(json.dumps(stats, indent=2))


@main.command()
@click.argument("query_text")
@click.option("--db", default=None, help="SQLite database path")
@click.option("--top-k", default=10, help="Number of results")
@click.option("--json-output", "json_out", is_flag=True, help="Output as JSON")
def search(query_text: str, db: str | None, top_k: int, json_out: bool):
    """Search the codebase index by semantic similarity."""
    from memory_sidecar.config import codebase_db_path
    from memory_sidecar.flows.codebase import search_codebase

    db_path = db or str(codebase_db_path())
    results = search_codebase(db_path, query_text, top_k)
    if json_out:
        click.echo(json.dumps(results, indent=2))
    else:
        if not results:
            click.echo("No results found.")
            return
        for r in results:
            click.echo(f"[{r['score']:.3f}] {r['filename']}:{r['location']}")
            for line in r["code"].strip().splitlines()[:3]:
                click.echo(f"    {line}")
            click.echo("---")


@main.command("index-docs")
@click.argument("docs_path")
@click.option("--db", default=None, help="SQLite database path")
@click.option("--chunk-size", default=1000, help="Max chunk size in characters")
@click.option("--chunk-overlap", default=300, help="Overlap between chunks")
@click.option("--batch-size", default=32, help="Embedding batch size")
def index_docs(docs_path: str, db: str | None, chunk_size: int, chunk_overlap: int, batch_size: int):
    """Index a documents directory (PDF, DOCX, PPTX, etc.) via Docling conversion."""
    try:
        import docling  # noqa: F401
    except ImportError:
        click.echo("Docling not installed. Run: uv pip install -e '.[docs]'", err=True)
        raise SystemExit(1) from None

    from memory_sidecar.config import codebase_db_path
    from memory_sidecar.flows.documents import index_documents

    db_path = db or str(codebase_db_path())
    click.echo(f"Indexing documents {docs_path} -> {db_path}")
    stats = index_documents(docs_path, db_path, chunk_size, chunk_overlap, batch_size)
    click.echo(json.dumps(stats, indent=2))


@main.command()
@click.argument("text")
def embed(text: str):
    """Generate an embedding vector for a text string. Outputs JSON array."""
    from memory_sidecar.embed import embed_one

    json.dump(embed_one(text), sys.stdout)
    sys.stdout.write("\n")


@main.command()
@click.argument("content")
@click.option("--agent", required=True, help="Agent ID")
@click.option(
    "--category", required=True, type=click.Choice(["pattern", "pitfall", "codebase", "preference", "outcome"])
)
@click.option("--tag", multiple=True, help="Tags as key:value pairs")
@click.option("--db", default=None, help="SQLite database path")
def store(content: str, agent: str, category: str, tag: tuple[str, ...], db: str | None):
    """Store a knowledge entry for an agent."""
    from memory_sidecar.config import knowledge_db_path
    from memory_sidecar.flows.knowledge import store as store_knowledge

    db_path = db or str(knowledge_db_path(agent))
    tags = {}
    for t in tag:
        if ":" in t:
            k, v = t.split(":", 1)
            tags[k] = v
    row_id = store_knowledge(db_path, content, category, tags or None)
    click.echo(json.dumps({"id": row_id, "agent": agent, "category": category}))


@main.command()
@click.argument("query_text")
@click.option("--agent", required=True, help="Agent ID")
@click.option("--category", default=None, help="Filter by category")
@click.option("--top-k", default=10, help="Number of results")
@click.option("--db", default=None, help="SQLite database path")
@click.option("--json-output", "json_out", is_flag=True, help="Output as JSON")
@click.option("--hybrid/--no-hybrid", default=True, help="Use hybrid (vector + FTS5) search (default: hybrid)")
def query(query_text: str, agent: str, category: str | None, top_k: int, db: str | None, json_out: bool, hybrid: bool):
    """Search an agent's knowledge base by semantic similarity."""
    from memory_sidecar.config import knowledge_db_path
    from memory_sidecar.flows.knowledge import query as query_knowledge

    db_path = db or str(knowledge_db_path(agent))
    results = query_knowledge(db_path, query_text, category, top_k, hybrid=hybrid)
    if json_out:
        click.echo(json.dumps(results, indent=2))
    else:
        if not results:
            click.echo("No results found.")
            return
        for r in results:
            click.echo(f"[{r['relevance']:.3f}] {r['category']}")
            click.echo(f"    {r['content'][:120]}")
            if r.get("tags"):
                click.echo(f"    tags: {r['tags']}")
            click.echo("---")


@main.command("episodic-put")
@click.argument("content")
@click.option("--file", "file_path", required=True, help="Path to the .mv2 file")
@click.option("--title", default=None, help="Optional title for the memory frame")
@click.option("--tag", multiple=True, help="Tags as key:value or key=value pairs")
def episodic_put(content: str, file_path: str, title: str | None, tag: tuple[str, ...]):
    """Store an episodic memory entry through the sidecar."""
    from memory_sidecar.flows.episodic import put as put_episodic

    tags = {}
    for t in tag:
        if ":" in t:
            k, v = t.split(":", 1)
            tags[k] = v
        elif "=" in t:
            k, v = t.split("=", 1)
            tags[k] = v
    result = put_episodic(file_path, content, title=title, tags=tags or None)
    click.echo(json.dumps(result))


@main.command("episodic-search")
@click.argument("query_text")
@click.option("--file", "file_path", required=True, help="Path to the .mv2 file")
@click.option("--top-k", default=10, help="Number of results")
def episodic_search(query_text: str, file_path: str, top_k: int):
    """Search episodic memory through the sidecar."""
    from memory_sidecar.flows.episodic import search as search_episodic

    result = search_episodic(file_path, query_text, top_k=top_k)
    click.echo(json.dumps(result, indent=2))


@main.command("episodic-commit")
@click.option("--file", "file_path", required=True, help="Path to the .mv2 file")
def episodic_commit(file_path: str):
    """Finalize episodic memory writes (currently a no-op)."""
    from memory_sidecar.flows.episodic import commit as commit_episodic

    result = commit_episodic(file_path)
    click.echo(json.dumps(result))


if __name__ == "__main__":
    main()
