"""Write test fixtures for the C# engine from faster-whisper itself (run with SubGen's venv).

    ..\\subgen\\.venv\\Scripts\\python tools\\bench\\make_engine_fixtures.py

  tests/Hushcue.Core.Tests/Golden/mel-input.s16   3 s of synthetic 16 kHz audio (a chirp in noise, fixed seed)
  tests/Hushcue.Core.Tests/Golden/mel-128.f32     faster-whisper's 128-band log-mel features of it
  tests/Hushcue.Core.Tests/Golden/tokenizer.json  faster-whisper Tokenizer results: special ids, encodings, decodes,
                                                  non-speech tokens, word splits (checked against large-v3-turbo's
                                                  tokenizer.json, which the test finds in the Hugging Face cache)
"""
from __future__ import annotations

import json
from pathlib import Path

import numpy as np
from faster_whisper.feature_extractor import FeatureExtractor
from faster_whisper.tokenizer import Tokenizer
import tokenizers

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "Hushcue.Core.Tests" / "Golden"

TEXTS = [" Anna, Brian, Marco", "Hello there, friend", " I'm, don't - it's 10,000!", " café naïve 日本 😀",
         "  double  spaces\tand tabs", " (laughs) [Music] ♪ la ♪", " <<hi>> -- ((x))"]


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)

    rng = np.random.default_rng(1)
    t = np.arange(3 * 16000) / 16000
    signal = 0.3 * np.sin(2 * np.pi * (200 + 900 * t) * t) + 0.05 * rng.standard_normal(t.size)
    pcm = np.clip(signal * 32767, -32768, 32767).astype(np.int16)
    pcm.tofile(OUT / "mel-input.s16")
    audio = pcm.astype(np.float32) / 32768.0
    FeatureExtractor(feature_size=128)(audio).astype(np.float32).tofile(OUT / "mel-128.f32")

    snapshots = Path.home() / ".cache/huggingface/hub/models--mobiuslabsgmbh--faster-whisper-large-v3-turbo/snapshots"
    model_dir = next(snapshots.iterdir())
    hf = tokenizers.Tokenizer.from_file(str(model_dir / "tokenizer.json"))
    tok = Tokenizer(hf, multilingual=True, task="transcribe", language="en")
    encodings = {text: tok.encode(text) for text in TEXTS}
    split_input = tok.encode(" Hello, world! It's 日本 time.") + [tok.eot]
    words, word_tokens = tok.split_to_word_tokens(split_input)
    fixture = {
        "special": {"sot": tok.sot, "eot": tok.eot, "sot_prev": tok.sot_prev, "no_timestamps": tok.no_timestamps,
                    "no_speech": tok.no_speech, "timestamp_begin": tok.timestamp_begin,
                    "sot_sequence": tok.sot_sequence},
        "encodings": encodings,
        "decodes": {text: tok.decode(ids) for text, ids in encodings.items()},
        "partial_decode": tok.decode(tok.encode(" 日本")[:1]),
        "non_speech_tokens": list(tok.non_speech_tokens),
        "split": {"tokens": split_input, "words": words, "word_tokens": word_tokens},
    }
    (OUT / "tokenizer.json").write_text(json.dumps(fixture, ensure_ascii=True, indent=1), encoding="utf-8")
    print("wrote mel and tokenizer fixtures")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
