# Phase 1b: CTranslate2 engine

**Result: Hushcue's engine produces SubGen's output.** It runs the same CTranslate2 build and model files as
faster-whisper, and the C# port of faster-whisper 1.2.1's decoding reproduces SubGen word for word and timestamp for
timestamp when given the same input.

Date: 2026-10-21. RTX 4070, Windows 11, `large-v3-turbo` (float16), SubGen's Balanced decoding.

## How it fits together

| Piece | What it is |
|---|---|
| `native/hushcue_ct2` | ~200 lines of C++: a plain C interface over CTranslate2's `models::Whisper` (load, encode, generate, align, detect language). Built against CTranslate2 4.8.2's headers and the `ctranslate2.dll` from the PyPI wheel, through an import library made from its exports |
| `native/build.ps1` | Fetches the headers, builds `native/bin/hushcue_ct2.dll`, and gathers `ctranslate2.dll`, cuBLAS and cuDNN into `%LOCALAPPDATA%\Hushcue\runtime` |
| `Engine/Native.cs`, `WhisperModel.cs` | P/Invoke layer and a C# wrapper |
| `Engine/MelSpectrogram.cs` | faster-whisper's log-mel features (STFT, Slaney mel filters, log10, clamp) |
| `Engine/WhisperTokenizer.cs` | Byte-level BPE from the model's tokenizer.json, plus faster-whisper's special tokens, non-speech suppression and word splitting |
| `Engine/Transcriber.cs` | faster-whisper's transcription loop: 30 s windows, prompts, temperature fallback, no-speech skipping, segment splitting, word timestamps from alignment, punctuation merging, silence-before-hallucination skipping, language detection |

## Checks

**Decoder: identical.** Given faster-whisper's own features, the C# engine's words and timings are byte-identical to
faster-whisper's on all four reference clips, in every mode SubGen uses:

| Mode | Clips identical |
|---|---|
| Balanced decoding (beam 5, temperature 0, word timestamps) | 4 / 4 |
| Auto language detection + vocabulary prompt + hallucination silence filter (2.0 s) | 4 / 4, same language probabilities to 4 decimals |
| Repetition penalty 1.3 (loop recovery) | 4 / 4 |

**Features: within one float32 step.** The C# log-mel features match numpy's to within 1.2e-7, and 98% are
bit-identical. The last 2% differ because numpy computes its FFT in float32 and its filter product through OpenBLAS,
whose exact operation order varies with the CPU; the C# code follows numpy's magnitude formula and the closest
summation order measured. SubGen itself is not bit-reproducible across CPUs for the same reason.

**End to end (C# computes its own features):**

| | SubGen | Hushcue |
|---|---|---|
| WER vs reference (SubGen clean-up) | 54.9% | 52.8% |
| SubGen's words reproduced | — | 98.0% (924 / 943) |
| Word timing vs SubGen, median / 90th pct | — | 0.00 s / 0.04 s |
| Speed, same session (encode + generate time, 4 clips) | 29.2 s | 26.8 s |

The speed numbers were taken while another application was using the GPU heavily, so absolute speeds varied run to
run (SubGen 19–58× real time); measured call by call in the same session, Hushcue's engine time is the same or lower.
A clean speed table needs an otherwise idle GPU.

## Reproduce

```bash
powershell -ExecutionPolicy Bypass -File native/build.ps1
dotnet build src/Hushcue.Cli -c Release
../subgen/.venv/Scripts/python tools/bench/subgen_baseline.py bench-out/subgen-a
src/Hushcue.Cli/bin/Release/net10.0/win-x64/Hushcue.Cli.exe transcribe --model MODEL_DIR --out bench-out/ct2 ../subgen/reference/*.mp4
../subgen/.venv/Scripts/python tools/bench/compare.py

# decoder identity: same features on both sides
../subgen/.venv/Scripts/python tools/bench/fw_reference.py bench-out/fw-full --language auto --prompt "..." --hallucination-silence 2.0
Hushcue.Cli.exe transcribe --model MODEL_DIR --out bench-out/cs-full --features bench-out/features-0 --language auto --prompt "..." --hallucination-silence 2.0 ../subgen/reference/*.mp4
```

`MODEL_DIR` is the CTranslate2 model SubGen downloaded, e.g.
`%USERPROFILE%\.cache\huggingface\hub\models--mobiuslabsgmbh--faster-whisper-large-v3-turbo\snapshots\<id>`.
