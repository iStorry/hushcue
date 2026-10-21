# Hushcue

Accurate subtitles from any video, generated privately on your PC.

Hushcue turns the speech in your videos into subtitles with Whisper, running entirely on your own machine.
It can save an `.srt` file, add a subtitle track to an `.mkv`, or burn the text into an `.mp4`.

> Work in progress: a C# + WinUI 3 rewrite of SubGen. See [TODO.md](TODO.md) for the plan.
> Engine: CTranslate2, the engine SubGen uses, called natively through a small C++ bridge
> ([Phase 1b](docs/phase1b-ctranslate2.md)). Whisper.net was tried first and dropped ([Phase 1](docs/phase1-engine-spike.md)).

## Layout

| Project | What it is |
|---|---|
| `src/Hushcue.Core` | Settings, subtitle cleanup, SRT, pipeline, ffmpeg and engine wrappers. No UI. |
| `src/Hushcue.Cli` | Console front end, used for benchmarks and scripting. |
| `native/hushcue_ct2` | C interface over CTranslate2's Whisper model, built by `native/build.ps1`. |
| `tools/bench` | Benchmark scripts comparing Hushcue with SubGen on the reference clips. |
| `src/Hushcue.App` | The WinUI 3 desktop app. |
| `tests/Hushcue.Core.Tests` | xUnit tests for Core. |

## Build

Needs the .NET 10 SDK (pinned in `global.json`) on Windows 10 1809 or later, x64. The engine bridge also needs
Visual Studio 2022 Build Tools (C++ workload), CMake, and a Python environment with the `ctranslate2==4.8.2`,
`nvidia-cublas-cu12` and `nvidia-cudnn-cu12` wheels (SubGen's venv by default).

```bash
powershell -ExecutionPolicy Bypass -File native/build.ps1
dotnet build Hushcue.slnx
dotnet test Hushcue.slnx
dotnet run --project src/Hushcue.App
```
