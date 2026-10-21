"""Compare every benchmark run in bench-out/ against the reference SRTs and against SubGen.

    python tools\\bench\\compare.py [BASELINE_DIR]

Prints a Markdown table per run (baseline: bench-out/subgen-a):
  WER ref     word error rate against ../subgen/reference/NAME.srt, all clips pooled, after running the run's
              words through SubGen's clean-up (markers, loops, junk) so every engine is cleaned the same way
  WER ref, raw  the same on the unprocessed segment text
  WER vs SG   cleaned words against SubGen's cleaned words (how far the engines disagree)
  timing      for words both engines produced, |start difference| median / 90th percentile in seconds
  speed       audio seconds per second of transcription, all clips pooled
  RAM         working set after the model loaded / peak while transcribing (MB)
"""
from __future__ import annotations

import difflib
import json
import re
import statistics
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SUBGEN = ROOT.parent / "subgen"
sys.path.insert(0, str(SUBGEN))
import pipeline  # noqa: E402
from evaluate import score, srt_words  # noqa: E402

OUT = ROOT / "bench-out"
REFERENCE = SUBGEN / "reference"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def wer(pairs: list[tuple[list[str], list[str]]]) -> float:
    errors = words = 0
    for ref, hyp in pairs:
        s = score(ref, hyp)
        errors += s.substitutions + s.deletions + s.insertions
        words += s.ref_words
    return errors / max(words, 1)


def cleaned(words: list[dict]) -> list[str]:
    """Words of the SRT SubGen's clean-up makes from these words (no loop re-decoding), so engines compare like-for-like."""
    ws = [pipeline.Word(w["word"], w["start"], w["end"]) for w in words]
    ws = pipeline.collapse_repeats(pipeline.squash_stretched(pipeline.strip_markers(ws)))
    cues = pipeline.fix_timing(pipeline.drop_junk(pipeline.merge_orphans(pipeline.build_cues(ws))))
    return srt_words(pipeline.to_srt(cues))


def norm(word: str) -> str:
    return re.sub(r"[^a-z0-9']+", "", word.lower())


def timing(base: list[dict], other: list[dict]) -> list[float]:
    """|start difference| for words matched in order between two word lists."""
    a = [w for w in base if norm(w["word"])]
    b = [w for w in other if norm(w["word"])]
    matcher = difflib.SequenceMatcher(None, [norm(w["word"]) for w in a], [norm(w["word"]) for w in b], autojunk=False)
    return [abs(a[blk.a + k]["start"] - b[blk.b + k]["start"]) for blk in matcher.get_matching_blocks() for k in range(blk.size)]


def main(argv: list[str]) -> int:
    baseline = OUT / (argv[1] if len(argv) > 1 else "subgen-a")
    clips = sorted(p.stem for p in REFERENCE.glob("*.srt"))
    runs = sorted((p for p in OUT.iterdir() if (p / "metrics.json").exists()), key=lambda p: (p != baseline, p.name))
    print(f"Clips: {', '.join(clips)} ({sum(len(srt_words(read(REFERENCE / f'{c}.srt'))) for c in clips)} reference words)\n")
    print("| run | WER ref | WER ref, raw | WER vs SG | timing med / p90 (s) | speed (x RT) | load (s) "
          "| RAM loaded / peak (MB) | VRAM (MB) |")
    print("|---|---|---|---|---|---|---|---|---|")
    ref = {c: srt_words(read(REFERENCE / f"{c}.srt")) for c in clips}
    base = {c: cleaned(json.loads(read(baseline / f"{c}.words.json"))) for c in clips}
    for run in runs:
        m = json.loads(read(run / "metrics.json"))
        hyp = {c: cleaned(json.loads(read(run / f"{c}.words.json"))) for c in clips}
        ref_wer = wer([(ref[c], hyp[c]) for c in clips])
        raw_wer = wer([(ref[c], srt_words(read(run / f"{c}.raw.srt"))) for c in clips])
        sg_wer = wer([(base[c], hyp[c]) for c in clips])
        diffs = [d for c in clips for d in timing(json.loads(read(baseline / f"{c}.words.json")),
                                                  json.loads(read(run / f"{c}.words.json")))]
        tm = f"{statistics.median(diffs):.2f} / {statistics.quantiles(diffs, n=10)[-1]:.2f}" if diffs else "-"
        audio = sum(m["clips"][c]["audio_seconds"] for c in clips)
        busy = sum(m["clips"][c]["raw_seconds"] for c in clips)
        peak = max(m["clips"][c]["raw_peak_ws_mb"] for c in clips)
        vram = m.get("vram_mb", "-")
        print(f"| {run.name} | {ref_wer:.1%} | {raw_wer:.1%} | {sg_wer:.1%} | {tm} | {audio / busy:.0f} | {m['load_seconds']:.1f} | "
              f"{m['after_load_ws_mb']:.0f} / {peak:.0f} | {vram} |")

    print()
    for run in runs:
        twin = OUT / f"{run.name}-repeat"
        if twin.exists():
            same = all(read(run / f"{c}.raw.srt") == read(twin / f"{c}.raw.srt") for c in clips)
            print(f"Repeatability {run.name} vs {twin.name}: {'identical' if same else 'DIFFERENT'}")
    full = [(srt_words(read(REFERENCE / f"{c}.srt")), srt_words(read(baseline / f"{c}.full.srt"))) for c in clips
            if (baseline / f"{c}.full.srt").exists()]
    if full:
        print(f"SubGen finished output (with clean-up) WER ref: {wer(full):.1%}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
