"""Write golden cases for the C# clean-up: words in, the SRT SubGen's pipeline.py makes from them out.

    ..\\subgen\\.venv\\Scripts\\python tools\\bench\\make_golden.py

  tests/Hushcue.Core.Tests/Golden/synthetic-NN.json  random but repeatable word lists built to hit every branch
                                                      (markers, loops, stretched hums, hallucinations, odd spacing,
                                                      non-ASCII); committed, run on every test
  bench-out/golden/RUN-CLIP.json                      the real transcripts in bench-out/*/CLIP.words.json; local only
                                                      (they hold transcripts of your own media), run when present
"""
from __future__ import annotations

import json
import random
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT.parent / "subgen"))
import pipeline as p  # noqa: E402

SYNTHETIC = ROOT / "tests" / "Hushcue.Core.Tests" / "Golden"
REAL = ROOT / "bench-out" / "golden"

VOCAB = ["oh,", "yes", "maybe", "please", "mmm", "Mmmmmmmmmm", "Ahhhhh!", "10000", "(laughs)", "(door", "creaks)",
         "[Music]", "[", "applause]", "*sighs", "heavily*", "*", "♪", "la", "5*3", "...", "Okay.", "What?", "Stop!",
         "café", "naïve", "😀", "Oh", "my", "word.", "right", "there", "Yeah,", "I'm", "don't", "it's", "ing", "-",
         " hey", "so", "good.", "come", "here", "now", "Thank", "you", "for", "watching."]
LOOPS = [["mmm,"], ["oh,", "wow,"], ["yes", "right", "here"], ["Oh", "my", "word."], ["..."]]
JUNK = [["Thank", "you", "for", "watching."], ["Thanks", "for", "listening."], ["Please", "subscribe."]]


def synthetic_words(rng: random.Random) -> list[dict]:
    words, t = [], rng.uniform(0, 3)
    while len(words) < rng.randint(5, 160):
        roll = rng.random()
        if roll < 0.08:  # a loop
            phrase = rng.choice(LOOPS)
            group = phrase * rng.randint(2, 14)
        elif roll < 0.12:  # a hallucination on its own after a pause
            t += rng.uniform(1.0, 4.0)
            group = rng.choice(JUNK)
        else:
            group = [rng.choice(VOCAB)]
        for text in group:
            gap = rng.choice([0.0, 0.0, rng.uniform(0, 0.4), rng.uniform(0, 0.4), rng.uniform(0.6, 3.0)])
            if rng.random() < 0.03:
                gap = -t if rng.random() < 0.2 else 0.0  # same start as the previous word, or back to zero
            start = round(max(t + gap, 0.0), 2)
            end = round(start + rng.choice([0.0, rng.uniform(0.05, 0.9), rng.uniform(0.9, 7.0)]), 2)
            space = "" if text in ("ing", "-") or rng.random() < 0.05 else " "
            words.append({"word": space + text, "start": start, "end": end})
            t = max(t, end)
    return words


def expected_srt(words: list[dict]) -> str:
    ws = p.squash_stretched(p.strip_markers([p.Word(w["word"], w["start"], w["end"]) for w in words]))
    cues = p.fix_timing(p.drop_junk(p.merge_orphans(p.build_cues(p.collapse_repeats(ws)))))
    return p.to_srt(cues)


def write(path: Path, words: list[dict]) -> None:
    path.write_text(json.dumps({"words": words, "srt": expected_srt(words)}, indent=1), encoding="utf-8")


def main() -> int:
    SYNTHETIC.mkdir(parents=True, exist_ok=True)
    for n in range(60):
        write(SYNTHETIC / f"synthetic-{n:02}.json", synthetic_words(random.Random(n)))
    real = sorted((ROOT / "bench-out").glob("*/*.words.json"))
    if real:
        REAL.mkdir(parents=True, exist_ok=True)
        for f in real:
            write(REAL / f"{f.parent.name}-{f.name.removesuffix('.words.json')}.json",
                  json.loads(f.read_text(encoding="utf-8-sig")))
    print(f"60 synthetic cases, {len(real)} real cases")
    return 0


if __name__ == "__main__":
    sys.exit(main())
