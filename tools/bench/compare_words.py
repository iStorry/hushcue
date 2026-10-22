"""Compare two folders of NAME.words.json (fw_reference.py against Hushcue.Cli transcribe) word for word.

    python tools\\bench\\compare_words.py BASE_DIR OTHER_DIR

Per clip: whether every word and its start and end are identical, and if not, the share of the base's words the other
reproduces in order and the median / largest start difference over the words both have.
"""
from __future__ import annotations

import difflib
import json
import statistics
import sys
from pathlib import Path


def load(path: Path) -> list[dict]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main(argv: list[str]) -> int:
    base, other = Path(argv[1]), Path(argv[2])
    print("| clip | words | identical (text + times) | words same | start diff median / max (s) |")
    print("|---|---|---|---|---|")
    all_same = True
    for path in sorted(base.glob("*.words.json")):
        name = path.name.removesuffix(".words.json")
        a, b = load(path), load(other / path.name)
        exact = [(w["word"], w["start"], w["end"]) for w in a] == [(w["word"], w["start"], w["end"]) for w in b]
        all_same &= exact
        matcher = difflib.SequenceMatcher(None, [w["word"] for w in a], [w["word"] for w in b], autojunk=False)
        pairs = [(a[m.a + k], b[m.b + k]) for m in matcher.get_matching_blocks() for k in range(m.size)]
        diffs = [abs(x["start"] - y["start"]) for x, y in pairs] or [0.0]
        print(f"| {name} | {len(a)} / {len(b)} | {'yes' if exact else 'no'} | {len(pairs) / max(len(a), 1):.1%} | "
              f"{statistics.median(diffs):.2f} / {max(diffs):.2f} |")
    return 0 if all_same else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
