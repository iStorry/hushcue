# Hushcue — TODO

Accurate subtitles from any video, generated privately on your PC.
A C# + WinUI 3 rewrite of SubGen (`../subgen`, Python + PySide6 + faster-whisper).

Work top to bottom. Phase 1 is a go/no-go gate: don't start the UI until the engine is proven.

## Targets (measured on the Python app, RTX 4070, large-v3-turbo, Balanced)

| | SubGen today | Hushcue goal |
|---|---|---|
| RAM, window idle | ~85 MB | ≤ 110 MB (empty WinUI 3 window already measures ~110 MB) |
| RAM while transcribing (2 min file) | ~800 MB | ≤ 500 MB |
| RAM peak on a 30 min file | 1.8 GB (grows with length) | ≤ 600 MB, flat with length |
| VRAM | ~2.2 GB | ≤ 1.5 GB |
| Speed | ~51× real time | ≥ 40× real time |
| Accuracy (WER on `reference/` clips) | baseline | no worse than +1 point |
| Install size (without models) | ~3 GB venv (7.3 GB with torch) | ≤ 700 MB (CUDA) / ≤ 100 MB (Vulkan) |
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
- [x] Measured: the empty WinUI 3 window idles at ~110 MB RAM (Release), more than SubGen's whole PySide6 UI (~85 MB). Retry with trimming / Native AOT in Phase 8

## Phase 1 — Engine spike (go / no-go)

Whisper.net (whisper.cpp for .NET) is the only practical engine; CTranslate2 has no .NET bindings.

- [ ] Console app: Whisper.net + CUDA runtime + `large-v3-turbo` ggml model (try f16, q8_0, q5_0)
- [ ] Match SubGen's decoding: beam 5, **`WithTemperature(0)` and `WithTemperatureInc(0)`** (defaults are 0.2 and give non-reproducible output), `WithNoContext()`, `WithPrompt(vocabulary)`, English, no-speech 0.6, log-prob -1.0, token timestamps on
- [ ] Find the beam-size option, and confirm `ProcessAsync` accepts a `CancellationToken`
- [ ] Write SRTs for every clip in `../subgen/reference/` (plain text join, no cleanup yet)
- [ ] Score with `python ../subgen/evaluate.py` against the Python app's output on the same clips
- [ ] Record: WER, speed (× real time), RAM idle / peak, VRAM, model load time
- [ ] Repeat with the Vulkan runtime (works on AMD/Intel too; much smaller install)
- [ ] Run the same clip twice and confirm identical output
- [ ] **Decision:** CUDA or Vulkan runtime, which model quantization, go or no-go. Write the numbers into README

## Phase 2 — Port the subtitle cleanup (pure logic)

Port from `../subgen/pipeline.py`, keeping the same constants and behaviour.

- [ ] `Word`, `Cue` records; constants (MAX_LINE 42, MAX_CUE_CHARS 84, MAX_CUE_SECONDS 6.0, MIN_CUE_SECONDS 0.7, WORD_GAP_SPLIT 0.8, MIN_CUE_GAP 0.2)
- [ ] `StripMarkers`, `SquashStretched`, `FindRepeatRuns`, `CollapseRepeats`
- [ ] `BuildCues`, `MergeOrphans`, `DropJunk` (hallucination list), `FixTiming`
- [ ] `WrapLines`, `ToSrt`, `CoverageWarnings`, `UniquePath`
- [ ] Port the 22 tests from `test_pipeline.py` to xUnit
- [ ] Golden test: dump word lists from the Python app to JSON for each reference clip, check C# produces a byte-identical SRT

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
- [ ] Silero VAD via Whisper.net's VAD processor (threshold, min silence, 500 ms padding) when the preset or "skip silence" asks for it
- [ ] Drop segments with no-speech prob > 0.6 and avg log-prob < -1.0
- [ ] Loop recovery: re-decode looped passages in isolation. Whisper.net has no repetition penalty, so measure whether the re-decode alone fixes loops; if not, fork Whisper.net or P/Invoke whisper.cpp's logits filter
- [ ] Port faster-whisper's `hallucination_silence_threshold` (2.0 s) logic
- [ ] Overlap: embed file N while transcribing file N+1 (`Channel<T>`), finished files leave the queue
- [ ] One combined progress value + status line across both stages
- [ ] GPU → CPU fallback. whisper.cpp may crash the process on CUDA errors, so probe the GPU up front or run the engine in a child process
- [ ] Error handling: an embed failure keeps the .srt; summary error at the end
- [ ] Port the 14 tests from `test_run_job.py`
- [ ] Check the 30 min file: RAM stays flat with file length

## Phase 5 — Models

- [ ] Store models in `%LOCALAPPDATA%\Hushcue\models`
- [ ] First-run download (ggml Whisper model + Silero VAD) with progress, resume and checksum
- [ ] Load the model once and keep it loaded between runs; free it after the app has been idle for a while

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

- [ ] Re-run the Phase 1 measurements on the finished app against the targets table
- [ ] WER on all reference clips vs SubGen
- [ ] Publish the numbers in README

## Phase 8 — Ship

- [ ] Self-contained publish; try Native AOT / trimming (Whisper.net doesn't state support, so test it)
- [ ] Installer: MSIX or unpackaged (e.g. Inno Setup / WiX), decide
- [ ] ffmpeg bundling: GPL builds (with libx264) affect the licence; pick an LGPL build + NVENC, or keep ffmpeg as a user install
- [ ] CUDA runtime: ship the needed DLLs or require the CUDA toolkit; or go Vulkan only
- [ ] GitHub Actions: build, test, release artifacts
- [ ] GitHub repo `hushcue`, README with screenshots, first release

## Open questions

- Is token-timestamp accuracy close enough to faster-whisper's word timestamps? (Phase 1 answers this)
- Do loops come back without a repetition penalty? (Phase 4)
- CUDA vs Vulkan as the default runtime? (Phase 1)
