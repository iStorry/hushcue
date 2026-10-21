"""faster-whisper's output for the reference clips with chosen options, to check the C# engine against.

    ..\\subgen\\.venv\\Scripts\\python tools\\bench\\fw_reference.py OUT_DIR [--language auto|CODE] [--prompt TEXT]
        [--repetition-penalty P] [--hallucination-silence SECONDS] [--seconds N]

Writes OUT_DIR/CLIP.words.json and, so Hushcue.Cli can decode the very same input, bench-out/features-N/CLIP.f32
(faster-whisper's own log-mel features). --seconds N uses only the first N seconds of each clip.
Run Hushcue.Cli transcribe with the same options and --features bench-out/features-N, then compare the words.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
CLIPS = sorted((ROOT.parent / "subgen" / "reference").glob("*.mp4"))
sys.path.insert(0, str(ROOT.parent / "subgen"))
import pipeline  # noqa: E402  (for its CUDA DLL setup)

pipeline.setup_cuda_paths()
from faster_whisper import WhisperModel  # noqa: E402
from faster_whisper.feature_extractor import FeatureExtractor  # noqa: E402


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
    args = parser.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    features_dir = ROOT / "bench-out" / f"features-{int(args.seconds or 0)}"
    features_dir.mkdir(parents=True, exist_ok=True)
    model = WhisperModel("large-v3-turbo", device="cuda", compute_type="float16")
    for clip in CLIPS:
        audio = audio_of(clip, args.seconds)
        FeatureExtractor(feature_size=128)(audio).astype(np.float32).tofile(features_dir / f"{clip.stem}.f32")
        segments, info = model.transcribe(
            audio, language=None if args.language == "auto" else args.language, beam_size=5, temperature=[0.0],
            condition_on_previous_text=False, vad_filter=False, word_timestamps=True, no_speech_threshold=0.6,
            log_prob_threshold=-1.0, initial_prompt=args.prompt, repetition_penalty=args.repetition_penalty,
            hallucination_silence_threshold=args.hallucination_silence)
        words = [{"word": w.word, "start": w.start, "end": w.end, "probability": w.probability}
                 for s in segments for w in (s.words or [])]
        (out / f"{clip.stem}.words.json").write_text(json.dumps(words, indent=1), encoding="utf-8")
        print(f"{clip.stem}: {len(words)} words, language {info.language} ({info.language_probability:.4f})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
