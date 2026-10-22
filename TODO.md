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

## Phase 1b — CTranslate2 engine ✅ done

Full results: [docs/phase1b-ctranslate2.md](docs/phase1b-ctranslate2.md).

- [x] Installed Visual Studio 2022 Build Tools (C++ workload) and CMake
- [x] C++ bridge DLL (`native/hushcue_ct2`): plain C functions over CTranslate2 4.8.2's `models::Whisper`
      (load, encode, generate, align, detect language), built against its headers and the PyPI wheel's
      `ctranslate2.dll` by `native/build.ps1`
- [x] C#: P/Invoke layer and `WhisperModel` wrapper (`Engine/Native.cs`, `Engine/WhisperModel.cs`)
- [x] Ported faster-whisper 1.2.1's Python layer: log-mel features, byte-level BPE tokenizer, 30 s window loop,
      temperature fallback, segment splitting, word timestamps with punctuation merging, no-speech skipping,
      hallucination silence threshold, language detection (`Engine/`)
- [x] Uses the CTranslate2 models SubGen already downloaded
- [x] Decoder identical to faster-whisper on all 4 reference clips in every mode SubGen uses (same features in);
      with its own features (98% bit-identical to numpy's) it reproduces 98% of SubGen's words, WER 52.8% vs 54.9%
- [x] Speed: same engine time as faster-whisper measured call by call; a clean speed table needs an idle GPU
      (another app was using it during the runs)
- [x] Removed the Whisper.net spike; `Hushcue.Cli transcribe` now runs the CTranslate2 engine
- [x] Tests: features vs faster-whisper's (committed fixture), tokenizer vs faster-whisper's (skipped without the model)
- [ ] Speed table on an idle GPU (Phase 7)
- [ ] Non-ASCII model paths: CTranslate2 opens files with narrow strings; check or copy models to an ASCII path

## Phase 2 — Port the subtitle cleanup (pure logic) ✅ done

Ported from `../subgen/pipeline.py` into `src/Hushcue.Core/Subtitles/`, same constants and behaviour.

- [x] `Word`, `Cue` records; constants (MAX_LINE 42, MAX_CUE_CHARS 84, MAX_CUE_SECONDS 6.0, MIN_CUE_SECONDS 0.7, WORD_GAP_SPLIT 0.8, MIN_CUE_GAP 0.2)
- [x] `StripMarkers`, `SquashStretched`, `FindRepeatRuns`, `CollapseRepeats`
- [x] `BuildCues`, `MergeOrphans`, `DropJunk` (hallucination list), `FixTiming`
- [x] `WrapLines`, `Srt.Write`, `CoverageWarnings`, `FileNames.Unique`
- [x] Python string rules that affect output (whitespace set, `len` in code points, round half to even) in `Py.cs`
- [x] 13 of the 22 tests in `test_pipeline.py` are clean-up tests: ported to xUnit (`CleanupTests.cs`).
      The 7 that need a model (loop recovery, `transcribe_file`, presets) move to Phase 4; the other 2 test
      `evaluate.py` and a faster-whisper Python type, which don't carry over
- [x] Golden test (`GoldenTests.cs`): C# writes byte-identical SRTs to SubGen on 60 synthetic cases (committed) and
      40 real transcripts (`bench-out/golden/`, local only). Regenerate with `tools/bench/make_golden.py`
- [x] Checked the golden test catches bugs: 4 deliberate changes each failed 1–11 cases

## Phase 3 — Media (ffmpeg / ffprobe) ✅ done

Ported from `../subgen/burn.py` and `extract_audio` into `src/Hushcue.Core/Media/`.

- [x] Locate ffmpeg/ffprobe (bundled `ffmpeg\` next to the app first, then PATH), clear error if missing (`Tools`)
- [x] Extract audio as 16 kHz mono float, piped straight into memory (no temp WAV), with the optional enhance
      filter (`Audio.ReadAsync`)
- [x] Cancellation (`CancellationToken`) kills the ffmpeg process tree; partial outputs are deleted, retrying while
      Windows still holds the file
- [x] `ffprobe` JSON → `VideoInfo` (width, height, duration, audio codecs) (`Probe`)
- [x] Embed: soft track (.mkv, stream copy) and burn (.mp4, libass style sized to the picture) (`Embed`)
- [x] NVENC probe with x264 fallback, cached
- [x] `-progress pipe:1` parsing → fraction done
- [x] Output naming (`name.mkv`, `.subbed` if taken)
- [x] MKV → MP4 tool: remux without subtitle tracks, optional .srt extraction, batch with Recycle Bin
- [x] Recycle Bin via `SHFileOperationW` with the same silent, undoable flags as SubGen (the VisualBasic helper shows
      error dialogs). Checked by hand: a test file landed in the Recycle Bin, restorable
- [x] Ported all 17 tests from `test_burn.py` (`MediaTests.cs`, real ffmpeg encodes; skipped if ffmpeg is missing),
      plus 2 audio-extraction tests

## Phase 4 — Pipeline

Port `run_job` / `transcribe_file`. It is in `Pipeline/SubtitleJob.cs` and `Pipeline/JobRun.cs`.

- [x] Presets: Fast, Balanced (default), Thorough. **Best (Demucs) is dropped**
- [x] Settings: language, enhance, vocabulary, skip silence, embed mode, keep .srt, delete original
- [x] Silero VAD (ONNX Runtime, faster-whisper 1.2.1's `silero_vad_v6.onnx` and vad.py) when the preset or "skip
      silence" asks for it, plus restore_speech_timestamps (`Engine/SileroVad.cs`, `Engine/SpeechTimestamps.cs`).
      Matches faster-whisper's probabilities, speech timestamps and restored times on a fixture (`VadTests.cs`)
- [ ] VAD end to end on the reference clips: `fw_reference.py --vad-threshold 0.1 --vad-min-silence 1000` against
      `Hushcue.Cli transcribe` with the same options
- [ ] Check "skip silence" against SubGen: it uses faster-whisper's default VAD options (threshold 0.5, 2 s minimum
      silence), a guess, since SubGen's source was not at hand
- [x] Drop segments with no-speech prob > 0.6 and avg log-prob < -1.0
- [ ] Loop recovery: re-decode looped passages in isolation with repetition penalty 1.3
- [ ] Overlap: embed file N while transcribing file N+1 (`Channel<T>`). Written (`Pipeline/JobRun.cs`,
      `Pipeline/JobProgress.cs`), not tested yet; SubGen's run_job may differ in progress and failure handling, so
      check it against `test_run_job.py`
- [x] Finished files leave the queue
- [x] One combined progress value + status line across both stages
- [x] GPU → CPU fallback (int8 on CPU) on CUDA errors
- [x] Error handling: an embed failure keeps the .srt; summary error at the end
- [x] Write .srt files with CRLF line endings: SubGen's Python writes them in text mode on Windows
- [ ] Port the 14 tests from `test_run_job.py`, and the 7 model tests left from `test_pipeline.py`
      (6 loop-recovery / `transcribe_file`, 1 presets-are-deterministic)

## Phase 5 — Models

- [x] Store models in `%LOCALAPPDATA%\Hushcue\models` (CTranslate2 format, from Hugging Face); the Hugging Face
      cache SubGen fills is still used when present, which saves a download (`Pipeline/Models.cs`)
- [ ] First-run download with progress, resume and checksum. Written (`Pipeline/ModelDownload.cs`): files pinned to a
      Hugging Face commit with their SHA-256 in `Pipeline/Models.cs`, staged in `models\NAME.download`, not tested yet
- [x] Load the model once and keep it loaded between runs

## Phase 6 — WinUI 3 app (first version in place)

Built ahead of Phase 4/5. "Generate subtitles" runs the job in `Pipeline/SubtitleJob.cs` (no loop recovery yet).
Tested by UI Automation before VAD, embed overlap and the model download: a clip went through Generate (soft
track + .srt) end to end. MKV to MP4 page still to be tested by hand.

- [x] Window shell, Mica, dark/light theme following Windows, app icon
- [x] Mode switch: "Generate subtitles" / "MKV to MP4"
- [x] Drop zone with empty state, file list (multi-select, remove, clear, count), duplicate skip
- [x] File pickers (`InitializeWithWindow` with the window handle), output folder picker
- [x] Options: preset with hint, language, vocabulary, result (srt / burn / track), keep .srt, delete original, clean up audio, skip silence
- [x] Progress bar, status line, collapsible log (last 500 lines), Generate / Cancel
- [x] Confirm dialog before moving originals to the Recycle Bin; error dialog on failure
- [x] UI updates from the worker through `DispatcherQueue`
- [x] Remember the last options and output folder
- [x] Cancel the job cleanly when the window closes
- [ ] Test the MKV to MP4 page by hand
- [ ] Keyboard navigation and screen reader labels (only a few controls are labelled so far)

## Phase 7 — Measure and compare

- [ ] Speed and WER on all reference clips vs SubGen (`tools/bench/compare.py`)
- [ ] Publish the numbers in README

## Phase 8 — Ship

- [ ] Self-contained publish; try trimming / Native AOT
- [ ] Installer: MSIX or unpackaged (e.g. Inno Setup / WiX), decide
- [ ] ffmpeg bundling: GPL builds (with libx264) affect the licence; pick an LGPL build + NVENC, or keep ffmpeg as a user install
- [ ] Ship `ctranslate2.dll`, cuBLAS and cuDNN (~1.7 GB), or download them on first run
- [x] GitHub Actions: build and test on `windows-2025` (`.github/workflows/ci.yml`: CPU-only engine bridge, ffmpeg
      from Chocolatey). The 4 model tests skip there: no model on the runner
- [ ] GitHub Actions: release artifacts
- [ ] Third-party notices (CTranslate2, faster-whisper and Silero VAD are MIT)
- [ ] GitHub repo `hushcue`, README with screenshots, first release
