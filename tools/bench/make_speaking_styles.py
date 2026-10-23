"""Speak English phrases in many styles with macOS' built-in voices and write a test video of them, the macOS
counterpart of make_lookalike.ps1:

    python3 tools/bench/make_speaking_styles.py

  eval/speaking-styles/speaking-styles.mp4   each phrase on a title card naming its style, voice and speed, with 1 s
                                             of silence after it, and 12 s of noise with no speech partway through
  eval/speaking-styles/speaking-styles.srt   the spoken words and their exact times, to compare subtitles against
  eval/speaking-styles/speaking-styles.json  the same cues with each one's style, voice, rate, filter and noise

Styles: speeds from very slow to very fast, whisper to shouting, high and low pitch, five accents, elderly, child-like,
robotic and sung voices, numbers, dates, acronyms, homophones, fillers, telephone bandwidth, background noise, echo,
and a quick two-voice dialogue. Needs ffmpeg on PATH and Pillow. The output is not committed.
"""
from __future__ import annotations

import json
import subprocess
import tempfile
import textwrap
import wave
from dataclasses import asdict, dataclass
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "eval" / "speaking-styles"
NAME = "speaking-styles"
SAMPLE_RATE = 48000
GAP_SECONDS = 1.0

VIDEO_SIZE = (1280, 720)
FONT_PATH = "/System/Library/Fonts/Helvetica.ttc"
WRAP_CHARS = 44
BACKGROUND = (18, 20, 28)
LABEL_COLOR = (140, 200, 255)
TEXT_COLOR = (240, 240, 240)
NOTE_COLOR = (150, 150, 160)


def pitch(factor: float) -> str:
    """Shifts the pitch by factor and keeps the length."""
    return f"asetrate={SAMPLE_RATE}*{factor},aresample={SAMPLE_RATE},atempo={1 / factor:.4f}"


TELEPHONE = "highpass=f=300,lowpass=f=3400"
QUIET_WHISPER = "volume=-12dB"
SOFT = "volume=-18dB"
SHOUT = "volume=14dB,alimiter=limit=0.95"
ECHO = "aecho=0.8:0.7:120|260:0.45|0.3"
CHORUS = "chorus=0.6:0.9:50|60:0.4|0.32:0.25|0.4:2|1.3"


@dataclass(frozen=True)
class Speech:
    style: str
    voice: str
    rate: int
    text: str
    audio_filter: str = ""
    noise: float = 0.0


@dataclass(frozen=True)
class Cue:
    start: float
    end: float
    speech: Speech


@dataclass(frozen=True)
class NoSpeech:
    style: str
    seconds: float
    noise: float


SCRIPT: list[Speech | NoSpeech] = [
    Speech("Normal speed", "Samantha", 175, "Good morning! Thanks for joining us today. Let's get started."),
    Speech("Slow", "Samantha", 100, "Please read the instructions carefully before you begin the test."),
    Speech("Very slow", "Daniel", 70, "The quick brown fox jumps over the lazy dog."),
    Speech("Medium, question", "Fred", 165, "Where did you put the keys? Were they on the kitchen table?"),
    Speech("Fast", "Samantha", 230, "I need you to call the office, cancel the meeting, and book a table for seven."),
    Speech("Very fast", "Daniel", 300,
           "Honestly, I can't believe how quickly the whole weekend went by, it's already Monday again."),
    Speech("Whisper", "Whisper", 150, "Don't make a sound. The baby is finally asleep."),
    Speech("Quiet whisper", "Whisper", 140, "Can you hear me? I'm right behind the door.", QUIET_WHISPER),
    Speech("Soft / quiet voice", "Samantha", 160, "It's okay. Take a deep breath, everything will be fine.", SOFT),
    Speech("Loud / shouting", "Ralph", 190, "Watch out! The car is coming! Get off the road now!", SHOUT),
    Speech("High pitch", "Samantha", 175, "Oh my goodness, look at that tiny puppy, it's adorable!", pitch(1.45)),
    Speech("Very high pitch", "Junior", 170, "Can we go to the park after lunch, please?", pitch(1.7)),
    Speech("Low pitch", "Fred", 160, "The storm will reach the coast sometime after midnight.", pitch(0.75)),
    Speech("Very low pitch", "Ralph", 150, "Welcome to the annual meeting of the board of directors.", pitch(0.6)),
    Speech("British accent", "Daniel", 175, "I'd fancy a cup of tea and a biscuit, if it's no bother."),
    Speech("Australian accent", "Karen", 175, "We're heading down to the beach this arvo, want to come along?"),
    Speech("Irish accent", "Moira", 175, "It's a grand day for a walk along the cliffs, isn't it?"),
    Speech("Indian accent", "Rishi", 175, "The train to Mumbai leaves at half past nine from platform four."),
    Speech("South African accent", "Tessa", 175, "Turn left at the robot, then keep going until the petrol station."),
    Speech("Elderly voice", "Grandma (English (US))", 140,
           "When I was young, we walked two miles to school every day."),
    Speech("Elderly voice, male", "Grandpa (English (UK))", 140, "Back in my day, a loaf of bread cost a penny."),
    Speech("Child-like voice", "Junior", 180, "I lost my tooth today and the tooth fairy is coming tonight!"),
    Speech("Numbers and money", "Samantha", 170,
           "Your total is forty-two dollars and fifteen cents, and the order number is 7 3 9 1 5."),
    Speech("Dates and times", "Daniel", 170, "The flight departs on March 3rd, 2027 at 6:45 in the morning."),
    Speech("Spelling and acronyms", "Samantha", 160,
           "My email is J A T at example dot com. The NASA and FBI reports are due by Friday."),
    Speech("Tongue twister, fast", "Fred", 250,
           "She sells seashells by the seashore, and the shells she sells are surely seashells."),
    Speech("Tongue twister, slow", "Fred", 110, "Peter Piper picked a peck of pickled peppers."),
    Speech("Homophones", "Samantha", 160, "Their dog went over there, and they're going to buy two more, too."),
    Speech("Telephone quality", "Daniel", 175, "Hello? Sorry, you're breaking up. Can you repeat the address?",
           TELEPHONE),
    Speech("Background noise", "Samantha", 175,
           "We're standing outside the stadium, and the crowd is getting louder.", noise=0.08),
    Speech("Heavy background noise", "Fred", 175, "I'll meet you at the north entrance in ten minutes.", noise=0.2),
    Speech("Echo / large room", "Daniel", 160, "Welcome, everyone, to the great hall of the museum.", ECHO),
    Speech("Chorus / muffled", "Samantha", 165, "This message is being recorded for training purposes.", CHORUS),
    NoSpeech("Long pause: background noise only, no speech", 12.0, 0.03),
    Speech("Robotic voice", "Zarvox", 160, "System check complete. All engines are running normally."),
    Speech("Sung", "Good News", 160, "Happy days are here again, the skies above are clear again."),
    Speech("Exclamations", "Samantha", 190, "Wow! No way! That's incredible! Congratulations!"),
    Speech("Hesitation and fillers", "Fred", 140,
           "Um, well, I mean, uh, I guess we could, you know, try again tomorrow."),
    Speech("Long sentence", "Samantha", 185,
           "After the meeting ended, the team gathered their notes, walked across the street to the small cafe on "
           "the corner, and spent another hour arguing about whether the new design was actually any better than "
           "the old one."),
    Speech("Quick dialogue, speaker A", "Samantha", 190, "Are you coming tonight?"),
    Speech("Quick dialogue, speaker B", "Daniel", 190, "I can't, I have to work late."),
    Speech("Quick dialogue, speaker A", "Samantha", 190, "That's a shame. Maybe next week, then?"),
    Speech("Quick dialogue, speaker B", "Daniel", 190, "Definitely. Next week works."),
    Speech("Normal speed, closing", "Samantha", 175, "That's the end of the test. Thank you for listening."),
]


def run(*args: str) -> None:
    subprocess.run(args, check=True, capture_output=True)


def to_wav(out: Path) -> list[str]:
    return ["-ac", "1", "-ar", str(SAMPLE_RATE), "-sample_fmt", "s16", str(out)]


def render_speech(item: Speech, out: Path) -> None:
    spoken = out.with_suffix(".aiff")
    run("say", "-v", item.voice, "-r", str(item.rate), "-o", str(spoken), item.text)
    shape = ",".join(filter(None, [f"aresample={SAMPLE_RATE}", item.audio_filter]))
    if not item.noise:
        run("ffmpeg", "-y", "-i", str(spoken), "-af", shape, *to_wav(out))
        return
    graph = (f"[0:a]{shape},apad=pad_dur=0.3[s];anoisesrc=c=pink:r={SAMPLE_RATE}:a={item.noise}[n];"
             "[s][n]amix=inputs=2:duration=first:normalize=0[a]")
    run("ffmpeg", "-y", "-i", str(spoken), "-filter_complex", graph, "-map", "[a]", *to_wav(out))


def render_no_speech(item: NoSpeech, out: Path) -> None:
    run("ffmpeg", "-y", "-f", "lavfi", "-i", f"anoisesrc=c=pink:r={SAMPLE_RATE}:a={item.noise}:d={item.seconds}",
        *to_wav(out))


def card_lines(item: Speech | NoSpeech) -> tuple[str, str]:
    """The card's main text and its small print."""
    if isinstance(item, NoSpeech):
        note = f"{item.seconds:.0f} s pink noise, amplitude {item.noise}"
        return "(no speech: subtitles should stay empty here)", note
    details = [f"voice: {item.voice}", f"rate: {item.rate} wpm"]
    if item.audio_filter:
        details.append(f"filter: {item.audio_filter}")
    if item.noise:
        details.append(f"noise: {item.noise}")
    return item.text, "   ".join(details)


def draw_card(path: Path, heading: str, text: str, note: str) -> None:
    image = Image.new("RGB", VIDEO_SIZE, BACKGROUND)
    draw = ImageDraw.Draw(image)
    draw.text((60, 50), heading, font=ImageFont.truetype(FONT_PATH, 34), fill=LABEL_COLOR)
    draw.multiline_text((60, 200), "\n".join(textwrap.wrap(text, WRAP_CHARS)),
                        font=ImageFont.truetype(FONT_PATH, 46), fill=TEXT_COLOR, spacing=14)
    draw.text((60, VIDEO_SIZE[1] - 70), note, font=ImageFont.truetype(FONT_PATH, 24), fill=NOTE_COLOR)
    image.save(path)


def read_pcm(path: Path) -> bytes:
    with wave.open(str(path), "rb") as audio:
        if (audio.getframerate(), audio.getnchannels(), audio.getsampwidth()) != (SAMPLE_RATE, 1, 2):
            raise ValueError(f"{path} is not {SAMPLE_RATE} Hz mono 16-bit")
        return audio.readframes(audio.getnframes())


def write_wav(path: Path, pcm: bytes) -> None:
    with wave.open(str(path), "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(SAMPLE_RATE)
        audio.writeframes(pcm)


def seconds_of(pcm: bytes | bytearray) -> float:
    return len(pcm) / 2 / SAMPLE_RATE


def srt_time(seconds: float) -> str:
    millis = round(seconds * 1000)
    hours, millis = divmod(millis, 3_600_000)
    minutes, millis = divmod(millis, 60_000)
    secs, millis = divmod(millis, 1000)
    return f"{hours:02}:{minutes:02}:{secs:02},{millis:03}"


def write_video(work: Path, cards: list[tuple[Path, float]], audio: Path) -> None:
    concat = work / "cards.txt"
    entries = [f"file '{card}'\nduration {seconds:.6f}" for card, seconds in cards]
    # The concat demuxer ignores the last entry's duration unless that file is listed once more.
    concat.write_text("\n".join(entries) + f"\nfile '{cards[-1][0]}'\n", encoding="utf-8")
    run("ffmpeg", "-y", "-f", "concat", "-safe", "0", "-i", str(concat), "-i", str(audio),
        "-vf", "fps=25,format=yuv420p", "-c:v", "libx264", "-tune", "stillimage", "-crf", "20",
        "-c:a", "aac", "-b:a", "160k", "-shortest", "-movflags", "+faststart", str(OUT / f"{NAME}.mp4"))


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    pcm = bytearray()
    gap = bytes(2 * round(GAP_SECONDS * SAMPLE_RATE))
    cards: list[tuple[Path, float]] = []
    cues: list[Cue] = []
    with tempfile.TemporaryDirectory(prefix="speaking-styles-") as temp:
        work = Path(temp)
        blank = work / "blank.png"
        Image.new("RGB", VIDEO_SIZE, BACKGROUND).save(blank)
        for index, item in enumerate(SCRIPT, start=1):
            clip = work / f"{index:02}.wav"
            if isinstance(item, Speech):
                render_speech(item, clip)
            else:
                render_no_speech(item, clip)
            frames = read_pcm(clip)
            start, length = seconds_of(pcm), seconds_of(frames)
            card = work / f"{index:02}.png"
            draw_card(card, f"{index}/{len(SCRIPT)}  {item.style}", *card_lines(item))
            cards += [(card, length), (blank, GAP_SECONDS)]
            pcm += frames + gap
            if isinstance(item, Speech):
                cues.append(Cue(start, start + length, item))
        audio = work / "audio.wav"
        write_wav(audio, bytes(pcm))
        write_video(work, cards, audio)

    srt = "\n".join(f"{n}\n{srt_time(cue.start)} --> {srt_time(cue.end)}\n{cue.speech.text}\n"
                    for n, cue in enumerate(cues, start=1))
    timings = [{"start": round(cue.start, 3), "end": round(cue.end, 3)} | asdict(cue.speech) for cue in cues]
    (OUT / f"{NAME}.srt").write_text(srt, encoding="utf-8")
    (OUT / f"{NAME}.json").write_text(json.dumps(timings, indent=1), encoding="utf-8")
    print(f"wrote {OUT / f'{NAME}.mp4'}: {seconds_of(pcm):.1f} s, {len(cues)} spoken phrases")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
