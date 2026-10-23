# Hushcue — TODO

Accurate subtitles from any video, generated privately on your PC.

v0.1.0 is out: the CTranslate2 engine with a C# port of faster-whisper 1.2.1, the subtitle job (loop recovery,
clean-up, embedding), the app (Generate subtitles, Edit subtitles, MKV to MP4, Settings, About), the installer and
automatic updates. v0.1.1 adds the "not sure" review flags and fixes the vocabulary (CHANGELOG.md). How it got there
is in the git history; this file holds what's next.

## Open from v0.1.0

- [ ] Check the v0.1.0 release run on GitHub Actions and the published release (installer, zip, `SHA256SUMS.txt`)
- [ ] Make the repository public, so installed copies can see new releases (the update check is anonymous)
- [ ] Test the MKV to MP4 page by hand
- [ ] Code signing (SmartScreen warns on unsigned installers): certificate or Azure Trusted Signing
- [ ] Trimming / Native AOT: trimmed builds crash in XAML at start; needs a `JsonSerializerContext` for
      `AppSettings` and CsWinRT trimming support first (would save about 90 MB)
- [ ] Subtitle tracks inside an `.mkv`: extract to edit, write back as a new file
- [ ] Compact mini window that stays on top while a batch runs

## v0.1.1 — Subtitle quality: the right word, not just a similar one (released; the open items move to v0.2.0)

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
- [x] **"Thank you" over noise**: Balanced writes "Thank you" / "you" over pink noise, silence and plain tones
      (4–5 cues in 2 minutes, with or without a vocabulary); Thorough writes nothing. Fixed on the
      `subtitle-quality` branch: `Pipeline/UnheardCues` drops a cue of up to three words with no Silero window
      at 0.2 or above under it (2 minutes of noise, silence and tones: 2 cues → 0)
- [x] **Context across windows: tried, staying off.** `JobSettings.PreviousText` (off; `eval --previous-text`)
      lets each window see the text before it. Balanced: 345 → 350 scored words right, but WER 1.4% → 1.6%;
      Thorough: 349 → 349, WER 1.5% → 2.0%. The extra errors are invented text carried over from the context:
      "please write them in the comments", "feel free to ask", "If you have any questions" (Thorough), and on
      an earlier run "Thank you for watching. Please subscribe to the channel and press the bell icon" in a
      pause. The prompt reset that is meant to stop this never fires, because every preset decodes at
      temperature 0 only
- [x] **Punctuation lost after a pause**: without context, a window that starts after a pause can come out
      without capitals or full stops ("the help pages thank you all now, a few details", Balanced,
      team-names.pauses). Fixed on the `subtitle-quality` branch with a punctuated prompt in every window
      (`JobSettings.PunctuationPrompt`): eval WER 1.2% → 0.9%, scored words 348 → 356 of 372
- [ ] Put a space back into run-together sentences ("notes.Ashleigh"), which fast speech causes
- [ ] **Balanced vs Thorough on look-alike words**: large-v3-turbo has 4 decoder layers, large-v3 has 32. The
      decoder is the part that picks between words that sound the same. Measure the gap; if it is large, say so in
      the quality descriptions
- [x] **"Not sure" in the review**: words keep Whisper's probability (`Subtitles.Word.Probability`) through the
      job; `Review/LookAlikes` lists ~75 groups of words that sound alike; `Review/UnsureWords` flags a look-alike
      below 0.9 (below 0.5 for the most common words, "to / two", "one / won", where Whisper's doubt is mostly
      digits or not). The review shows a "not sure" flag ("Whisper wasn't sure of “come”: it could be “calm” or
      “cum”") and one-click swaps under the text, which clear the flag and undo with Ctrl+Z. The unsure words are
      kept per .srt in %LOCALAPPDATA%\Hushcue\unsure, so reopening a file later still flags them; each is tied to
      the words around it, so a second "come" in the same subtitle is not mistaken for it. Measured with
      `eval --confidence`: Balanced flags 28 of 559 look-alike words, 13 of them wrong, and catches 13 of the 14
      wrong ones; Thorough flags 35, catches 6 of 10. On ordinary speech (the README sample) no flags on
      Balanced, one on Thorough. A general "any low-probability word" flag is not worth it: of the words below
      0.5 only 1 in 10 is wrong
- [ ] Later: the alternatives the beam search actually considered (`NumHypotheses`) as swaps for any word
- [ ] **Spelling preferences in Settings**: "Words Whisper should never write" become suppressed tokens, so the
      decoder picks its next best word for the same sound in its context (better than find-and-replace, which
      can't tell "come" from "calm"). "Always write X as Y" for names and brands, applied after the clean-up
- [x] Release v0.1.1: version in `Directory.Build.props`, notes in `CHANGELOG.md` (the release workflow puts the
      version's section first in the GitHub release, so the update dialog shows it), tag `v0.1.1`
- [ ] Check the v0.1.1 release run on GitHub Actions, and that an installed v0.1.0 offers the update (needs the
      repository to be public)

## v0.2.0 — Subtitle quality, part 2: cues, timing and silence (released; the open items move on)

Research and measurements: `docs/subtitle-quality-research.md`. Done in 0.2.0: cues to Netflix's rules
(`Subtitles/Breaks`, reading speed, durations), short cues with no speech dropped and the phrase list checked
against the audio (`Pipeline/UnheardCues`), the punctuation prompt, word edges trimmed to speech
(`Pipeline/WordEdges`: phrase starts 0.38 s → 0.03 s off), and `Hushcue.Cli timing` to score word timing.

- [ ] **Pick the punctuation prompt and decide on chunking, on an NVIDIA GPU** (stopped on the Mac's CPU after the
      first of eight runs). `JobSettings.ChunkOnSilence` (off; `Pipeline/SpeechClips`) decodes only the speech, in
      clips of up to 30 s cut in silences, as WhisperX does. On speaking-styles it lost one whole sentence
      ("The train to Mumbai leaves…"): decoded from the start of its clip, the prompt made Whisper skip it;
      without the prompt, or with some other wordings, the clip decodes in full. So the prompt's wording and
      chunking must be chosen together. Run each of these (`--device cuda`, Balanced, then Thorough) and compare
      WER, scored words, phrases heard and start/end error:

      ```
      Hushcue.Cli eval [--chunk-on-silence] --punctuation-prompt TEXT
      Hushcue.Cli timing --reference eval/speaking-styles/speaking-styles.srt [--chunk-on-silence] --punctuation-prompt TEXT eval/speaking-styles/speaking-styles.mp4
      ```

      with TEXT each of "Hi, how are you? I'm fine, thanks. Well, see you later." (the default), "Hello, welcome.
      Today, we'll talk about the plan, and then we'll take questions.", "Hi, how are you? I'm fine, thanks.",
      and `--no-punctuation-prompt` in place of `--punctuation-prompt`. Make speaking-styles first with
      `python3 tools/bench/make_speaking_styles.py` (macOS). Done so far, default prompt without chunking (CPU):
      eval 0.9% WER, 356/372; timing 42 of 43 phrases, WER 8.6%, starts 0.029 s median, 95% within 0.1 s
- [ ] **Enhance off on Thorough**: Thorough always runs ffmpeg's `afftdn` + `dynaudnorm`; no source found that
      denoising helps Whisper. Measure `eval --preset Thorough` with and without it
- [ ] **Shot-change snapping** for video: snap cue edges within ~11 frames of a cut onto it (ffmpeg scene detection)
- [ ] **Real recordings** with careful human subtitles for `timing` and `eval`, and a SubER score
- [ ] **Spikes**: wav2vec2 forced alignment in ONNX Runtime (Apache-2.0 `wav2vec2-base-960h`), Parakeet TDT v3
