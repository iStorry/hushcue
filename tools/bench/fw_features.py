"""Record the log-mel features faster-whisper computes, so Hushcue can decode the very same input.

Every call of a model's feature extractor is saved as FOLDER/KEY.f32 (raw float32, n_mels rows), where KEY is the
first 16 hex digits of the SHA-256 of the float32 samples it was given. Hushcue.Cli's --features FOLDER looks
features up by the same key, so whole files, VAD-trimmed audio and loop-recovery clips all find theirs.
"""
from __future__ import annotations

import hashlib
from pathlib import Path

import numpy as np


def audio_key(audio: np.ndarray) -> str:
    return hashlib.sha256(np.ascontiguousarray(audio, dtype=np.float32).tobytes()).hexdigest()[:16]


class _Recording:
    def __init__(self, extractor, folder: Path):
        self._extractor, self._folder = extractor, folder

    def __getattr__(self, name):
        return getattr(self._extractor, name)

    def __call__(self, audio, *args, **kwargs):
        features = self._extractor(audio, *args, **kwargs)
        np.ascontiguousarray(features, dtype=np.float32).tofile(self._folder / f"{audio_key(audio)}.f32")
        return features


def record_features(model, folder: Path) -> None:
    """Save the features of every later model.transcribe call into folder (a faster_whisper.WhisperModel)."""
    folder.mkdir(parents=True, exist_ok=True)
    if not isinstance(model.feature_extractor, _Recording):
        model.feature_extractor = _Recording(model.feature_extractor, folder)
    else:
        model.feature_extractor._folder = folder
