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

- [x] **Test set for words that sound alike** (`eval/lookalike`): four passages of 55–65 s (so later 30 s windows
      are tested too) with 93 scored words: calm / come / cum (incl. "summa cum laude"), their / there / they're,
      your / you're, its / it's, then / than, brake / break, site / sight, principal / principle, ... and six
      names with a vocabulary. Spoken by Windows' David and Zira voices (`tools/bench/make_lookalike.ps1`), clean
      and with pink noise, 1.7 MB of Opus. `Hushcue.Cli eval [--preset NAME]...` runs the app's job on them and
      prints the word error rate and the scored words right, with each miss ("cum → come (0:54)")
- [x] **Baseline** (v0.1.0): Balanced 2.5% WER, 156/186 scored words; Thorough 2.9%, 155/186. Look-alike words
      are mostly right on this clean TTS speech (Balanced misses cum → come, brakes → breaks, principle →
      principal, site → side; Thorough only principle and sight). The names are the big miss: right in the first
      ~25 s, then Jackson, Ashley, Catherine, Michaela, "Hushq" (Balanced 15/38, Thorough 10/38)
- [x] **Harder audio**: each passage now also comes as `.hard` (faster speech, loud pink noise, telephone
      bandwidth) and `.pauses` (12 s of noise after every third sentence, where Whisper tends to invent text).
      Whisper copes: with the vocabulary fix, Balanced is 1.4% WER and 345/372 scored words over all four
      variants, Thorough 1.5% and 349/372. The look-alike misses are the same few words in every variant
      (cum → come in "a gallery cum reading room", brakes → breaks, principle → principal, site → side,
      sight → site), so they come from the language model, not the audio: the review swaps and spelling
      preferences below are the fix for them. Still worth adding: real recordings (with permission)
- [x] **Fixed: the vocabulary only reached the first 30 seconds.** It went in as Whisper's initial prompt, and with
      `ConditionOnPreviousText = false` the prompt is dropped after the first window (`Transcriber.cs`,
      `promptResetSince`). Now it goes in as hotwords, which go into every window. Names: Balanced 15/38 → 31/38,
      Thorough 10/38 → 30/38; total WER Balanced 2.5% → 1.3%, Thorough 2.9% → 1.3%; nothing else changed. Still
      missed: Jaxon → Jackson (Balanced), Kathryn → Catherine (Thorough). Checked on 2 minutes of noise, silence
      and tones: the names never appear there
- [ ] **"Thank you" over noise**: Balanced writes "Thank you" / "you" over pink noise, silence and plain tones
      (4–5 cues in 2 minutes, with or without a vocabulary); Thorough writes nothing. Drop a lone "Thank you" /
      "you" cue when the audio under it has no speech (VAD probability), or flag it in the review
- [x] **Context across windows: tried, staying off.** `JobSettings.PreviousText` (off; `eval --previous-text`)
      lets each window see the text before it. Balanced: 345 → 350 scored words right, but WER 1.4% → 1.6%;
      Thorough: 349 → 349, WER 1.5% → 2.0%. The extra errors are invented text carried over from the context:
      "please write them in the comments", "feel free to ask", "If you have any questions" (Thorough), and on
      an earlier run "Thank you for watching. Please subscribe to the channel and press the bell icon" in a
      pause. The prompt reset that is meant to stop this never fires, because every preset decodes at
      temperature 0 only
- [ ] **Punctuation lost after a pause**: without context, a window that starts after a pause can come out
      without capitals or full stops ("the help pages thank you all now, a few details", Balanced,
      team-names.pauses). Try a short punctuated style prompt in every window (with the hotwords), measured
      with eval; also put a space back into run-together sentences ("notes.Ashleigh"), which fast speech causes
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
