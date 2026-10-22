<#
Speaks the passages of eval\lookalike\passages.json with Windows' built-in voices and writes, for each one,
eval\lookalike\audio\NAME.opus (clean) and NAME.noisy.opus (the same with pink noise mixed in), for Hushcue.Cli eval.

    powershell -ExecutionPolicy Bypass -File tools\bench\make_lookalike.ps1 [-NoiseLevel 0.03]

Needs ffmpeg on PATH. The committed audio was made with Microsoft David Desktop and Microsoft Zira Desktop (en-US,
on every English Windows 10 and 11); a passage whose voice is missing is spoken by the default voice.
#>
param([double]$NoiseLevel = 0.03)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Speech

$set = Join-Path $PSScriptRoot "..\..\eval\lookalike"
$audio = Join-Path $set "audio"
New-Item -ItemType Directory -Force $audio | Out-Null
$passages = (Get-Content (Join-Path $set "passages.json") -Raw -Encoding utf8 | ConvertFrom-Json).passages
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo 16000, ([System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen), ([System.Speech.AudioFormat.AudioChannel]::Mono)
$voices = (New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices() | ForEach-Object { $_.VoiceInfo.Name }

foreach ($p in $passages) {
    $wav = Join-Path ([IO.Path]::GetTempPath()) "hushcue-$($p.name).wav"
    $speaker = New-Object System.Speech.Synthesis.SpeechSynthesizer
    if ($voices -contains $p.voice) { $speaker.SelectVoice($p.voice) } else { Write-Warning "$($p.voice) is not installed" }
    $speaker.SetOutputToWaveFile($wav, $format)
    $speaker.Speak($p.text.Replace("*", ""))
    $speaker.Dispose()

    $clean = Join-Path $audio "$($p.name).opus"
    $noisy = Join-Path $audio "$($p.name).noisy.opus"
    ffmpeg -v error -y -i $wav -c:a libopus -b:a 32k $clean
    ffmpeg -v error -y -i $wav -filter_complex "anoisesrc=color=pink:amplitude=$($NoiseLevel):seed=7:sample_rate=16000[n];[0:a][n]amix=inputs=2:duration=first:normalize=0" -c:a libopus -b:a 32k $noisy
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $($p.name)" }
    Remove-Item $wav
    $seconds = [double](ffprobe -v error -show_entries format=duration -of csv=p=0 $clean)
    "{0,-16} {1,5:F1} s" -f $p.name, $seconds
}
