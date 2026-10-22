<#
Speaks the passages of eval\lookalike\passages.json with Windows' built-in voices and writes, for each one,
eval\lookalike\audio\NAME[.VARIANT].opus for Hushcue.Cli eval:

    NAME.opus          clean, normal speed
    NAME.noisy.opus    light pink noise
    NAME.hard.opus     faster speech, loud pink noise, telephone bandwidth (300-3400 Hz)
    NAME.pauses.opus   light noise, with 12 s of it (no speech) after every third sentence: where Whisper
                       tends to invent text or repeat itself

    powershell -ExecutionPolicy Bypass -File tools\bench\make_lookalike.ps1

Needs ffmpeg on PATH. The committed audio was made with Microsoft David Desktop and Microsoft Zira Desktop (en-US,
on every English Windows 10 and 11); a passage whose voice is missing is spoken by the default voice.
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Speech

$set = Join-Path $PSScriptRoot "..\..\eval\lookalike"
$audio = Join-Path $set "audio"
New-Item -ItemType Directory -Force $audio | Out-Null
$passages = (Get-Content (Join-Path $set "passages.json") -Raw -Encoding utf8 | ConvertFrom-Json).passages
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo 16000, ([System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen), ([System.Speech.AudioFormat.AudioChannel]::Mono)
$voices = (New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices() | ForEach-Object { $_.VoiceInfo.Name }

# suffix, speaking rate (-10..10), pause after every third sentence, noise amplitude, extra filters
$variants = @(
    @{ Suffix = "";        Rate = 0; Pause = 0;  Noise = 0;    Filter = "" },
    @{ Suffix = ".noisy";  Rate = 0; Pause = 0;  Noise = 0.03; Filter = "" },
    @{ Suffix = ".hard";   Rate = 3; Pause = 0;  Noise = 0.12; Filter = ",highpass=f=300,lowpass=f=3400" },
    @{ Suffix = ".pauses"; Rate = 0; Pause = 12; Noise = 0.03; Filter = "" }
)

function Speak($passage, $rate, $pause, $wav) {
    # sentences are spoken in groups of three (or all at once), so the voice keeps its natural pauses between them
    $prompt = New-Object System.Speech.Synthesis.PromptBuilder
    $sentences = [regex]::Split($passage.text.Replace("*", ""), "(?<=[.?!])\s+")
    $group = if ($pause -gt 0) { 3 } else { $sentences.Count }
    for ($i = 0; $i -lt $sentences.Count; $i += $group) {
        if ($i -gt 0) { $prompt.AppendBreak([TimeSpan]::FromSeconds($pause)) }
        $prompt.AppendText(($sentences[$i..([Math]::Min($i + $group, $sentences.Count) - 1)]) -join " ")
    }
    $speaker = New-Object System.Speech.Synthesis.SpeechSynthesizer
    if ($voices -contains $passage.voice) { $speaker.SelectVoice($passage.voice) } else { Write-Warning "$($passage.voice) is not installed" }
    $speaker.Rate = $rate
    $speaker.SetOutputToWaveFile($wav, $format)
    $speaker.Speak($prompt)
    $speaker.Dispose()
}

foreach ($p in $passages) {
    foreach ($v in $variants) {
        $wav = Join-Path ([IO.Path]::GetTempPath()) "hushcue-$($p.name)$($v.Suffix).wav"
        Speak $p $v.Rate $v.Pause $wav
        $out = Join-Path $audio "$($p.name)$($v.Suffix).opus"
        if ($v.Noise -eq 0) {
            ffmpeg -v error -y -i $wav -c:a libopus -b:a 32k $out
        } else {
            ffmpeg -v error -y -i $wav -filter_complex "anoisesrc=color=pink:amplitude=$($v.Noise):seed=7:sample_rate=16000[n];[0:a][n]amix=inputs=2:duration=first:normalize=0$($v.Filter)" -c:a libopus -b:a 32k $out
        }
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $($p.name)$($v.Suffix)" }
        Remove-Item $wav
        $seconds = [double](ffprobe -v error -show_entries format=duration -of csv=p=0 $out)
        "{0,-24} {1,5:F1} s" -f "$($p.name)$($v.Suffix)", $seconds
    }
}
