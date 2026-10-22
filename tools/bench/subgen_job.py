"""SubGen's finished subtitles for the reference clips, from its real transcribe_file (loop recovery included).

    ..\\subgen\\.venv\\Scripts\\python tools\\bench\\subgen_job.py OUT_DIR [--preset NAME] [--skip-silence]
        [--no-recover-loops] [--features DIR] [CLIP ...]

Writes OUT_DIR/CLIP.srt exactly as SubGen's run_job writes it, and records every set of log-mel features
faster-whisper computed on the way (the whole file and each re-decoded loop; fw_features.py, default folder
bench-out/features-job) so `Hushcue.Cli job --features DIR` can run Hushcue's job on the very same input.
Compare the two folders with compare_srt.py. Set SUBGEN_DIR if SubGen is not checked out next to this repository.
"""
from __future__ import annotations

import argparse
import os
import sys
import tempfile
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SUBGEN = Path(os.environ.get("SUBGEN_DIR") or ROOT.parent / "subgen")
sys.path.insert(0, str(SUBGEN))
import pipeline  # noqa: E402

from fw_features import record_features  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("out")
    parser.add_argument("clips", nargs="*")
    parser.add_argument("--preset", default="Balanced")
    parser.add_argument("--skip-silence", action="store_true")
    parser.add_argument("--no-recover-loops", action="store_true")
    parser.add_argument("--features", default=str(ROOT / "bench-out" / "features-job"))
    args = parser.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    clips = [Path(p) for p in args.clips] or sorted((SUBGEN / "reference").glob("*.mp4"))
    preset = pipeline.PRESETS[args.preset]
    settings = pipeline.Settings(preset=args.preset, skip_silence=args.skip_silence,
                                 recover_loops=not args.no_recover_loops)
    cancel = threading.Event()
    model = pipeline.load_model(preset.model, "cuda", print)
    record_features(model, Path(args.features))
    with tempfile.TemporaryDirectory() as tmp:
        for clip in clips:
            wav = Path(tmp) / f"{clip.stem}.wav"
            pipeline.extract_audio(clip, wav, settings.enhance or preset.enhance, cancel)
            started = time.perf_counter()
            cues = pipeline.transcribe_file(model, wav, settings, preset, cancel, lambda f: None,
                                            lambda m, name=clip.stem: print(f"{name}: {m}"))
            (out / f"{clip.stem}.srt").write_text(pipeline.to_srt(cues), encoding="utf-8")
            print(f"{clip.stem}: {len(cues)} cues in {time.perf_counter() - started:.1f} s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
