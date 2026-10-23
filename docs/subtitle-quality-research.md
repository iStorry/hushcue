# Subtitle quality: research and plan

What makes automatic subtitles good, what the best open tools and papers do, and where Hushcue stands. Gathered
October 2026 from GitHub projects, papers, broadcaster style guides and the Open ASR Leaderboard. Each claim names its
source; anything not checked against a primary source is marked **unverified**.

Two things decide how good a subtitle file is:

1. **The words**: what was said, spelled right, without invented text. This is the model's job and the hardest to move.
2. **The cues**: where a subtitle starts and ends, how the text is broken into subtitles and lines, how long each stays
   on screen. Viewers notice these at least as much as a wrong word, and they are cheap to fix in code.

Hushcue already does much of (1) well. Most of the remaining gain is in (2).

## Where Hushcue stands (v0.1.1)

| Stage | What it does now | File |
|---|---|---|
| Audio | ffmpeg to 16 kHz mono; Thorough always adds `highpass, afftdn, dynaudnorm` ("Enhance") | `Media/Audio.cs` |
| Model | large-v3-turbo (Fast, Balanced), large-v3 (Thorough), CTranslate2 | `Pipeline/Presets.cs` |
| Windows | Whisper's own 30 s seek; VAD only on Fast or with "skip silence" | `Engine/Transcriber.cs` |
| Decoding | temperature 0 only, beam 5, no previous-text context, hotwords in every window | `Pipeline/FileTranscription.cs` |
| Hallucinations | `hallucination_silence_threshold` 2 s, a phrase list, loop re-decoding, repeat capping | `Subtitles/Cleanup.cs` |
| Word timing | Whisper cross-attention + DTW (faster-whisper's word timestamps) | `Engine/Transcriber.cs` |
| Cues | split on 0.8 s gap, 6 s, 84 chars or a sentence end; lines balanced by length; min 0.7 s, 20 ms gap | `Subtitles/Cleanup.cs` |

Decisions already measured and kept (TODO.md): temperature 0 only (fallback made identical runs disagree on ~38% of
words), previous-text context off (more invented text), vocabulary as hotwords (names 15/38 → 31/38).

## 1. Cues: broadcast rules

### Netflix English (USA) Timed Text Style Guide

Read in full ([English guide](https://partnerhelp.netflixstudios.com/hc/en-us/articles/217350977-English-USA-Timed-Text-Style-Guide),
[general requirements](https://partnerhelp.netflixstudios.com/hc/en-us/articles/215758617-Timed-Text-Style-Guide-General-Requirements)).

- **42 characters per line, at most two lines.** "Text should usually be kept to one line, unless it exceeds the
  character limitation."
- **Reading speed: up to 20 characters per second** (adults), 17 for children's programmes.
- **Duration: at least 5/6 s** (20 frames at 24 fps), **at most 7 s**.
- **Line breaks:** break after punctuation, before conjunctions, before prepositions. Never separate:
  - an article from its noun
  - an adjective from its noun
  - a first name from a last name
  - a subject pronoun from its verb
  - a prepositional verb from its preposition
  - a verb from its auxiliary, reflexive pronoun or negation
- **Shape:** "Prefer a bottom-heavy pyramid shape … but avoid having just one or two words on the top line."
- **Continuity:** no ellipsis when a sentence carries over into the next subtitle (pause under 2 s). Use "…" (U+2026)
  for a pause of 2 s or more. "--" marks an abrupt interruption.
- **Two speakers in one subtitle:** a hyphen without a space, one speaker per line. Each line must be a complete sentence.
- **Numbers:** zero to ten written out, 11 and up as numerals. Ages always numerals.
- **Other style rules:** "okay", not "OK"; no double spaces; no en or em dashes.
- **Gap and shot changes:** these rules moved into a separate "Subtitle Timing Guidelines" page, which returned 404.
  An older Netflix supplementals guide says a **2-frame** minimum gap.

### Shot changes

- A Google patent ([US10798271](https://patents.google.com/patent/US10798271)) says viewers re-read a subtitle that
  appears less than 12 frames before a cut, and that subtitles appearing or ending near a cut flicker.
- HappyScribe ([help](https://help.happyscribe.com/en/articles/9395512-shot-changes-in-subtitling)) snaps cue edges
  within **11 frames** of a cut onto the cut. A subtitle crosses a cut only if the dialogue does.
- Ooona has the same feature, with separate snap-before and snap-after windows.

### Line and cue breaking in the literature

- Breaking by character count alone is called out as the common failure. Good breaks follow syntax.
- Papers: SVM/CRF models trained on professional subtitles (Álvarez et al. 2014/2017); POS and dependency features
  from ASR output; an unsupervised method that uses a masked language model's punctuation probability (ACL 2023 short).
- **SubER** ([pypi](https://pypi.org/project/subtitle-edit-rate/0.1.0/)) is the standard automatic metric. It is an
  edit distance that counts text, cue breaks and line breaks, and works when the cues don't line up with the reference.

### What open tools do

- **stable-ts** ([repo](https://github.com/jianfch/stable-ts)) regroups words into cues by rules: split at sentence
  punctuation, split on gaps over 0.5 s, split at commas past a length, split long cues evenly (`split_by_length` with
  `even_split`), merge tiny pieces (**unverified**: the exact default string).
- **stable-ts** also moves word edges out of silence, read from source (`stabilization/__init__.py`, `nonvad.py`):
  - It marks silence from loudness (20 levels, 5-frame smoothing, sound runs ≤ 0.1 s count as silence) or from
    Silero (probability < 0.35).
  - A word that starts inside a silence gets its start moved to the silence's end.
  - A sentence-final word that runs into a silence gets its end pulled back to the silence's start.
  - A word never becomes shorter than `min_word_dur` (0.1 s in `transcribe_minimal`).
- **Faster-Whisper-XXL** (Purfview, the engine Subtitle Edit uses) has `--sentence`, `--standard`, `--max_comma`,
  `--max_gap`, `--max_line_width` and `--max_line_count`. The flags exist (changelog); their defaults are not
  documented anywhere I could read.

### Gaps in Hushcue

| Rule | Hushcue now | Industry |
|---|---|---|
| Line break | balance the two lengths | syntax-aware, bottom-heavy |
| Cue break when too long | wherever the limit is hit | at a clause boundary |
| Reading speed | not checked | ≤ 20 characters per second |
| Minimum duration | 0.7 s | 5/6 s |
| Maximum duration | 6 s | 7 s |
| Gap | 20 ms | 2 frames (~83 ms at 24 fps) |
| End of cue | end of the last word | held into the silence after it, so it can be read |
| Shot changes | ignored | edges snapped within ~11 frames |

## 2. Windows and voice activity detection

**WhisperX** ([paper](https://arxiv.org/html/2303.00747v2)) uses "VAD cut & merge":
- Find speech with a VAD.
- Cut long speech at the quietest point, then merge neighbouring pieces up to 30 s.
- Decode each chunk on its own, without previous text. This also allows batching.

Results (Table 2; Whisper large-v2):

| | TED-LIUM WER | Insertions | 5-gram repeats | Kincaid46 WER | Insertions | 5-gram repeats |
|---|---|---|---|---|---|---|
| Whisper (buffered) | 10.5 | 7.7 | 221 | 12.5 | 3.2 | 131 |
| WhisperX | 9.7 | 6.7 | 189 | 11.8 | 2.2 | 75 |

- Unbatched, WER is 10.52 on the full audio, 9.72 with 15 s merges and 9.70 with 30 s merges. The best merge length is
  30 s, the length Whisper was trained on.
- "Batched transcription without VAD chunking … degrades both transcription quality and word segmentation."
- Batched with VAD is about 12× faster "without performance loss".

**faster-whisper** ([repo](https://github.com/SYSTRAN/faster-whisper)):
- `BatchedInferencePipeline` turns on VAD by default (`min_silence_duration_ms=160`, `max_speech_duration_s=30`) and
  forces previous text off.
- It decodes at the first temperature only and ignores `hallucination_silence_threshold`.
- Community reports are mixed. Issue [#1179](https://github.com/SYSTRAN/faster-whisper/issues/1179): ~22 WER batched
  vs ~18 sequential on 150 Spanish broadcast clips, with missing segments. The maintainers say non-batched mode loses
  text too, less often.

**Faster-Whisper-XXL's VAD notes** ([#231](https://github.com/Purfview/whisper-standalone-win/discussions/231)):

| VAD | Author's verdict |
|---|---|
| Silero v4 | "most accurate Silero version" |
| Silero v5 | "Bad accuracy… Avoid!" |
| pyannote v3 | "The best accuracy" |

Hushcue ships Silero v6, which is not in that comparison.

**For Hushcue:** cut & merge can be built on `ClipTimestamps` (one clip per merged chunk). Keep a low threshold and
generous padding, so no audio is dropped and Silero's weakness with breathy speech matters less. Measure it against
sequential decoding with `eval` before turning it on. Batching is a speed-up that comes for free once chunks exist.

## 3. Hallucinations

- **Calm-Whisper** ([Interspeech 2025](https://arxiv.org/pdf/2505.12969)): 3 of the 20 decoder heads in large-v3 cause
  most hallucinations over non-speech. Fine-tuning only those heads on noise with empty labels cut non-speech
  hallucination by over 80%, with < 0.1% WER cost on LibriSpeech. No released weights found.
- **Bag of Hallucinations** (Barański et al., ICASSP 2025): a phrase-list filter. A 2026 re-implementation found it
  helps only modestly; a list can't catch phrases it doesn't contain.
- **Hallucination Space Projection** (arXiv 2609.04561) and **SAE steering** (arXiv 2606.07473): research methods that
  need model internals; not practical in CTranslate2.
- **large-v3 vs large-v2:** Deepgram (a competitor) reports v3 hallucinating ~4× more on its own set. Treat that as
  anecdotal. Hushcue's own eval shows Thorough (large-v3) writes nothing over noise, while Balanced (turbo) writes
  "Thank you".
- **Previous-text context** raises looping and invention: WhisperX, faster-whisper's docstring and Hushcue's own eval
  all agree. Keep it off.

**For Hushcue:** check every cue against the Silero speech probability under it. A short cue ("you", "Thank you")
with almost no speech under it is the classic non-speech hallucination: drop it. This is the open TODO item. The
probabilities are already computed when VAD runs; when it doesn't, Silero is small next to Whisper (cost to measure).

## 4. Word timing

**WhisperX Table 2**, word segmentation precision and recall (200 ms collar):

| | AMI | Switchboard |
|---|---|---|
| Whisper (DTW) | 78.9 / 52.1 | 85.4 / 62.8 |
| WhisperX (wav2vec2 alignment) | 84.1 / 60.3 | 93.2 / 65.4 |

- **wav2vec2 aligner caveats:** less robust to noise than Whisper, and one model per language.
  `facebook/wav2vec2-base-960h` (English) is Apache-2.0.
- **MMS-300M forced aligner** (`ctc-forced-aligner`): multilingual, with ONNX and CoreML ports, but **CC-BY-NC-4.0**.
  Not usable in an MIT app.
- **CrisperWhisper** ([repo](https://github.com/nyrahealth/CrisperWhisper)): a retokenised, fine-tuned Whisper with
  ~30 ms (read) / 41 ms (conversational) boundary error, verbatim mode. Listed as **CC-BY-NC-4.0** (**unverified**).
- **A 2026 preprint** (arXiv 2607.18934) reports WhisperX at 66 ms on read speech but 200 ms on disfluent speech, and
  argues attention-based timing can beat forced alignment there.
- **stable-ts silence snapping** (section 1) is the cheapest fix: no new model, just the VAD probabilities.

**For Hushcue:** snap word edges out of non-speech first. Forced alignment with wav2vec2 in ONNX Runtime is the bigger
step; keep DTW as the fallback when the alignment score is poor (noise, music).

## 5. Models

**Open ASR Leaderboard** ([blog](https://huggingface.co/blog/open-asr-leaderboard), paper arXiv 2510.06961).
Short-form English average WER:

| Model | WER | Decoder | Notes |
|---|---|---|---|
| IBM Granite Speech 4.1 2B | 5.33 | LLM | Apache-2.0; word timestamps in the "plus" variant |
| Cohere Transcribe 03-2026 | 5.42 | | Apache-2.0, gated; no timestamps |
| NVIDIA Canary-Qwen-2.5B | 5.63 | LLM | |
| NVIDIA Parakeet TDT 0.6B v2 | 6.05 | TDT | RTFx ~3400; v3 adds 25 European languages; word timestamps, punctuation |
| Distil-Whisper large-v3.5 | 7.21 | | worse than turbo on long-form (11.39 vs 10.25 OOD) |
| Whisper large-v3 | 7.44 | | |

- **Long-form track** (podcasts, meetings): "Among open models, OpenAI's Whisper Large v3 performs the best." Whisper
  large-v3 scores 6.43 against Parakeet CTC 1.1B's 6.68. Closed systems still lead there.
- So the newer models win on short clips; on long files Whisper is still the best open choice.
- None of them runs on CTranslate2. They would need ONNX Runtime, which Hushcue already ships for Silero.
- Only Parakeet and Granite "plus" give word timestamps.
- Parakeet is the one worth a spike: small, fast, timestamps built in. Check its ONNX export and sherpa-onnx support
  first (**unverified**).

## 6. Audio pre-processing

- **Vocal separation:**
  - Jam-ALT ([arXiv 2311.13987](https://arxiv.org/pdf/2311.13987)): HTDemucs separation generally made Whisper *worse*
    on lyrics (better only for large-v2 English), and sometimes made it pick the wrong language.
  - Syed et al. ([arXiv 2506.15514](https://arxiv.org/html/2506.15514v1)): separation helps consistently when used to
    find where vocals are (for segmentation), not as the audio Whisper hears.
  - Faster-Whisper-XXL's author rates MDX23 Kim vocal v2 "better than HT Demucs v4 FT". One user reports it 7× slower.
- **Denoising:** no source found that `afftdn`-style noise reduction helps Whisper. Whisper was trained on noisy audio.
  Thorough turns Enhance on by default, so measure it off.
- **For Hushcue:** don't add separation as Whisper's input. Consider it later as the VAD source for music-heavy videos.
  Measure Enhance off on Thorough.

## 7. Fixing words after decoding

- **LLM correction of N-best lists** ([arXiv 2307.04172](https://arxiv.org/html/2307.04172v1)): most of the gain came
  from picking Whisper's second-best hypothesis. That is the existing TODO item: offer `NumHypotheses` alternatives as
  review swaps.
- **Whispering LLaMA** (EMNLP 2023): 37.66% relative WER cut over the N-best oracle, with a fine-tuned LLaMA. Too heavy
  for now.
- **ROVER** (voting across systems): helps only when the systems make different errors ([arXiv 2409.09554](https://arxiv.org/pdf/2409.09554)
  reached 5.95% with ROVER, 4.72% with LLM correction). Whisper + Parakeet would be diverse enough; later.

## 8. Measuring

- `eval/lookalike` scores words only, on synthetic speech.
- `eval/speaking-styles` (made by `tools/bench/make_speaking_styles.py`) has exact cue times. That is enough for a
  first timing score: how far cue starts and ends are from the truth.
- Next: real recordings with human subtitles, scored with SubER and a timing error.

## Plan

In order. Each step is measured before it is kept.

1. **Cue building to broadcast rules** (`Subtitles/Cleanup.cs`):
   - syntax-aware cue and line breaks
   - 20 characters per second reading speed, by holding cues into the following silence
   - 5/6 s minimum, 7 s maximum, 2-frame gap
2. **No-speech cues**: drop a short cue when Silero hears no speech under it.
3. **Word edges out of silence**: stable-ts style, using the same probabilities.
4. **VAD cut & merge** through `ClipTimestamps`; compare with sequential decoding in `eval`.
5. **Enhance off on Thorough**, if eval agrees.
6. **Shot-change snapping** for video (ffmpeg scene detection).
7. **Timing score in eval** against `speaking-styles`; then real recordings and SubER.
8. **Spikes**: wav2vec2 forced alignment (ONNX); Parakeet TDT v3 (ONNX); N-best swaps in the review.

Not doing: temperature fallback, previous-text context (both measured worse here), MMS or CrisperWhisper (licences),
vocal separation as Whisper's input (evidence against).

## Progress on the `subtitle-quality` branch

### Step 1 (done): cues to broadcast rules

- `Subtitles/Breaks.cs` rates each place between two words as a break.
- `WrapLines` and `BuildCues` use those ratings.
- `FixTiming` holds cues until they are readable at 20 characters per second, with Netflix's durations and gap.

What changed on `eval/speaking-styles` (Balanced, CPU):

| Before | After |
|---|---|
| "I'd fancy a cup of tea and a / biscuit, if it's no bother." | "I'd fancy a cup of tea / and a biscuit, if it's no bother." |
| "Back in my day, a loaf / of bread cost a penny." | "Back in my day, / a loaf of bread cost a penny" |
| "Watch out. The car is / coming. Get off the road now." | "Watch out. The car is coming. / Get off the road now." |

Short cues ("It's adorable.") now stay up 5/6 s instead of 0.7 s. Long ones are held into the pause after them.

**Found while testing:** in long stretches without pauses, Balanced often writes no full stops or capitals ("until the
petrol station When I was young,"). The cue builder can only find sentences that are punctuated. Two fixes:
- a capital after an unpunctuated lower-case word now counts as a likely sentence start when choosing breaks
- the real fix is at the decoder: a punctuated style prompt (step 3)

### Step 2 (done): short cues with no speech under them

`Pipeline/UnheardCues.cs` drops a cue of up to three words when no Silero window under it reaches 0.2.

| Audio (Balanced) | Before | After |
|---|---|---|
| 2 min of pink noise, silence, 440 Hz and 1 kHz tones | 2 × "Thank you." | nothing |
| `speaking-styles` (3:38, 43 phrases, includes 12 s of noise) | 49 cues | nothing real dropped |

The job's log names each dropped cue, so a wrong drop is easy to spot.

### Step 3 (done): punctuation prompt

`JobSettings.PunctuationPrompt` (on) puts "Hi, how are you? I'm fine, thanks. Well, see you later." in front of the
vocabulary as hotwords, so every window sees punctuated text. Whisper copies the style of its prompt.

On `speaking-styles` (Balanced, CPU, 488 reference words):

| | Sentence ends (ref 61) | Commas (ref 35) | WER |
|---|---|---|---|
| Without | 49 | 20 | 8.8% |
| With | 65 | 32 | 10.0% |

- The prompt never appeared in the output.
- Of the WER difference:
  - one word is misheard (peck → pack)
  - one word is doubled ("Congratulations. Congratulations.")
  - four words are a cleanup bug the better punctuation exposed: the spoken "Thank you for listening." now stands as
    its own cue, and `DropJunk`'s phrase list removed it (fixed in step 2b)

On `eval/lookalike` (Balanced, CPU, all four variants, 2592 words, 372 scored):

| | WER | Scored words right |
|---|---|---|
| Without | 1.2% | 348/372 |
| With | **0.9%** | **356/372** |

Names gain most (Jaxon, Ashleigh, Kathryn), probably because the vocabulary now follows a sentence instead of
standing alone. `eval --no-punctuation-prompt` compares the two.

### Step 2b (done): the phrase list checks the audio

- `DropJunk` no longer removes "Thanks for watching." and the rest of the phrase list blindly. `UnheardCues` drops
  such a cue only when the mean speech probability under it is below 0.5. People do say these phrases (YouTube
  outros, the end of talks); Whisper's invented ones sit over music or silence.
- Silero runs once per file before decoding, so the live transcript and the saved file agree.
- "Re-run this part" goes through the same check.

### Measuring timing: `Hushcue.Cli timing`

- `tools/bench/make_speaking_styles.py` now times each phrase from the first to the last sample of its voice (within
  35 dB of its peak, before noise is added). Before, a phrase's time was the whole clip.
- `Hushcue.Cli timing --reference SRT MEDIA` runs the job and scores each phrase:
  - start error: its first word's start against the reference
  - end error: its last word's end against the reference
  - the share of phrases within 0.1 s and 0.25 s
  - the word error rate and the phrases furthest off
- The scorer is `Evaluation/TimingScoring.cs`. It works with any subtitles whose times are exact, so real recordings
  with careful human subtitles can be added later.

### Step 4 (done): word edges out of silence

`Pipeline/WordEdges.cs`, as stable-ts does:
- Each word's start moves forward to the first Silero window inside it at 0.35 or above, and its end back to the
  last such window.
- One window (32 ms) is kept to spare, and a word is never shorter than 0.1 s.
- A word with no speech under it is left alone.

On `speaking-styles` (Balanced):

| | Start error, median | Starts within 0.1 s | End error, median |
|---|---|---|---|
| Before | 0.38 s (0.35 s early on average) | 2% | 0.15 s |
| After | **0.03 s** | **95%** | 0.15 s |

- Words and WER are unchanged.
- The early starts were Whisper's timing stretching the first word after a pause back over the silence, which made
  subtitles appear before anyone spoke.
- The two phrases still early are whispered: Silero hears no speech in them, so their words are left as they are.
- Ends are about 0.16 s early. Trimming can only shrink a word, so it cannot fix them, and the reading-speed hold
  already covers cue ends.

### Step 5 (built, off, to measure on a GPU): VAD cut & merge

`JobSettings.ChunkOnSilence` (off) decodes only the speech, through faster-whisper's clip_timestamps.
`Pipeline/SpeechClips.cs` builds the clips:
- Silero finds speech at threshold 0.1, the same generous level as "skip silence".
- Neighbouring speech is merged into clips of up to 30 s.
- A silence of 2 s or more between clips is skipped.
- Clips closer together than that touch, so nothing between them is lost. The cut is at the first of the quietest
  windows in the second before the next speech.
- A clip starts up to 1 s before its speech.

**Found:** on `speaking-styles`, one clip lost its whole first sentence ("The train to Mumbai leaves…"):

| Clip decoded | Result |
|---|---|
| With the default prompt | sentence skipped |
| With no prompt | complete |
| With "Hi, how are you? I'm fine, thanks." | complete |
| With "Hello, welcome. Today, we'll talk about the plan, …" | complete |
| With "Okay, so, here we go. Yes, it's ready. Is it? Good." | sentence skipped |

The prompt's wording and chunking therefore have to be chosen together. The comparison (three wordings and no prompt,
each with and without chunking, on `eval` and `timing`) is in TODO.md, to run on an NVIDIA GPU. On the Mac's CPU only
the first configuration finished: the default prompt without chunking, which matches the numbers above.

### Next

- Steps 5 and 6 (Enhance off on Thorough), measured on a GPU: see TODO.md, v0.2.0.
