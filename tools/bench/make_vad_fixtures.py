"""Write test fixtures for the C# voice activity detection from faster-whisper itself (run with SubGen's venv).

    ..\\subgen\\.venv\\Scripts\\python tools\\bench\\make_vad_fixtures.py SPEECH1 SPEECH2 SPEECH3

  tests/Hushcue.Core.Tests/Golden/vad-input.s16  the three speech clips with silence of different lengths between
                                                 them, the last one repeated quietly, in low noise (fixed seed)
  tests/Hushcue.Core.Tests/Golden/vad.json       Silero speech probabilities, speech timestamps for several option
                                                 sets, timestamp restoring, and Python's round(x, 2)

The clips are 16 kHz mono 16-bit WAV files; the committed ones were spoken by macOS
`say -o a.wav --file-format=WAVE --data-format=LEI16@16000 "..."`.
"""
from __future__ import annotations

import json
import sys
import wave
from pathlib import Path

import numpy as np
from faster_whisper.vad import SpeechTimestampsMap, VadOptions, collect_chunks, get_speech_timestamps, get_vad_model

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "Hushcue.Core.Tests" / "Golden"
SAMPLE_RATE = 16000

# Seconds of noise before, between and after the clips: a lead-in, a gap shorter than the padding, a gap longer than
# the default minimum silence, a gap between the Fast preset's and the default's minimum silence, and a tail.
GAPS = [1.2, 0.25, 2.6, 0.9, 1.4]
QUIET_GAIN = 0.15

OPTION_SETS = {
    "default": VadOptions(),
    "fast_preset": VadOptions(threshold=0.1, min_silence_duration_ms=1000),
    "max_speech": VadOptions(max_speech_duration_s=2.0, min_silence_duration_ms=500),
    "strict": VadOptions(threshold=0.7, neg_threshold=0.5, min_speech_duration_ms=600, speech_pad_ms=100),
}


def read_wav(path: str) -> np.ndarray:
    with wave.open(path, "rb") as f:
        if (f.getframerate(), f.getnchannels(), f.getsampwidth()) != (SAMPLE_RATE, 1, 2):
            raise SystemExit(f"{path}: expected 16 kHz mono 16-bit WAV")
        return np.frombuffer(f.readframes(f.getnframes()), np.int16).astype(np.float32) / 32768.0


def compose(clips: list[np.ndarray]) -> np.ndarray:
    rng = np.random.default_rng(7)
    pieces = [*clips, clips[-1] * QUIET_GAIN]
    parts = [np.zeros(int(GAPS[0] * SAMPLE_RATE), np.float32)]
    for piece, gap in zip(pieces, GAPS[1:]):
        parts += [piece, np.zeros(int(gap * SAMPLE_RATE), np.float32)]
    audio = np.concatenate(parts)
    return audio + 0.003 * rng.standard_normal(audio.size).astype(np.float32)


def restore_cases(chunks: list[dict]) -> list[dict]:
    ts_map = SpeechTimestampsMap(chunks, SAMPLE_RATE)
    kept = sum(c["end"] - c["start"] for c in chunks) / SAMPLE_RATE
    times = [float(t) for t in np.linspace(0, kept, 29)] + [s / SAMPLE_RATE for s in ts_map.chunk_end_sample]
    return [{"time": t, "is_end": is_end, "chunk": ts_map.get_chunk_index(t, is_end),
             "original": ts_map.get_original_time(t, is_end=is_end)}
            for t in times for is_end in (False, True)]


def main() -> int:
    if len(sys.argv) != 4:
        print(__doc__)
        return 2
    pcm = np.clip(compose([read_wav(p) for p in sys.argv[1:]]) * 32767, -32768, 32767)
    pcm = pcm.astype(np.int16)
    OUT.mkdir(parents=True, exist_ok=True)
    pcm.tofile(OUT / "vad-input.s16")
    audio = pcm.astype(np.float32) / 32768.0

    padded = np.pad(audio, (0, 512 - audio.shape[0] % 512))
    probabilities = get_vad_model()(padded)
    timestamps = {name: get_speech_timestamps(audio, options) for name, options in OPTION_SETS.items()}
    default_chunks = timestamps["default"]
    rng = np.random.default_rng(3)
    halves = [k / 8 for k in range(1, 80, 2)]  # exact binary halves at two decimals: ties go to even
    fixture = {
        "options": {name: vars(options) | {"max_speech_duration_s": min(options.max_speech_duration_s, 1e9)}
                    for name, options in OPTION_SETS.items()},
        "probabilities": [float(p) for p in probabilities],
        "timestamps": timestamps,
        "collected_samples": int(collect_chunks(audio, default_chunks)[0][0].shape[0]),
        "restore": restore_cases(default_chunks),
        "round2": [[x, round(x, 2)] for x in halves + [float(x) for x in rng.uniform(0, 600, 200)]],
    }
    (OUT / "vad.json").write_text(json.dumps(fixture, indent=1), encoding="utf-8")
    print(f"wrote {audio.size / SAMPLE_RATE:.1f} s of VAD input; default chunks: {default_chunks}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
