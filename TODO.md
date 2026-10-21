# Hushcue — TODO

Accurate subtitles from any video, generated privately on your PC.
A C# + WinUI 3 rewrite of SubGen (`../subgen`, Python + PySide6 + faster-whisper).

Work top to bottom. The engine must match SubGen before the UI work starts.

## Targets

The goal is a native app that performs the same as SubGen. RAM and VRAM are not tracked.

| | SubGen today | Hushcue goal |
|---|---|---|
| Speed (RTX 4070, large-v3-turbo, Balanced) | 46–53× real time | the same |
| Accuracy (WER on `reference/` clips, SubGen clean-up) | 54.9% | the same, ideally identical output |
| Setup | Python + setup.bat + winget ffmpeg | one installer |

## Phase 0 — Setup

- [x] Install the .NET 10 SDK (10.0.402, pinned in `global.json`)
- [x] WinUI 3 builds with the plain `dotnet` CLI: no Visual Studio workload needed
- [x] `git init`, .NET `.gitignore`, README stub
- [ ] Choose a LICENSE (see the ffmpeg licensing item in Phase 8)
- [x] Solution layout (`Hushcue.slnx`):
  - `src/Hushcue.Core`: settings, presets, subtitle cleanup, SRT, pipeline, ffmpeg, engine wrapper (no UI)
  - `src/Hushcue.Cli`: console front end (used for the spike, benchmarks and scripting)
  - `src/Hushcue.App`: WinUI 3 app (Windows App SDK 2.x, WinUI component only, unpackaged, runtime self-contained)
  - `tests/Hushcue.Core.Tests`: xUnit
- [x] `Directory.Build.props`: nullable on, warnings as errors, shared version, x64

## Phase 1 — Engine spike: Whisper.net ✅ done, no-go

Full results: [docs/phase1-engine-spike.md](docs/phase1-engine-spike.md). Benchmark tools: `tools/bench/`.

- [x] `Hushcue.Cli transcribe`: Whisper.net 1.9.1 with CUDA 12 / Vulkan runtimes, `large-v3-turbo` ggml (f16, q8_0, q5_0)
- [x] Matched SubGen's decoding (beam 5, temperature 0 with no fallback, no context, English, no-speech 0.6, log-prob -1.0)
- [x] Scored all four reference clips against the references and against SubGen, through SubGen's own clean-up
- [x] Repeatability: identical output on repeat runs
- [x] **Decision: no-go.** Best Whisper.net setup is 12 WER points worse than SubGen, word timings are off
      (median 0.4 s, 1 in 10 off by 5 s+), quantized models loop, and it runs at about half SubGen's speed
- [x] **New engine plan: CTranslate2** (SubGen's own engine) called natively from C#, so speed and output match

## Phase 1b — CTranslate2 engine

- [ ] Install a C++ toolchain: Visual Studio 2022 Build Tools (C++ workload) + CMake (needs your OK, ~5–7 GB)
- [ ] C++ bridge DLL (`native/hushcue_ct2`): plain C functions over CTranslate2 4.8.2's `models::Whisper`
      (load, encode, generate with beam / repetition penalty / no-speech prob, align for word timestamps,
      detect language, free). Build against CTranslate2's headers and the `ctranslate2.dll` from the PyPI wheel
- [ ] C#: P/Invoke layer + `SafeHandle`s in `Hushcue.Core`
- [ ] Port faster-whisper's Python layer to C#: log-mel spectrogram, Whisper tokenizer (byte-level BPE, encode
      for the vocabulary prompt), 30 s window seeking, segment splitting on timestamp tokens, word timestamps from
      `align` (incl. punctuation merging), no-speech / log-prob filtering, `hallucination_silence_threshold`
- [ ] Use the CTranslate2 models SubGen already downloads (`Systran/faster-whisper-*`, `mobiuslabsgmbh/...-turbo`)
- [ ] Check against SubGen on the four reference clips: same words, same timings, same speed
- [ ] Remove the Whisper.net spike code and packages from `Hushcue.Cli`

## Phase 2 — Port the subtitle cleanup (pure logic)

Port from `../subgen/pipeline.py`, keeping the same constants and behaviour.

- [ ] `Word`, `Cue` records; constants (MAX_LINE 42, MAX_CUE_CHARS 84, MAX_CUE_SECONDS 6.0, MIN_CUE_SECONDS 0.7, WORD_GAP_SPLIT 0.8, MIN_CUE_GAP 0.2)
- [ ] `StripMarkers`, `SquashStretched`, `FindRepeatRuns`, `CollapseRepeats`
- [ ] `BuildCues`, `MergeOrphans`, `DropJunk` (hallucination list), `FixTiming`
- [ ] `WrapLines`, `ToSrt`, `CoverageWarnings`, `UniquePath`
- [ ] Port the 22 tests from `test_pipeline.py` to xUnit
- [ ] Golden test: feed SubGen's word lists (`bench-out/subgen-a/*.words.json`) through the C# clean-up, check the SRT is byte-identical

## Phase 3 — Media (ffmpeg / ffprobe)

Port from `../subgen/burn.py` and `extract_audio`.

- [ ] Locate ffmpeg/ffprobe (bundled first, then PATH), clear error if missing
- [ ] Extract audio as 16 kHz mono PCM, piped straight into memory (no temp WAV), with the optional enhance filter (`highpass=f=80,afftdn=nf=-25,dynaudnorm=f=150:g=15`)
- [ ] Cancellation kills the ffmpeg process; partial outputs are deleted (with the retry-while-locked behaviour)
- [ ] `ffprobe` JSON → `VideoInfo` (width, height, duration, audio codecs)
- [ ] Embed: soft track (.mkv, stream copy) and burn (.mp4, libass style sized to the picture)
- [ ] NVENC probe with x264 fallback, cached
- [ ] `-progress pipe:1` parsing → fraction done
- [ ] Output naming (`name.mkv`, `.subbed` if taken)
- [ ] MKV → MP4 tool: remux without subtitle tracks, optional .srt extraction
- [ ] Recycle Bin via `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)`
- [ ] Port the 17 tests from `test_burn.py`

## Phase 4 — Pipeline

Port `run_job` / `transcribe_file`.

- [ ] Presets: Fast, Balanced (default), Thorough. **Best (Demucs) is dropped**
- [ ] Settings: language, enhance, vocabulary, skip silence, embed mode, keep .srt, delete original
- [ ] Silero VAD (ONNX Runtime, same model and parameters as faster-whisper) when the preset or "skip silence" asks for it
- [ ] Drop segments with no-speech prob > 0.6 and avg log-prob < -1.0
- [ ] Loop recovery: re-decode looped passages in isolation with repetition penalty 1.3
- [ ] Overlap: embed file N while transcribing file N+1 (`Channel<T>`), finished files leave the queue
- [ ] One combined progress value + status line across both stages
- [ ] GPU → CPU fallback (int8 on CPU) on CUDA errors
- [ ] Error handling: an embed failure keeps the .srt; summary error at the end
- [ ] Port the 14 tests from `test_run_job.py`

## Phase 5 — Models

- [ ] Store models in `%LOCALAPPDATA%\Hushcue\models` (CTranslate2 format, from Hugging Face)
- [ ] First-run download with progress, resume and checksum
- [ ] Load the model once and keep it loaded between runs

## Phase 6 — WinUI 3 app

- [ ] Window shell, Mica, dark/light theme following Windows, app icon
- [ ] Mode switch: "Generate subtitles" / "MKV to MP4"
- [ ] Drop zone with empty state, file list (multi-select, remove, clear, count), duplicate skip
- [ ] File pickers (`InitializeWithWindow` with the window handle), output folder picker
- [ ] Options: preset with hint, language, vocabulary, result (srt / burn / track), keep .srt, delete original, clean up audio, skip silence
- [ ] Progress bar, status line, collapsible log (last 500 lines), Generate / Cancel
- [ ] Confirm dialog before moving originals to the Recycle Bin; error dialog on failure
- [ ] UI updates from the worker through `DispatcherQueue`
- [ ] Remember the last options and output folder
- [ ] Cancel the job cleanly when the window closes
- [ ] Keyboard navigation and screen reader labels

## Phase 7 — Measure and compare

- [ ] Speed and WER on all reference clips vs SubGen (`tools/bench/compare.py`)
- [ ] Publish the numbers in README

## Phase 8 — Ship

- [ ] Self-contained publish; try trimming / Native AOT
- [ ] Installer: MSIX or unpackaged (e.g. Inno Setup / WiX), decide
- [ ] ffmpeg bundling: GPL builds (with libx264) affect the licence; pick an LGPL build + NVENC, or keep ffmpeg as a user install
- [ ] Ship `ctranslate2.dll`, cuBLAS and cuDNN (~1.7 GB), or download them on first run
- [ ] GitHub Actions: build, test, release artifacts
- [ ] GitHub repo `hushcue`, README with screenshots, first release
