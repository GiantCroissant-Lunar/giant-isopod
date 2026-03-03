"""Episodic memory helpers backed by Memvid, exposed through the sidecar."""

from __future__ import annotations

import json
import shutil
import subprocess
import time
from datetime import UTC, datetime
from pathlib import Path

TRANSIENT_ERROR_MARKERS = (
    "AccessDenied",
    "LockFailure",
    "FileDoesNotExist",
    "PermissionDenied",
    "meta.json",
    "存取被拒",
)


def put(file_path: str, content: str, title: str | None = None, tags: dict[str, str] | None = None) -> dict:
    """Store episodic memory content in a Memvid-backed .mv2 file."""
    path = Path(file_path)
    path.parent.mkdir(parents=True, exist_ok=True)

    _ensure_created(path)

    args = ["put", str(path)]
    if title:
        args.extend(["--title", title])
    if tags:
        for key, value in tags.items():
            args.extend(["--tag", f"{key}={value}"])

    _run_with_retry(args, input_text=content, ensure_file=path)
    _append_index_entry(path, content, title=title, tags=tags)
    return {"ok": True, "file": str(path)}


def search(file_path: str, query_text: str, top_k: int = 10) -> dict:
    """Search episodic memory, returning Memvid-compatible JSON."""
    path = Path(file_path)
    if not path.exists():
        return {"hits": []}

    memvid_result: dict | None = None
    try:
        result = _run_with_retry(
            ["find", "--query", query_text, str(path), "--json", "--top-k", str(top_k)],
            ensure_file=path,
        )
        stdout = result.stdout or ""
        if stdout.strip():
            memvid_result = json.loads(stdout)
    except (json.JSONDecodeError, OSError, RuntimeError, ValueError):
        memvid_result = None

    if memvid_result is not None:
        return memvid_result

    return {"hits": _search_index(path, query_text, top_k)}


def commit(file_path: str) -> dict:
    """No-op commit hook for episodic memory."""
    return {"ok": True, "file": file_path}


def _ensure_created(path: Path) -> None:
    if path.exists():
        return

    result = _run_with_retry(["create", str(path)], ensure_file=path)
    if result.returncode == 0 or path.exists():
        return

    raise RuntimeError(f"Failed to create episodic memory file: {path}")


def _run_with_retry(
    args: list[str],
    input_text: str | None = None,
    ensure_file: Path | None = None,
    attempts: int = 3,
) -> subprocess.CompletedProcess[str]:
    last_result: subprocess.CompletedProcess[str] | None = None
    for attempt in range(1, attempts + 1):
        result = _run_memvid(args, input_text)
        last_result = result

        if result.returncode == 0:
            return result

        if ensure_file is not None and args[0] == "create" and ensure_file.exists():
            return result

        if attempt < attempts and _looks_transient(result.stderr):
            time.sleep(0.35 * attempt)
            continue

        break

    assert last_result is not None
    stderr = last_result.stderr.strip()
    stdout = last_result.stdout.strip()
    detail = stderr or stdout or f"memvid exited with code {last_result.returncode}"
    raise RuntimeError(detail)


def _run_memvid(args: list[str], input_text: str | None = None) -> subprocess.CompletedProcess[str]:
    executable = _resolve_memvid_executable()
    return subprocess.run(  # noqa: S603
        [executable, *args],
        input=input_text,
        text=True,
        encoding="utf-8",
        errors="replace",
        capture_output=True,
        check=False,
    )


def _resolve_memvid_executable() -> str:
    for candidate in ("memvid", "memvid.cmd", "memvid.exe", "memvid.bat"):
        resolved = shutil.which(candidate)
        if resolved:
            return resolved

    raise FileNotFoundError("Could not locate memvid on PATH.")


def _looks_transient(stderr: str) -> bool:
    return any(marker in stderr for marker in TRANSIENT_ERROR_MARKERS)


def _index_path(path: Path) -> Path:
    return path.with_suffix(path.suffix + ".episodes.jsonl")


def _append_index_entry(path: Path, content: str, title: str | None, tags: dict[str, str] | None) -> None:
    entry = {
        "text": content,
        "title": title,
        "tags": tags or {},
        "timestamp": datetime.now(UTC).isoformat(),
    }

    index_path = _index_path(path)
    index_path.parent.mkdir(parents=True, exist_ok=True)
    with index_path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(entry, ensure_ascii=False))
        handle.write("\n")


def _search_index(path: Path, query_text: str, top_k: int) -> list[dict]:
    index_path = _index_path(path)
    if not index_path.exists():
        return []

    query_tokens = _tokenize(query_text)
    hits: list[dict] = []
    with index_path.open("r", encoding="utf-8") as handle:
        for line in handle:
            if not line.strip():
                continue

            entry = json.loads(line)
            text = str(entry.get("text", ""))
            title = entry.get("title")
            score = _score_entry(text, title, query_tokens)
            if score <= 0:
                continue

            hits.append(
                {
                    "text": text,
                    "title": title,
                    "score": score,
                    "timestamp": entry.get("timestamp"),
                }
            )

    hits.sort(key=lambda item: item["score"], reverse=True)
    return hits[:top_k]


def _score_entry(text: str, title: str | None, query_tokens: set[str]) -> float:
    if not query_tokens:
        return 0.0

    haystack = _tokenize(f"{title or ''} {text}")
    if not haystack:
        return 0.0

    overlap = len(query_tokens & haystack)
    if overlap == 0:
        return 0.0

    return overlap / len(query_tokens)


def _tokenize(value: str) -> set[str]:
    return {token for token in value.lower().split() if token}
