# Phase 4: Pipeline

**Result: Hushcue's subtitle job is SubGen's.** Given the same log-mel features, it writes the same finished `.srt`
files as SubGen, byte for byte, on all four reference clips with Balanced (loop recovery on and off), Fast and
"skip silence". With its own features (98% bit-identical to numpy's) it reproduces 97.7% of SubGen's raw words. In
the finished subtitles that becomes 76% of SubGen's words, because SubGen's clean-up amplifies small timing
differences. Its word error rate against the hand-made references is 49.1%, against SubGen's 55.1%.

Date: 2026-10-22. RTX 4070 (shared with another application, so no speed figures), Windows 11, `large-v3-turbo`
float16, faster-whisper 1.2.1 / CTranslate2 4.8.2 in SubGen's venv.

## What was ported

| SubGen (`pipeline.py`) | Hushcue |
|---|---|
| `run_job`, `_Tracker` | `Pipeline/JobRun.cs`, `Pipeline/JobProgress.cs` |
| `transcribe_file` | `FileTranscription.Transcribe` |
| `_decode_words` | `FileTranscription.DecodeWords`, VAD options in `Presets.Vad` |
| `recover_loops`, `read_wav_slice` | `FileTranscription.RecoverLoops`, `FileTranscription.Slice` |
| `load_model`, `_gpu_available`, `extract_audio`, `burn.embed_subtitles`, `burn.recycle` | delegates in `Pipeline/JobSteps.cs`, so tests replace them as SubGen's tests monkeypatch them |

**Loop recovery** (on by default, `JobSettings.RecoverLoops`, as in SubGen's `Settings`). Each loop that
`FindRepeatRuns` finds is padded by 2.0 s on each side, overlapping spans are merged, and each span is decoded once
more on its own with repetition penalty 1.3. The re-decoded words replace the words that start inside the span,
unless the re-decode found nothing. `CollapseRepeats` then caps whatever still loops. The span is cut from the
in-memory audio as `read_wav_slice` reads SubGen's 16-bit WAV: from sample `int(start × 16000)`,
`int((end − start) × 16000)` samples, cut short at the end. The in-memory samples go through 16-bit first, so they
are the WAV's values.

**VAD options.** SubGen's `_decode_words` runs voice activity detection when the preset has it (Fast) or "skip
silence" is on. Every run uses threshold = the preset's or 0.1, `min_silence_duration_ms` = the preset's or 1000, and
`speech_pad_ms` = 500. Everything else keeps faster-whisper's `VadOptions` defaults. Before this phase Hushcue used
faster-whisper's defaults for "skip silence" (0.5 / 2000 ms / 400 ms) and left out the 500 ms pad in Fast. Both are
fixed and checked by `TranscriptionTests.VadUsesSubGensOptions` and the end-to-end runs below. The re-decodes in loop
recovery use the same options, as in SubGen.

## Checks on the reference clips

The four 3-minute clips of `../subgen/reference/`. "Same features" means the C# engine decoded the log-mel features
faster-whisper computed in the same run. `tools/bench/fw_features.py` records each one under a hash of the audio it
was computed from: the whole file, the audio VAD kept, and every re-decoded loop. Because Hushcue looks features up by
the hash of its own audio, finding all of them also proves that Hushcue's audio, VAD-kept audio and loop slices are
bit-identical to SubGen's.

### VAD, raw words (`fw_reference.py` vs `Hushcue.Cli transcribe`, threshold 0.1, 1000 ms, pad 500 ms)

| Clip | Speech kept by VAD | Words | Same features: words + times identical | Own features: words reproduced |
|---|---|---|---|---|
| clip 1 | 168.3 of 180 s | 198 | yes | 54.5% |
| clip 2 | 10.2 of 180 s | 1 | yes | 100% |
| clip 3 | 106.7 of 180 s | 38 | yes | 97.4% |
| clip 4 | 45.8 of 180 s | 54 | yes | 90.7% |

### Finished subtitles, SubGen's `transcribe_file` vs Hushcue's job

| Mode | Same features: identical `.srt` | Own features: SubGen's words reproduced | Own features: WER vs reference, SubGen / Hushcue |
|---|---|---|---|
| Balanced, loop recovery on (the default) | **4 / 4** | 76.0% (333 / 438) | 55.1% / 49.1% |
| Balanced, loop recovery off | 4 / 4 | 79.9% (330 / 413) | 50.4% / 47.4% |
| Balanced + skip silence | 4 / 4 | 62.2% (186 / 299) | |
| Fast | 4 / 4 | 98.7% (234 / 237) | |

Both programs are deterministic. SubGen's job gave the same files in two runs and matched the finished output
recorded a day earlier (`bench-out/subgen-a`), and Hushcue's job also gave the same files twice.

### Why own features agree less in the finished subtitles

Same features in, same subtitles out, in every mode. So every difference comes from the features, which differ from
numpy's by at most one float32 step in 2% of the values (Phase 1b). At the level of raw words that costs little:

| Clip | Raw words (Balanced decode, no clean-up) | Reproduced | Finished subtitles, words reproduced |
|---|---|---|---|
| clip 1 | 267 | 255 (95.5%) | 93.6% |
| clip 2 | 249 | 249 (100%), timings up to 1.7 s apart | 30.0% |
| clip 3 | 192 | 188 (97.9%) | 39.0% |
| clip 4 | 235 | 229 (97.4%) | 62.9% |
| all | 943 | 921 (97.7%) | 76.0% |

The finished subtitles amplify these differences:

- The clean-up works on timings as well as text. On clip 2 every raw word matches, yet the timings move by up to
  1.7 s. That changes how loops group into cues and which of them `DropJunk` drops, and clip 2 is almost all loop
  (249 raw words, 30 finished).
- Loop recovery re-decodes spans that start and end at word times. When the times move, the re-decoded clip is
  different audio, and everything decoded in that span can change (clip 2: SubGen re-decodes 4 spans covering 126 s,
  Hushcue 2 spans covering 102 s).
- Whole segments are dropped on two thresholds (no-speech probability > 0.6 and average log-probability < −1.0). On
  these clips, where much of the audio is sighing and laughing, a borderline segment can fall on either side.

These differences are not errors on either side. The reference word error rate is no worse with Hushcue's features
(49.1% against 55.1%), and clip 1, the clip with the most ordinary speech, agrees on 93.6% of its finished words.
Matching numpy's features bit for bit would need its pocketfft float32 FFT and OpenBLAS's summation order. Even
SubGen does not get identical features on a different CPU.

## `run_job` behaviour

`test_run_job.py` and SubGen's `run_job` were compared step by step:

| | SubGen | Hushcue |
|---|---|---|
| Progress | per file: extract 0, model load 0.05, "Transcribing..." 0.08, 0.08–0.98 by segment end, "Writing SRT..." 0.99; with embedding, transcription takes 85% (soft) or 30% (burn) of each file; overall value is the mean over files; status lines of both stages joined | the same values and texts (the transcribing text adds speed and time left); separator `·` instead of `\|`; each file's state also reported to the queue |
| Embed overlap | file N embedded on a thread while file N+1 is transcribed | the same, with a `Channel`; the transcription stage runs on its own thread so it never holds up the embed stage |
| Embed fails (ffmpeg error) | logged, the temporary `.srt` rescued next to the video, the job carries on and raises at the end; the original is kept and the file is not reported done | the same; the file is marked Failed in the queue |
| Transcription fails | the job stops; embeds still queued are skipped, and with "keep .srt" off their transcripts are lost with the temporary folder | the job stops transcribing, **finishes the queued embeds**, then reports the error |
| Embed fails with an unexpected error | stops the job, but while files are left to transcribe it raises `Cancelled` instead of the error (the transcription loop sees the abort flag first) | stops the job and **reports the error** |
| Cancel | both stages stop; temporary files deleted | the same |
| GPU error | retried once on the CPU | the same (CTranslate2 errors whose message names CUDA, cuBLAS, cuDNN, a DLL or out of memory) |

The two bold rows are deliberate improvements and are documented in `JobRun.cs`.

## Tests

`tests/Hushcue.Core.Tests`, 161 in total (118 before this phase):

- `TranscriptionTests` (15): the 7 model tests left from `test_pipeline.py` with a fake model (loop recovery x4,
  `transcribe_file` with and without recovery, cancel, presets deterministic), plus SubGen's decoding and VAD options
  and the WAV slicing.
- `JobTests` (18): the 14 tests of `test_run_job.py` with a fake model and real ffmpeg. 12 are ported directly. The 2
  for the dropped Best preset are adapted: progress stays monotonic over several files, and a saved "Best" setting
  runs as Balanced on the plain audio. Hushcue adds: cancel, a failed transcription finishing the queued embed, GPU to
  CPU fallback, and the model download inside the job.
- `ModelDownloadTests` (9): a fake Hugging Face on `HttpListener`. It redirects `resolve/` URLs to a file store, honours
  byte ranges, and can break or stall a connection. Covered: progress, resume (the `Range` header survives the
  redirect), a server that ignores ranges, checksum and size failures, cancel, stalls, HTTP errors, leftover folders.
  The real multi-GB models are never downloaded. One bug was found and fixed: HttpClient's own timeout came out as an
  `OperationCanceledException`, which the app would have shown as "Cancelled". It is now a download error.
- `ModelPathTests` (1, needs the model): loads large-v3-turbo through junctions named `modèle` and `日本語 モデル`.
  CTranslate2 4.8.2 converts the UTF-8 path to UTF-16 before opening files (`src/filesystem.cc`), and the bridge
  passes UTF-8, so non-ASCII paths need no workaround. By hand: the same path in the ANSI code page fails, 8.3 short
  paths work.

## Reproduce

```bash
export SUBGEN_DIR=../subgen   # where SubGen is, if not next to this repository
PY=../subgen/.venv/Scripts/python
CLI=src/Hushcue.Cli/bin/Release/net10.0/win-x64/Hushcue.Cli.exe
dotnet build src/Hushcue.Cli -c Release

# finished subtitles: SubGen's real transcribe_file, recording every set of features it decodes
$PY tools/bench/subgen_job.py bench-out/subgen-job --features bench-out/features-job     # [--preset Fast] [--skip-silence] [--no-recover-loops]
$CLI job --out bench-out/hc-job --features bench-out/features-job ../subgen/reference/*.mp4   # same options
$CLI job --out bench-out/hc-job-own ../subgen/reference/*.mp4                                 # Hushcue's own features
$PY tools/bench/compare_srt.py bench-out/subgen-job bench-out/hc-job

# VAD, raw words
$PY tools/bench/fw_reference.py bench-out/fw-vad --vad-threshold 0.1 --vad-min-silence 1000 --vad-speech-pad 500 --features bench-out/features-vad
$CLI transcribe --model MODEL_DIR --out bench-out/cs-vad --features bench-out/features-vad --vad-threshold 0.1 --vad-min-silence 1000 --vad-speech-pad 500 ../subgen/reference/*.mp4
$PY tools/bench/compare_words.py bench-out/fw-vad bench-out/cs-vad
```
