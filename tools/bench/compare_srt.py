"""Compare two folders of finished subtitles (NAME.srt), e.g. SubGen's job against Hushcue's.

    python tools\\bench\\compare_srt.py BASE_DIR OTHER_DIR

Per clip and pooled: whether the files are identical (line endings aside), cues on each side, the share of the base's
words the other reproduces in order, the word error rate between them, and each side's word error rate against the
hand-made reference (SUBGEN_DIR/reference/NAME.srt). Words are normalized as SubGen's evaluate.py does (lower case,
no punctuation). Set SUBGEN_DIR if SubGen is not checked out next to this repository.
"""
from __future__ import annotations

import difflib
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SUBGEN = Path(os.environ.get("SUBGEN_DIR") or ROOT.parent / "subgen")
sys.path.insert(0, str(SUBGEN))
from evaluate import score, srt_words  # noqa: E402


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")


def cue_count(text: str) -> int:
    return sum(1 for block in text.strip().split("\n\n") if block.strip())


def errors(ref: list[str], hyp: list[str]) -> int:
    s = score(ref, hyp)
    return s.substitutions + s.deletions + s.insertions


def main(argv: list[str]) -> int:
    base, other = Path(argv[1]), Path(argv[2])
    reference = SUBGEN / "reference"
    print(f"| clip | identical | cues {base.name} / {other.name} | words same | WER between | WER ref {base.name} / {other.name} |")
    print("|---|---|---|---|---|---|")
    totals = {"words": 0, "same": 0, "between": 0, "ref": 0, "ref_base": 0, "ref_other": 0}
    names = sorted(p.stem for p in base.glob("*.srt"))
    identical = 0
    for name in names:
        a, b = read(base / f"{name}.srt"), read(other / f"{name}.srt")
        identical += a == b
        wa, wb = srt_words(a), srt_words(b)
        same = sum(m.size for m in difflib.SequenceMatcher(None, wa, wb, autojunk=False).get_matching_blocks())
        between = errors(wa, wb)
        totals["words"] += len(wa)
        totals["same"] += same
        totals["between"] += between
        ref_text = ""
        if (reference / f"{name}.srt").exists():
            ref = srt_words(read(reference / f"{name}.srt"))
            ea, eb = errors(ref, wa), errors(ref, wb)
            totals["ref"] += len(ref)
            totals["ref_base"] += ea
            totals["ref_other"] += eb
            ref_text = f"{ea / max(len(ref), 1):.1%} / {eb / max(len(ref), 1):.1%}"
        print(f"| {name} | {'yes' if a == b else 'no'} | {cue_count(a)} / {cue_count(b)} | "
              f"{same / max(len(wa), 1):.1%} ({same} / {len(wa)}) | {between / max(len(wa), 1):.1%} | {ref_text} |")
    ref_total = (f"{totals['ref_base'] / totals['ref']:.1%} / {totals['ref_other'] / totals['ref']:.1%}"
                 if totals["ref"] else "")
    print(f"| all | {identical} / {len(names)} | | {totals['same'] / max(totals['words'], 1):.1%} "
          f"({totals['same']} / {totals['words']}) | {totals['between'] / max(totals['words'], 1):.1%} | {ref_total} |")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
