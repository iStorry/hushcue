# UI mockups

Concepts for the Hushcue UI redesign, tracked in [Phase 6b of the TODO](../../TODO.md). They are drawn in a neutral
style (`mocks.css`, light and dark); the app itself uses WinUI Fluent styles.

| | Mock | What it shows |
|---|---|---|
| 1 | [main window](1-main-window-queue.html) ([png](1-main-window-queue.png)) | Progress for each file in the queue, preset switch, result cards, advanced options folded away |
| 2 | [review editor](2-review-editor.html) ([png](2-review-editor.png)) | Video preview, waveform with draggable cues, editable cue list with flagged hallucinations and gaps |
| 3 | [first run and done](3-first-run-and-done.html) ([png](3-first-run-and-done.png)) | GPU and ffmpeg checks with the model download; totals, warnings and Undo after a batch |

The numbers and file names in them are made up.

To redraw a PNG after editing its HTML (macOS, Google Chrome):

```bash
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless=new --hide-scrollbars --force-device-scale-factor=2 --window-size=800,488 --virtual-time-budget=4000 --screenshot="$PWD/1-main-window-queue.png" "file://$PWD/1-main-window-queue.html"
```

Window heights used: 488 (mock 1), 542 (mock 2), 382 (mock 3).
