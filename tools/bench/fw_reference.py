"""faster-whisper's output for the reference clips with chosen options, to check the C# engine against.

    ..\\subgen\\.venv\\Scripts\\python tools\\bench\\fw_reference.py OUT_DIR [--language auto|CODE] [--prompt TEXT]
        [--repetition-penalty P] [--hallucination-silence SECONDS] [--seconds N]
        [--vad] [--vad-threshold T] [--vad-min-silence MS] [--vad-speech-pad MS] [--features DIR]

Writes OUT_DIR/CLIP.words.json and, so Hushcue.Cli can decode the very same input, the log-mel features faster-whisper
computed (fw_features.py; default folder bench-out/features). --seconds N uses only the first N seconds of each clip.
Run Hushcue.Cli transcribe with the same options and --features DIR, then compare the words (compare_words.py).
SubGen's VAD options are --vad-threshold 0.1 --vad-min-silence 1000 --vad-speech-pad 500.
Set SUBGEN_DIR if SubGen is not checked out next to this repository.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
SUBGEN = Path(os.environ.get("SUBGEN_DIR") or ROOT.parent / "subgen")
CLIPS = sorted((SUBGEN / "reference").glob("*.mp4"))
sys.path.insert(0, str(SUBGEN))
import pipeline  # noqa: E402  (for its CUDA DLL setup)

pipeline.setup_cuda_paths()
from faster_whisper import WhisperModel  # noqa: E402

from fw_features import record_features  # noqa: E402


def audio_of(path: Path, seconds: float | None) -> np.ndarray:
    """16 kHz mono through 16-bit, as SubGen's WAV + faster-whisper's decoder give it."""
    cmd = ["ffmpeg", "-v", "error", "-i", str(path), "-vn", "-ac", "1", "-ar", "16000"]
    if seconds:
        cmd += ["-t", str(seconds)]
    pcm = subprocess.run(cmd + ["-f", "s16le", "pipe:1"], capture_output=True, check=True).stdout
    return np.frombuffer(pcm, np.int16).astype(np.float32) / 32768.0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("out")
    parser.add_argument("--language", default="en")
    parser.add_argument("--prompt")
    parser.add_argument("--repetition-penalty", type=float, default=1.0)
    parser.add_argument("--hallucination-silence", type=float)
    parser.add_argument("--seconds", type=float)
    parser.add_argument("--vad", action="store_true")
    parser.add_argument("--vad-threshold", type=float)
    parser.add_argument("--vad-min-silence", type=int)
    parser.add_argument("--vad-speech-pad", type=int)
    parser.add_argument("--features", default=str(ROOT / "bench-out" / "features"))
    args = parser.parse_args()
    vad_parameters = {key: value for key, value in [("threshold", args.vad_threshold),
                                                    ("min_silence_duration_ms", args.vad_min_silence),
                                                    ("speech_pad_ms", args.vad_speech_pad)]
                      if value is not None}

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    model = WhisperModel("large-v3-turbo", device="cuda", compute_type="float16")
    record_features(model, Path(args.features))
    for clip in CLIPS:
        audio = audio_of(clip, args.seconds)
        segments, info = model.transcribe(
            audio, language=None if args.language == "auto" else args.language, beam_size=5, temperature=[0.0],
            condition_on_previous_text=False, vad_filter=args.vad or bool(vad_parameters),
            vad_parameters=vad_parameters or None, word_timestamps=True, no_speech_threshold=0.6,
            log_prob_threshold=-1.0, initial_prompt=args.prompt, repetition_penalty=args.repetition_penalty,
            hallucination_silence_threshold=args.hallucination_silence)
        words = [{"word": w.word, "start": w.start, "end": w.end, "probability": w.probability}
                 for s in segments for w in (s.words or [])]
        (out / f"{clip.stem}.words.json").write_text(json.dumps(words, indent=1), encoding="utf-8")
        print(f"{clip.stem}: {len(words)} words, language {info.language} ({info.language_probability:.4f}), "
              f"{info.duration_after_vad:.1f} of {info.duration:.1f} s decoded")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
