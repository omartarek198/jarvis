# Starts the JARVIS kernel and tray app, then opens the console.
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\start-jarvis.ps1

$root = Split-Path $PSScriptRoot -Parent

Write-Host "Building..."
dotnet build (Join-Path $root "Jarvis.sln") --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed"; exit 1 }

Write-Host "Starting kernel (http://localhost:5588)..."
Start-Process -WindowStyle Minimized -FilePath "dotnet" `
    -ArgumentList "run --project `"$(Join-Path $root 'src\Jarvis.Kernel')`" --no-build"

Start-Sleep -Seconds 5

Write-Host "Starting tray (hold Right-Ctrl to talk, or say 'hey Jarvis')..."
# Optional: pin specific capture devices by (partial) name. By default the tray uses the
# Windows communications-default microphone.
# $env:JARVIS_MIC = "<microphone name>"
# $env:JARVIS_WAKE_MIC = "<microphone name>"
Start-Process -FilePath (Join-Path $root "src\Jarvis.Tray\bin\Debug\net9.0-windows\Jarvis.Tray.exe")

Write-Host "Opening console..."
python (Join-Path $root "scripts\open-console.py")
