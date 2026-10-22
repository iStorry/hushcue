# Phase 7: Hushcue against SubGen

Date: 2026-10-22. RTX 4070, Windows 11, Balanced preset (`large-v3-turbo`, float16, loop recovery on), the four
3-minute reference clips (12 minutes of audio). Both programs ran their full job: read the audio, load the model,
transcribe, clean up, write the .srt.

## Speed

Wall time for all four clips, alternating the two programs, two rounds each. Another application kept the GPU at
about 40% load throughout, so absolute speeds are lower than on an idle GPU; both programs ran under the same load.

| Round | SubGen (`subgen_job.py`) | Hushcue (`Hushcue.Cli job`) |
|---|---|---|
| 1 | 28.9 s | 26.4 s |
| 2 | 28.8 s | 26.2 s |

Hushcue is about 9% faster end to end (same engine; less start-up and Python overhead).

## Accuracy

Word error rate of the finished subtitles against the hand-made references (`tools/bench/compare_srt.py`):

| Clip | SubGen | Hushcue |
|---|---|---|
| clip 1 | 37.4% | 32.8% |
| clip 2 | 136.8% | 78.9% |
| clip 3 | 84.8% | 76.1% |
| clip 4 | 54.0% | 55.6% |
| **all** | **55.1%** | **49.1%** |

Both programs give the same output on every repeat run. Hushcue reproduces 76% of SubGen's finished words: given the
same log-mel features its job writes SubGen's SRTs byte for byte ([Phase 4](phase4-pipeline.md)), so the differences
come from Hushcue computing features within one float32 step of numpy's, which shifts some word timings and with them
the clean-up's cue grouping and loop re-decoding. The references are drafts, not fully corrected transcripts, so the
absolute numbers are high; the comparison between the two programs on the same references holds.

## Reproduce

```bash
dotnet build src/Hushcue.Cli -c Release
../subgen/.venv/Scripts/python tools/bench/subgen_job.py bench-out/p7-subgen
src/Hushcue.Cli/bin/Release/net10.0/win-x64/Hushcue.Cli.exe job --out bench-out/p7-hushcue ../subgen/reference/*.mp4
../subgen/.venv/Scripts/python tools/bench/compare_srt.py bench-out/p7-subgen bench-out/p7-hushcue
```
