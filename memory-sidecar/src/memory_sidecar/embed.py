"""FastEmbed wrapper for ONNX-based embedding generation."""

from __future__ import annotations

from memory_sidecar.config import DEFAULT_EMBED_MODEL

_model = None


def _get_model():
    global _model
    if _model is None:
        from fastembed import TextEmbedding

        _model = TextEmbedding(model_name=DEFAULT_EMBED_MODEL)
    return _model


def embed_texts(texts: list[str]) -> list[list[float]]:
    """Generate embeddings for a batch of texts using FastEmbed ONNX runtime."""
    model = _get_model()
    embeddings = list(model.embed(texts))
    return [e.tolist() for e in embeddings]


def embed_one(text: str) -> list[float]:
    """Generate embedding for a single text."""
    return embed_texts([text])[0]
