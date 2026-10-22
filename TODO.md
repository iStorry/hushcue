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
- [x] LICENSE: MIT (chosen for v0.1.0, see Phase 8)
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
- [x] Speed against SubGen, same conditions (Phase 7): Hushcue's full job is ~9% faster. An idle-GPU table is still
      open: another app kept the GPU at ~40% load during every run
- [x] Non-ASCII model paths: no workaround needed. CTranslate2 4.8.2 converts the UTF-8 path to UTF-16 before opening
      files (`src/filesystem.cc`), and the bridge passes UTF-8. `ModelPathTests` loads the model through junctions named
      `modèle` and `日本語 モデル` (ANSI-encoded paths fail, 8.3 short paths work, checked by hand)

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

## Phase 4 — Pipeline ✅ done

Port `run_job` / `transcribe_file`. It is in `Pipeline/SubtitleJob.cs`, `Pipeline/JobRun.cs` and
`Pipeline/FileTranscription.cs`. Full results: [docs/phase4-pipeline.md](docs/phase4-pipeline.md).

- [x] Presets: Fast, Balanced (default), Thorough. **Best (Demucs) is dropped**
- [x] Settings: language, enhance, vocabulary, skip silence, embed mode, keep .srt, delete original
- [x] Silero VAD (ONNX Runtime, faster-whisper 1.2.1's `silero_vad_v6.onnx` and vad.py) when the preset or "skip
      silence" asks for it, plus restore_speech_timestamps (`Engine/SileroVad.cs`, `Engine/SpeechTimestamps.cs`).
      Matches faster-whisper's probabilities, speech timestamps and restored times on a fixture (`VadTests.cs`)
- [x] VAD end to end on the reference clips: `fw_reference.py --vad-threshold 0.1 --vad-min-silence 1000
      --vad-speech-pad 500` against `Hushcue.Cli transcribe` with the same options: words and times identical on 4 / 4
      clips with the same features (and the same VAD-kept audio, bit for bit)
- [x] "Skip silence" checked against SubGen's `_decode_words`: every VAD run (Fast, or skip silence on any preset)
      uses threshold = the preset's or 0.1, minimum silence = the preset's or 1000 ms, speech pad 500 ms. Hushcue had
      guessed faster-whisper's defaults (0.5 / 2000 / 400) for skip silence and left the 500 ms pad out of Fast; fixed
      (`Presets.Vad`), and the finished subtitles of Fast and skip silence are identical to SubGen's with the same
      features
- [x] Drop segments with no-speech prob > 0.6 and avg log-prob < -1.0
- [x] Loop recovery (`FileTranscription.RecoverLoops`, on by default as in SubGen): loops padded by 2 s, nearby ones
      merged, re-decoded once with repetition penalty 1.3 from the in-memory audio sliced as `read_wav_slice` reads
      the WAV, originals kept if the re-decode is empty, then `CollapseRepeats`
- [x] Finished subtitles identical to SubGen's real `transcribe_file` on 4 / 4 clips given the same features
      (Balanced with and without loop recovery, Fast, skip silence). With its own features: 97.7% of SubGen's raw
      words, 76% of its finished words (the clean-up and loop recovery amplify timing differences), WER vs reference
      49.1% against SubGen's 55.1%. `Hushcue.Cli job --features DIR` + `tools/bench/subgen_job.py`
- [x] Overlap: embed file N while transcribing file N+1 (`Channel<T>`, `Pipeline/JobRun.cs`, `Pipeline/JobProgress.cs`).
      Tested; same progress values as SubGen's run_job. Two deliberate differences: a failed transcription lets the
      queued embeds finish (SubGen dropped them, losing their transcripts when "keep .srt" was off), and an unexpected
      embed error is reported as itself (SubGen could report "cancelled")
- [x] Finished files stay in the queue, ticked, and are skipped by the next run (re-adding one queues it again)
- [x] One combined progress value + status line across both stages
- [x] GPU → CPU fallback (int8 on CPU) on CUDA errors
- [x] Error handling: an embed failure keeps the .srt; summary error at the end
- [x] Write .srt files with CRLF line endings: SubGen's Python writes them in text mode on Windows
- [x] Ported the 14 tests from `test_run_job.py` (`JobTests.cs`; the 2 for the dropped Best preset adapted) and the
      7 model tests left from `test_pipeline.py` (`TranscriptionTests.cs`), with a fake model through `JobSteps` (the
      seam for the transcriber, audio, embed and Recycle Bin), plus cancel, GPU fallback, download-in-job and a failed
      transcription finishing the queued embed

## Phase 5 — Models ✅ done

- [x] Store models in `%LOCALAPPDATA%\Hushcue\models` (CTranslate2 format, from Hugging Face); the Hugging Face
      cache SubGen fills is still used when present, which saves a download (`Pipeline/Models.cs`)
- [x] First-run download with progress, resume and checksum (`Pipeline/ModelDownload.cs`): files pinned to a Hugging
      Face commit with their SHA-256 in `Pipeline/Models.cs`, staged in `models\NAME.download`. Tested against a fake
      Hugging Face on `HttpListener` (`ModelDownloadTests.cs`: progress, resume through the redirect, range ignored,
      checksum and size failures, cancel, stall, HTTP errors). Fixed: HttpClient's own timeout was reported as a
      cancel. Not run against the real Hugging Face (multi-GB)
- [x] Load the model once and keep it loaded between runs

## Phase 6 — WinUI 3 app (first version in place)

"Generate subtitles" runs the job in `Pipeline/SubtitleJob.cs` (with loop recovery). Run and checked on Windows by
UI Automation after the redesign (Phase 6b): .srt only, subtitle track and burn-in end to end on a 3-minute clip and
a batch of four, a non-media file, a file with no audio, an audio-only file while embedding, Cancel (button and Esc)
mid-burn, and closing the window mid-run. MKV to MP4 page still to be tested by hand.

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
- [x] Keyboard navigation and screen reader labels: every control has a name (rows read as "name, status"), Tab
      order follows the layout, Tab enters a card group on the checked card and arrows move and check
      (`Pages/ChoiceGroup.cs`), the status line and setup checks are live regions. Checked by walking the Tab order
      and the UI Automation tree. Not checked with Narrator itself, nor at 200% text size (a system setting); text
      wraps or trims and the options scroll, so it should hold
- [x] Single instance: later launches (one per file from Explorer's menu) hand their files to the running window
      over a named pipe and bring it to the front; the app holds the mutex `Hushcue` (installer `AppMutex`). Checked
      with three launches at once and one onto a minimised window

## Phase 6b — UI redesign

Mockups in [docs/mocks/](docs/mocks/) (HTML to open in a browser, PNG to view anywhere). Work through them in order:
idea 1 rearranges controls that mostly exist already; idea 2 is the feature that would set Hushcue apart.

**Idea 1: a queue that shows progress for each file** ([mock](docs/mocks/1-main-window-queue.png))

- [x] Core reports each file's state (`FileProgress`: waiting, working, done, failed) from `SubtitleJob` and the
      MKV to MP4 converter, alongside the overall value
- [x] Each row shows its own icon, status, progress bar and percentage; speed and time left while transcribing
      (`TranscribeRate`, measured from the first progress report so model loading doesn't count)
- [x] Finished files stay ticked with "Show in folder"; failed files show the reason and are retried next run
- [x] Preset as three cards in a row, result as three cards with the tradeoff on each (`ChoiceCard` style)
- [x] Language, vocabulary, audio clean-up and skip silence under "More options", opened when any differ from the
      defaults
- [x] Build and run it on Windows. Fixed: the preset cards were clipped (RadioButton's default MinWidth of 120),
      detail lines wrapped, the caption buttons ignored the app theme; added an accent fill on the checked card, a
      tile behind each row's glyph, a remove button per row, and a title bar (WinUI `TitleBar`) with the tools.
      Checked in light and dark, at 900x640 and maximised on a 3440 px screen (pages stop at 1640 px)
- [x] Probe files when they are added (`Media/MediaProbe.cs`): length in the row and the header; warnings in the row
      before the run. Unreadable files and files without audio are skipped; files without a picture get an .srt
      only when embedding (run as their own group)
- [x] Total time left for the whole batch in the bottom bar (`BatchEstimate`, weighted by length; reading audio and
      loading the model don't count)
- [x] "Offline · <GPU>" status (now an entry at the bottom of the sidebar, was a badge in the title bar; GPU name from the registry's display adapters); "CPU" without CUDA,
      without the GPU libraries, or when the job falls back. It opens the system check
- [x] Live speed hint under the preset ("~52× real time on your GPU"), measured per preset and device (Core times
      each file from the start of decoding until its subtitles are ready) and kept in the settings (blended with the
      last run)
- [x] The app never reads Core's log or status wording: Core reports each file's stage (`FileProgress.Stage`, speed and
      time left) and what the job did as data (`Pipeline/JobEvent.cs`: device and GPU → CPU fallback, model download,
      finished transcripts with their cues, measured speed, .srt and coverage warnings, recycled originals). The log
      is only shown in the log view

**Idea 2: review and edit before embedding** ([mock](docs/mocks/2-review-editor.png))

Status: merged into `main`, built but never run (the review agent was stopped mid-way; testing is with the user).
Core: `src/Hushcue.Core/Review/` (cue editor with undo, flags, waveform envelope, re-run a part, review before
embedding; `ReviewTests.cs`); the job reports stages and events as data, so the app no longer reads Core's log
wording. App: Review page, live transcript, waveform timeline.

- [x] Open existing subtitles in the editor without transcribing: the "Edit subtitles" page (open a video; its
      `name.srt` / `name.en.srt` is found, or pick one; recent edits listed), drop a video with its .srt, pass an .srt
      on the command line, or Explorer's "Edit subtitles with Hushcue" on .srt files (installer). Pairing in
      `Subtitles/SubtitlePairs.cs` (tested); app side built, not run
- [ ] Subtitle tracks inside an .mkv: extract to edit, write back as a new file
- [ ] Run and check every item below in the app
- [ ] Show cues as they are transcribed in a side panel
- [ ] Review page: video preview with the subtitles drawn live, editable and searchable cue list
- [ ] Flag likely hallucinations (`DropJunk` list) and gaps (`CoverageWarnings`); "Re-run this part" re-decodes
      just that stretch (ties in with loop recovery, Phase 4)
- [ ] Waveform timeline with cue blocks whose edges can be dragged to fix timing
- [ ] "Embed into video" as the last step, after review; export the edited .srt

**Idea 3: first run and the finished screen** ([mock](docs/mocks/3-first-run-and-done.png))

- [x] Setup screen (`Controls/SetupView`), shown at start when ffmpeg or the model is missing (or the engine can't
      load), and from the sidebar status entry as "System check": GPU and CUDA, the GPU libraries (`RuntimeDownload`, only with a
      CUDA device), ffmpeg and NVENC ("Check again" picks up a fresh PATH), the model with progress, speed, pause and
      resume; "Continue on CPU" without a GPU. The real large-v3-turbo download was run through it: paused at
      1.4 GB, resumed, finished with checksums, moved into place (then removed again). The GPU libraries download
      itself was not run here (this PC has them). No smaller model to offer yet (only the large ones are pinned)
- [x] Finished screen (`Controls/FinishedPanel`) in place of the options: video length, time taken, speed, coverage
      warnings and failures by file, Open folder, and "Put them back" for originals moved to the Recycle Bin
      (`Media/RecycleBinRestore.cs`, plus Open Recycle Bin). Restore checked on two recycled test files

**Navigation, Settings, About** (built, not run)

- [x] Sidebar (`NavigationView`) with the three tools: Generate subtitles, Edit subtitles, MKV to MP4; Settings and
      About at the bottom. Replaces the tabs in the title bar
- [x] Settings page: language, vocabulary, audio clean-up, skip silence, keep the .srt, Recycle Bin, review before
      embedding, theme (Windows / light / dark), where transcription runs, open the models and data folder. The
      options card on Generate keeps only Quality and Result, with a one-line summary of the rest and a link
- [x] About page: version, privacy note, what it is built on, source code, report a problem, third-party notices
- [x] Big windows: options card as tall as its content; on wide pages (1400+ px) the live transcript and finished
      summary get their own column; pages grow to 2560 px for ultra-wide screens
- [ ] Check all of the above in the running app (light and dark, 900 px to ultra-wide)

**Windows touches**

- [x] Progress on the taskbar button (`ITaskbarList3`, seen on the taskbar during a burn) and the button flashes when
      a batch ends with the window in the background (`FlashWindowEx`; not watched happening). No toast notification:
      it needs the App Notifications package and registration for an unpackaged app
- [ ] Compact mini window that stays on top while a batch runs
- [ ] Explorer right-click "Generate subtitles with Hushcue", and drop files on the taskbar button
- [x] Shortcuts: Ctrl+O add files, Ctrl+Enter start (from anywhere, also the list), Delete remove selected, Esc
      cancel after asking; checked by sending the keys

## Phase 7 — Measure and compare

Results: [docs/phase7-results.md](docs/phase7-results.md).

- [x] Speed and WER on all reference clips vs SubGen, full job (`subgen_job.py`, `Hushcue.Cli job`, `compare_srt.py`):
      26.3 s vs 28.9 s for 12 min of audio; WER vs references 49.1% vs 55.1%; repeat runs identical
- [x] Publish the numbers in README
- [ ] Repeat the speed table on an idle GPU

## Phase 8 — Ship

Full notes and measurements: [docs/shipping.md](docs/shipping.md).

- [x] Self-contained publish (`tools/publish.ps1`): ReadyToRun (window 25% sooner, +44 MB), app-local VC++ runtime,
      `EnableMsixTooling` (without it the published app has no resources.pri and crashes at start). 290 MB folder,
      66 MB installer, 102 MB zip. Published app checked: starts, and loads its libraries from its own folder
- [ ] Trimming / Native AOT: not usable yet. Trimmed builds crash in XAML at start; needs a `JsonSerializerContext`
      for `AppSettings` and CsWinRT trimming support first (would save about 90 MB)
- [x] Installer: Inno Setup (`installer/hushcue.iss`), per-user without admin, Start menu, uninstaller, optional
      Explorer "Generate subtitles with Hushcue" for the 18 media types. Built, installed, launched (shortcut and
      Explorer verb) and uninstalled cleanly here
- [x] Installer follow-ups in the app: single instance (`Services/SingleInstance.cs`), so multi-select in Explorer
      queues into one window; the named mutex "Hushcue" is the installer's `AppMutex`
- [ ] Code signing (SmartScreen warns on unsigned installers): certificate or Azure Trusted Signing
- [x] ffmpeg: not bundled for v0.1.0, the user installs it (`winget install Gyan.FFmpeg`; the setup screen says so).
      `publish.ps1 -BundleFfmpeg` stays available. Gyan's builds are GPL (libx264); options in docs/shipping.md
- [x] GPU libraries downloaded on first run, not shipped (`Pipeline/RuntimeDownload.cs`): cuBLAS + NVRTC wheels from
      PyPI, pinned by SHA-256, 630 MB. cuDNN is not needed at all (CTranslate2's Windows build doesn't use it), which
      saves 1.1 GB. Tested with fake wheels; the real download checked once (pins match SubGen's DLLs)
- [x] Setup screen uses `RuntimeDownload.Check()` / `DownloadAsync` (`Controls/SetupView.xaml.cs`)
- [x] GitHub Actions: build and test on `windows-2025` (`.github/workflows/ci.yml`: CPU-only engine bridge, Gyan's
      ffmpeg pinned by SHA-256). The model tests skip there: no model on the runner
- [x] GitHub Actions: release (`.github/workflows/release.yml`): on a `v*` tag, builds the installer, a zip and
      `SHA256SUMS.txt` and publishes them as the latest release. First run: the `v0.1.0` tag
- [x] Third-party notices (`THIRD-PARTY-NOTICES.md`, upstream notice files in `licenses\` of the package)
- [x] LICENSE: MIT, Jatinjit Singh. Permissive, so it sits well with the bundled Microsoft and Intel binaries'
      terms; see docs/shipping.md
- [x] Automatic updates (`Hushcue.Core/Updates`, `App/Services/Updater.cs`): once a day on start (Settings can turn it
      off, About has "Check for updates") the app asks GitHub for the latest release; "Update available" offers
      Update now / Skip this version / Later. The installer is downloaded, checked against `SHA256SUMS.txt`, run
      silently (`/RELAUNCH=1` opens Hushcue again afterwards). A portable copy opens the release page instead.
      Needs the repository to be public: the check is an anonymous GitHub API call
- [x] GitHub repo `iStorry/hushcue`, README with screenshots (new sidebar layout, neutral sample video), test data
      and fixtures with neutral wording
- [ ] First release: `v0.1.0` tag pushed; check the Actions run and the published release
