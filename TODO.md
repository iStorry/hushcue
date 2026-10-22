# Hushcue — TODO

Accurate subtitles from any video, generated privately on your PC.

v0.1.0 is out: the CTranslate2 engine with a C# port of faster-whisper 1.2.1, the subtitle job (loop recovery,
clean-up, embedding), the app (Generate subtitles, Edit subtitles, MKV to MP4, Settings, About), the installer and
automatic updates. How it got there is in the git history; this file holds what's next.

## Open from v0.1.0

- [ ] Check the v0.1.0 release run on GitHub Actions and the published release (installer, zip, `SHA256SUMS.txt`)
- [ ] Make the repository public, so installed copies can see new releases (the update check is anonymous)
- [ ] Test the MKV to MP4 page by hand
- [ ] Code signing (SmartScreen warns on unsigned installers): certificate or Azure Trusted Signing
- [ ] Trimming / Native AOT: trimmed builds crash in XAML at start; needs a `JsonSerializerContext` for
      `AppSettings` and CsWinRT trimming support first (would save about 90 MB)
- [ ] Subtitle tracks inside an `.mkv`: extract to edit, write back as a new file
- [ ] Compact mini window that stays on top while a batch runs

## v0.1.1 — Subtitle quality: the right word, not just a similar one

Whisper's misses are mostly words that sound alike: calm / come / cum, their / there, your / you're, then / than,
knew / new, weather / whether, here / hear. "come" and "cum" sound the same, so only the words around them can
decide; "calm" differs in its vowel, which many accents blur. So the work is about giving the decoder more context,
a stronger language model, and the person reviewing a fast way to fix what is still wrong. Measure first, then change.

- [ ] **Test set for words that sound alike** (`tools/bench`): short spoken sentences where the context decides
      ("Stay calm and come inside", "Come on, calm down", "Their dog is over there", ...), neutral TTS like the
      README sample, with exact reference text, committed. A CLI command `Hushcue.Cli eval` prints the word error
      rate and the share of look-alike words right, per preset, so every change below is measured
- [ ] **Bug: the vocabulary only reaches the first 30 seconds.** It goes in as Whisper's initial prompt, and with
      `ConditionOnPreviousText = false` the prompt is dropped after the first window (`Transcriber.cs`,
      `promptResetSince`). Pass it as hotwords instead, which go into every window
- [ ] **Context across windows**: try `ConditionOnPreviousText = true` (each 30 s window sees the text before it).
      It was off to stop loops spreading; the loop recovery and prompt reset on a fallback may now cover that.
      Switch only if the test set gets better and no loops appear
- [ ] **Balanced vs Thorough on look-alike words**: large-v3-turbo has 4 decoder layers, large-v3 has 32. The
      decoder is the part that picks between words that sound the same. Measure the gap; if it is large, say so in
      the quality descriptions
- [ ] **"Not sure" in the review**: keep each word's probability (the engine has it; `Subtitles.Word` drops it),
      flag words below a threshold, and for words in a look-alike list offer the others as one-click swaps
      (calm / come / cum). Later: the alternatives the beam search actually considered (`NumHypotheses`)
- [ ] **Spelling preferences in Settings**: "Words Whisper should never write" become suppressed tokens, so the
      decoder picks its next best word for the same sound in its context (better than find-and-replace, which
      can't tell "come" from "calm"). "Always write X as Y" for names and brands, applied after the clean-up
- [ ] Release v0.1.1: version in `Directory.Build.props`, release notes with the measured gains, tag `v0.1.1`
