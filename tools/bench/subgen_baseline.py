"""Run SubGen (faster-whisper) on the reference clips and save what Hushcue is compared against.

    ..\\subgen\\.venv\\Scripts\\python tools\\bench\\subgen_baseline.py OUT_DIR [CLIP ...]

For each clip writes, into OUT_DIR:
  NAME.raw.srt    one cue per Whisper segment, no SubGen clean-up (engine-to-engine comparison)
  NAME.words.json every word with start/end, as faster-whisper reports it
  NAME.full.srt   SubGen's finished output (Balanced preset, all clean-up)
plus metrics.json (load time, speed, peak RAM). Clips default to ../subgen/reference/*.mp4.
Decoding matches SubGen's Balanced preset: large-v3-turbo, beam 5, temperature 0, no VAD, no context.
"""
from __future__ import annotations

import ctypes
import json
import sys
import tempfile
import threading
import time
from ctypes import wintypes
from pathlib import Path

SUBGEN = Path(__file__).resolve().parents[3] / "subgen"
sys.path.insert(0, str(SUBGEN))
import pipeline  # noqa: E402


class _Counters(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]


ctypes.windll.kernel32.GetCurrentProcess.restype = wintypes.HANDLE
ctypes.windll.psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(_Counters), wintypes.DWORD]


def working_set_mb() -> float:
    c = _Counters()
    c.cb = ctypes.sizeof(c)
    ctypes.windll.psapi.GetProcessMemoryInfo(ctypes.windll.kernel32.GetCurrentProcess(), ctypes.byref(c), c.cb)
    return c.WorkingSetSize / 2**20


class PeakSampler:
    """Highest working set seen while the block runs (sampled every 50 ms)."""

    def __enter__(self):
        self.peak, self._stop = working_set_mb(), threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()
        return self

    def _run(self):
        while not self._stop.is_set():
            self.peak = max(self.peak, working_set_mb())
            time.sleep(0.05)

    def __exit__(self, *exc):
        self._stop.set()
        self._thread.join()


def srt(cues: list[tuple[float, float, str]]) -> str:
    return pipeline.to_srt([pipeline.Cue(s, e, t.strip()) for s, e, t in cues if t.strip()])


def main(argv: list[str]) -> int:
    out = Path(argv[1])
    out.mkdir(parents=True, exist_ok=True)
    clips = [Path(p) for p in argv[2:]] or sorted((SUBGEN / "reference").glob("*.mp4"))
    preset = pipeline.PRESETS["Balanced"]
    settings = pipeline.Settings(preset="Balanced")
    cancel = threading.Event()

    t = time.perf_counter()
    model = pipeline.load_model(preset.model, "cuda", print)
    metrics = {"engine": "faster-whisper", "model": preset.model, "load_seconds": time.perf_counter() - t,
               "after_load_ws_mb": working_set_mb(), "clips": {}}

    with tempfile.TemporaryDirectory() as tmp:
        for clip in clips:
            wav = Path(tmp) / f"{clip.stem}.wav"
            pipeline.extract_audio(clip, wav, False, cancel)
            seconds = wav.stat().st_size / 32000
            with PeakSampler() as raw_peak:
                t = time.perf_counter()
                segments, _ = model.transcribe(
                    str(wav), language="en", beam_size=5, temperature=[0.0], condition_on_previous_text=False,
                    vad_filter=False, word_timestamps=True, no_speech_threshold=0.6, log_prob_threshold=-1.0)
                segments = list(segments)
                raw_seconds = time.perf_counter() - t
            words = [{"word": w.word, "start": w.start, "end": w.end} for s in segments for w in (s.words or [])]
            (out / f"{clip.stem}.raw.srt").write_text(srt([(s.start, s.end, s.text) for s in segments]), encoding="utf-8")
            (out / f"{clip.stem}.words.json").write_text(json.dumps(words, indent=1), encoding="utf-8")

            with PeakSampler() as full_peak:
                t = time.perf_counter()
                cues = pipeline.transcribe_file(model, wav, settings, preset, cancel, lambda f: None)
                full_seconds = time.perf_counter() - t
            (out / f"{clip.stem}.full.srt").write_text(pipeline.to_srt(cues), encoding="utf-8")

            metrics["clips"][clip.stem] = {
                "audio_seconds": seconds,
                "raw_seconds": raw_seconds, "raw_realtime": seconds / raw_seconds, "raw_peak_ws_mb": raw_peak.peak,
                "full_seconds": full_seconds, "full_peak_ws_mb": full_peak.peak,
                "segments": len(segments), "words": len(words),
            }
            print(f"{clip.stem}: {seconds / raw_seconds:.0f}x realtime, {len(words)} words, peak {raw_peak.peak:.0f} MB")

    (out / "metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
