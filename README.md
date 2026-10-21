# Hushcue

Accurate subtitles from any video, generated privately on your PC.

Hushcue turns the speech in your videos into subtitles with Whisper, running entirely on your own machine.
It can save an `.srt` file, add a subtitle track to an `.mkv`, or burn the text into an `.mp4`.

> Work in progress: a C# + WinUI 3 rewrite of SubGen. See [TODO.md](TODO.md) for the plan.

## Layout

| Project | What it is |
|---|---|
| `src/Hushcue.Core` | Settings, subtitle cleanup, SRT, pipeline, ffmpeg and engine wrappers. No UI. |
| `src/Hushcue.Cli` | Console front end, used for benchmarks and scripting. |
| `src/Hushcue.App` | The WinUI 3 desktop app. |
| `tests/Hushcue.Core.Tests` | xUnit tests for Core. |

## Build

Needs the .NET 10 SDK (pinned in `global.json`) on Windows 10 1809 or later, x64.

```bash
dotnet build Hushcue.slnx
dotnet test Hushcue.slnx
dotnet run --project src/Hushcue.App
```
