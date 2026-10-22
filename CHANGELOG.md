# Changelog

The release workflow puts a version's section at the top of its GitHub release notes, which the app's update dialog
shows: keep each section short and plain.

## 0.1.1 — 2026-10-22

- Words that sound like other words (calm / come / cum, their / there, principle / principal, ...) are flagged
  "not sure" in the review when Whisper wasn't sure of them, with one-click buttons to swap in the other spellings.
- Fixed: the Vocabulary setting only reached the first 30 seconds of each file. Names and words in it are now
  spelled your way all the way through (in the test set, names right went from 15 to 31 of 38 on Balanced).
- For developers: `Hushcue.Cli eval` and a spoken test set (eval/lookalike) measure transcription quality per preset.

## 0.1.0 — 2026-10-22

- First release: Generate subtitles, Edit subtitles and MKV to MP4, an installer and automatic updates.
