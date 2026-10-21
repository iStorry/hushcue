# Phase 1: engine spike — Whisper.net vs SubGen

**Decision: no-go for Whisper.net.** Hushcue keeps SubGen's engine, CTranslate2, called natively from C#.

Date: 2026-10-21. RTX 4070, Windows 11, `large-v3-turbo`, SubGen's Balanced decoding (beam 5, temperature 0 with
no fallback, no previous-text context, English, no VAD) on both sides.

## Results

Four 3-minute clips from `../subgen/reference/` (clips 1 to 4), 470 reference words in total.
Every engine's words go through SubGen's own clean-up before scoring, so the comparison is like-for-like.

| Run | WER vs reference | Words that differ from SubGen | Word timing vs SubGen, median / 90th pct | Speed |
|---|---|---|---|---|
| **SubGen (faster-whisper)** | **54.9%** | — | — | **46–53× real time** |
| Whisper.net CUDA 12, f16 | 67.0% | 66% | 0.42 s / 5.4 s | 25–27× |
| Whisper.net CUDA 12, f16 + DTW timing | 89.8% | 97% | 0.29 s / 34 s | 32× |
| Whisper.net CUDA 12, f16 + flash attention | 77.0% | 71% | 0.36 s / 37 s | 32× |
| Whisper.net CUDA 12, q8_0 | 108.3% | 101% | 0.46 s / 11 s | 26× |
| Whisper.net CUDA 12, q5_0 | 88.7% | 83% | 0.37 s / 27 s | 25× |
| Whisper.net Vulkan, f16 | 79.8% | 72% | 0.59 s / 20 s | 9× |
| Whisper.net Vulkan, q5_0 | 96.2% | 91% | 0.64 s / 8 s | 21× |

- **Accuracy:** the best Whisper.net setup is 12 points worse than SubGen (the gate was +1 point). Quantized models
  and the alternative timing modes are worse still: they fall into repetition loops (clip 4: 340 words at f16,
  537 at q5, 598 on Vulkan q5), and Whisper.net has no repetition penalty to recover them.
- **Word timing:** half the words land 0.4 s or more away from SubGen's, and one in ten by 5 s or more. SubGen's cue
  building depends on these timings.
- **Speed:** about half of SubGen's on the same GPU.
- **Repeatability:** fine. Two identical Whisper.net runs gave byte-identical output (temperature 0, no fallback).

The reference SRTs are drafts, not fully hand-corrected transcripts (SubGen itself scores 55%), so the absolute WER
numbers are high. The comparison between engines on the same references still holds.

## Why the same engine

SubGen's speed and accuracy come from CTranslate2, a C++ library; faster-whisper is a Python layer around it.
Calling CTranslate2 directly from C# keeps the same GPU kernels and the same decoder, so Hushcue can produce the same
output at the same speed, and gets the repetition penalty and accurate word alignment that Whisper.net lacks.

Costs: NVIDIA GPUs only (CPU still works), cuBLAS + cuDNN must ship (~1.7 GB install), and a small C++ bridge DLL
needs a C++ toolchain to build.

## Reproduce

```bash
../subgen/.venv/Scripts/python tools/bench/subgen_baseline.py bench-out/subgen-a
python tools/bench/run_hushcue.py            # Whisper.net configurations (needs the ggml models)
../subgen/.venv/Scripts/python tools/bench/compare.py
```

Whisper.net needs `cudart64_12.dll`, `cublas64_12.dll` and `cublasLt64_12.dll` in `%LOCALAPPDATA%\Hushcue\cuda12`, and
the ggml models in `%LOCALAPPDATA%\Hushcue\models`.
