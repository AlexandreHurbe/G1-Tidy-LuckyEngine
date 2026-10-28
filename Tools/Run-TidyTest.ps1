# Runs N episodes of the combined task in the open editor and prints the controller's log lines.
#
#   powershell -ExecutionPolicy Bypass -File Tools\Run-TidyTest.ps1 -Episodes 1 -Seed 42
#
# Needs the "Tidy" scene open (Build-TidyScene.ps1 leaves it open). It sets MaxEpisodes and Seed on the controller
# for the run and puts them back (0 / 0 = run forever, random seed) afterwards.
# Recording: only happens if you are signed in to LuckyHub; pass -NoRecord to switch it off for the test.
param([int]$Episodes = 1, [int]$Seed = 42, [int]$MaxSeconds = 900, [switch]$NoRecord)

$ErrorActionPreference = 'Stop'
$here = Split-Path $MyInvocation.MyCommand.Path
$call = Join-Path $here 'call.ps1'
$log = Join-Path $env:TEMP 'LuckyEngine\Logs\APP.log'

function Call($name, $json = '{}') { & $call $name $json }
function Prop($id, $name, $value) { Call set_entity_property ('{"entityID":"' + $id + '","componentName":"Script","propertyName":"' + $name + '","value":"' + $value + '"}') | Out-Null }

$scene = Call get_current_scene
if ($scene -notmatch 'Tidy') { throw "open the Tidy scene first (current: $scene)" }
$m = [regex]::Match((Call search_entities '{"scriptClass":"TidyWaiter"}'), 'UUID:\s*(\d+)')
if (-not $m.Success) { throw 'no entity with the TidyWaiter script found' }
$g1 = $m.Groups[1].Value

Prop $g1 MaxEpisodes $Episodes
Prop $g1 Seed $Seed
if ($NoRecord) { Prop $g1 Record 'false' }

Write-Host "Playing $Episodes episode(s), seed $Seed ..."
Call harness/enter_play_mode | Out-Null
$until = (Get-Date).AddSeconds($MaxSeconds)
$run = @()
do {
    Start-Sleep -Seconds 5
    $lines = Get-Content $log
    $idx = ($lines | Select-String 'OnRuntimeStart Called').LineNumber | Select-Object -Last 1
    if ($idx) { $run = $lines[($idx - 1)..($lines.Count - 1)] | Where-Object { $_ -match '\[Tidy\]|\[CafeRobot\]|C# Exception|Exception' } }
} while (-not ($run -match '\[Tidy\] done:|Exception') -and (Get-Date) -lt $until)

Call harness/exit_play_mode | Out-Null
Prop $g1 MaxEpisodes 0
Prop $g1 Seed 0
if ($NoRecord) { Prop $g1 Record 'true' }

Write-Host "`n--- [Tidy] summary lines ---"
$run | Where-Object { $_ -match '\[Tidy\]' } | ForEach-Object { $_ -replace '^\[\d+:\d+:\d+\] \[\w+\] APP: ', '' }
Write-Host "`n--- failures reported by the cafe controller ---"
$run | Where-Object { $_ -match 'failed|Exception|Fall' -and $_ -notmatch '\[Tidy\]' } | Select-Object -First 25 | ForEach-Object { $_ -replace '^\[\d+:\d+:\d+\] \[\w+\] APP: ', '' }
if (-not ($run -match '\[Tidy\] done:')) { Write-Host "`nThe run did not finish within $MaxSeconds s." }
