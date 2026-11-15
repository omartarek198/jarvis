# Installs Piper TTS (local neural voices) for JARVIS.
# Run manually:  powershell -ExecutionPolicy Bypass -File scripts\install-piper.ps1
# Without Piper, JARVIS speaks via Windows SAPI (works out of the box, lower quality).

$ErrorActionPreference = "Stop"
$root      = Split-Path $PSScriptRoot -Parent
$piperDir  = Join-Path $root "voice\tts\piper"
$voicesDir = Join-Path $root "voice\tts\voices"
New-Item -ItemType Directory -Force -Path $piperDir, $voicesDir | Out-Null

Write-Host "Downloading Piper (rhasspy/piper 2023.11.14-2, windows amd64)..."
$zip = Join-Path $env:TEMP "piper_windows_amd64.zip"
Invoke-WebRequest -Uri "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip" -OutFile $zip
Expand-Archive -Path $zip -DestinationPath (Join-Path $env:TEMP "piper-extract") -Force
Copy-Item (Join-Path $env:TEMP "piper-extract\piper\*") $piperDir -Recurse -Force
Remove-Item $zip -Force

$voices = @(
    # JARVIS default: British male
    @{ name = "en_GB-alan-medium";   url = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_GB/alan/medium/en_GB-alan-medium" },
    # "Boris" fun profile: Russian voice reading English
    @{ name = "ru_RU-dmitri-medium"; url = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/ru/ru_RU/dmitri/medium/ru_RU-dmitri-medium" }
)
foreach ($v in $voices) {
    Write-Host "Downloading voice $($v.name)..."
    Invoke-WebRequest -Uri "$($v.url).onnx"      -OutFile (Join-Path $voicesDir "$($v.name).onnx")
    Invoke-WebRequest -Uri "$($v.url).onnx.json" -OutFile (Join-Path $voicesDir "$($v.name).onnx.json")
}

Write-Host "Piper installed. Restart the kernel — TTS engine will switch from sapi to piper automatically."
