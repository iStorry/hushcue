"""Run the Hushcue.Cli transcribe spike (and SubGen, for comparison) over the reference clips, recording VRAM.

    python tools\\bench\\run_hushcue.py [CONFIG ...]

Each configuration writes into bench-out/<name>/ (SRTs, words, metrics.json with vram_mb added).
VRAM is the peak of nvidia-smi's used memory during the run minus what was in use before it started.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CLI = ROOT / "src" / "Hushcue.Cli" / "bin" / "Release" / "net10.0" / "win-x64" / "Hushcue.Cli.exe"
MODELS = Path(os.environ["LOCALAPPDATA"]) / "Hushcue" / "models"
CLIPS = sorted((ROOT.parent / "subgen" / "reference").glob("*.mp4"))
SUBGEN_PYTHON = ROOT.parent / "subgen" / ".venv" / "Scripts" / "python.exe"

F16, Q8, Q5 = "ggml-large-v3-turbo.bin", "ggml-large-v3-turbo-q8_0.bin", "ggml-large-v3-turbo-q5_0.bin"
CONFIGS = {
    "subgen-a": [],  # SubGen itself (faster-whisper), see subgen_baseline.py
    "subgen-b": [],
    "cuda12-f16": ["--runtime", "cuda12", "--model", F16],
    "cuda12-f16-repeat": ["--runtime", "cuda12", "--model", F16],
    "cuda12-f16-dtw": ["--runtime", "cuda12", "--model", F16, "--dtw"],
    "cuda12-f16-flash": ["--runtime", "cuda12", "--model", F16, "--flash"],
    "cuda12-q8": ["--runtime", "cuda12", "--model", Q8],
    "cuda12-q5": ["--runtime", "cuda12", "--model", Q5],
    "vulkan-f16": ["--runtime", "vulkan", "--model", F16],
    "vulkan-q5": ["--runtime", "vulkan", "--model", Q5],
    "cpu-q5": ["--runtime", "cpu", "--model", Q5],
}


def vram_used() -> int:
    out = subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
                         capture_output=True, text=True).stdout
    return int(out.split()[0])


def run(name: str, extra: list[str]) -> None:
    out = ROOT / "bench-out" / name
    if name.startswith("subgen"):
        args = [str(SUBGEN_PYTHON), str(ROOT / "tools" / "bench" / "subgen_baseline.py"), str(out)]
    else:
        args = [str(CLI), "transcribe", "--out", str(out), *extra, *map(str, CLIPS)]
        i = args.index("--model")
        args[i + 1] = str(MODELS / args[i + 1])
    before, peak, done = vram_used(), [0], threading.Event()

    def sample() -> None:
        while not done.is_set():
            peak[0] = max(peak[0], vram_used())
            time.sleep(0.2)

    sampler = threading.Thread(target=sample, daemon=True)
    sampler.start()
    t = time.perf_counter()
    proc = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace")
    wall = time.perf_counter() - t
    done.set()
    sampler.join()
    if proc.returncode != 0:
        print(f"{name}: FAILED ({proc.returncode})\n{proc.stdout[-2000:]}\n{proc.stderr[-2000:]}")
        return
    metrics_path = out / "metrics.json"
    metrics = json.loads(metrics_path.read_text(encoding="utf-8"))
    metrics["vram_mb"] = peak[0] - before
    metrics["wall_seconds"] = wall
    metrics_path.write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    lines = [ln for ln in proc.stdout.splitlines() if ln.startswith(("Loaded", "Loading", *(c.stem + ":" for c in CLIPS)))]
    print(f"== {name} (VRAM {peak[0] - before} MB)\n  " + "\n  ".join(lines), flush=True)


if __name__ == "__main__":
    for name in sys.argv[1:] or CONFIGS:
        run(name, CONFIGS[name])
