# Scripted walkthrough of the public demo (demo data, demo agent adapter, external actions off).
# Start the kernel first:  dotnet run --project src\Jarvis.Kernel
# Then:                    powershell -ExecutionPolicy Bypass -File scripts\demo.ps1 [-Fast]
param([switch]$Fast)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$cli  = Join-Path $root "src\Jarvis.Cli\bin\Debug\net9.0\Jarvis.Cli.exe"
$api  = "http://localhost:5588"
if (-not (Test-Path $cli)) { Write-Error "Build first: dotnet build Jarvis.sln" }

function Step([string]$title) { Write-Host "`n== $title" -ForegroundColor Cyan }
function Say([string]$text) {
    Write-Host "> $text" -ForegroundColor Yellow
    & $cli --source demo $text
    if (-not $Fast) { Start-Sleep -Milliseconds 1200 }
}

$health = Invoke-RestMethod "$api/api/system/health"
Write-Host ("kernel {0} · demoMode={1} · agentMode={2} · externalActions={3}" -f `
    $health.kernel, $health.demoMode, $health.agentMode, $health.externalActions)

Step "1. Commands route through the grammar tier against local SQLite data"
Say "what should I focus on right now"
Say "what do I owe people"

Step "2. A coding task is a Mutate command: it parks until confirmed"
Say "ask claude to fix the tax rounding in northwind"
Say "what did you just do"
Say "confirm"

Step "3. The agent runs as a supervised child process; its RESULT block is parsed"
do {
    Start-Sleep -Seconds 1
    $run = (Invoke-RestMethod "$api/api/agent-runs?take=1")[0]
} while ($run.status -eq "running")
Say "what did claude find"
Say "list tasks for northwind"

Step "4. Outbound and OS-level actions are disabled in this build, even after confirm"
Say "lock the pc"
Say "confirm"

Step "5. The audit trail explains every decision"
Say "why did you do that"
Say "reliability report"
Invoke-RestMethod "$api/api/commands?take=6" |
    Select-Object @{n='time';e={([datetime]$_.createdAt).ToLocalTime().ToString('HH:mm:ss')}}, tier, intent, safety, status, tool |
    Format-Table -AutoSize
