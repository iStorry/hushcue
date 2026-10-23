# Changelog

The release workflow puts a version's section at the top of its GitHub release notes, which the app's update dialog
shows: keep each section short and plain.

## 0.2.0 — 2026-10-23

- Subtitles read like professional ones: lines and subtitles break where the sentence does (after a comma, before
  "and"), not after "the" or between a first and last name, and each subtitle stays up long enough to read.
- Subtitles no longer appear before anyone speaks: word timing is trimmed to the speech itself (in the test video,
  subtitles now start within 0.1 s of the speech 95% of the time, up from 2%).
- Full stops and capitals no longer go missing in long stretches of speech, and fewer words are misheard (in the
  test set, 356 of 372 tricky words right, up from 348).
- "Thank you." and "you" no longer appear over noise, tones or silence; "Thanks for watching" is kept when someone
  really says it.
- The window fits any screen: it opens at the right size for your display scaling, can be made much smaller
  (down to 600×500), and every page rearranges itself for a narrow or short window instead of cutting things off.
- On a narrow window the options move under the file list, the review editor puts the subtitles under the video,
  and buttons wrap onto a second line; a window too short for a page scrolls it.
- The subtitle shown over the video in the review grows and shrinks with the picture.
- Fixed: long file names and counts running under other text, the timeline drawing past its edges, the options
  card stopping short of the right edge, the setup screen's download text overlapping, and the setup card sitting
  at the top of the window instead of in the middle.
- For developers: the app's CLI, tests and engine build on a Mac (CPU only), Windows and macOS builds of the
  engine use separate folders so one checkout can be shared, and a speaking-styles test video is made with macOS
  voices (`tools/bench/make_speaking_styles.py`); `Hushcue.Cli timing` scores word timing against it, and
  `docs/subtitle-quality-research.md` has the research behind this release.

## 0.1.1 — 2026-10-22

- Words that sound like other words (calm / come / cum, their / there, principle / principal, ...) are flagged
  "not sure" in the review when Whisper wasn't sure of them, with one-click buttons to swap in the other spellings.
- Fixed: the Vocabulary setting only reached the first 30 seconds of each file. Names and words in it are now
  spelled your way all the way through (in the test set, names right went from 15 to 31 of 38 on Balanced).
- For developers: `Hushcue.Cli eval` and a spoken test set (eval/lookalike) measure transcription quality per preset.

## 0.1.0 — 2026-10-22

- First release: Generate subtitles, Edit subtitles and MKV to MP4, an installer and automatic updates.
