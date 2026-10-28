# Brings the RobotCafe parts this project needs into the project, and applies RobotCafe-changes.patch.
#
#   Tools\Setup-FromRobotCafe.ps1                       (RobotCafe sitting next to this folder)
#   Tools\Setup-FromRobotCafe.ps1 -RobotCafe D:\path\to\RobotCafe
#
# RobotCafe is Lucky Robots' example project; install it from the Lucky Engine Content Vault first. Nothing from it is in this
# repository. Safe to run twice: the files are copied again (which undoes the patch) and the patch is re-applied.
# Does NOT touch Assets\Scenes\Tidy.hscene (do not run make_tidy_base.py either: it overwrites that scene).
param([string]$RobotCafe)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $MyInvocation.MyCommand.Path)
if (-not $RobotCafe) { $RobotCafe = Join-Path (Split-Path $root) 'RobotCafe' }

$parts = @(
    @{ From = 'Assets\Robots\UnitreeG1';                       To = 'Assets\Robots\UnitreeG1';                       Skip = @() },
    @{ From = 'Assets\Cafe';                                   To = 'Assets\Cafe';                                   Skip = @() },
    @{ From = 'Assets\Scripts\Client\Source\CafeRobot';        To = 'Assets\Scripts\Client\Source\CafeRobot';        Skip = @('Cinematics') }
)

foreach ($p in $parts) {
    if (-not (Test-Path (Join-Path $RobotCafe $p.From))) {
        throw "Not found: $(Join-Path $RobotCafe $p.From)`nInstall RobotCafe from the Lucky Engine Content Vault and put it next to this folder, or pass -RobotCafe <path>."
    }
}

foreach ($p in $parts) {
    $args = @((Join-Path $RobotCafe $p.From), (Join-Path $root $p.To), '/E', '/NFL', '/NDL', '/NJH', '/NJS')
    if ($p.Skip) { $args += '/XD'; $args += $p.Skip }
    robocopy @args | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed for $($p.From) (exit $LASTEXITCODE)" }
    Write-Host "copied $($p.From)"
}

Push-Location $root
$ErrorActionPreference = 'Continue'    # git writes its normal "does not apply" message to stderr; Windows PowerShell 5.1 would treat that as fatal
try {
    $patch = 'RobotCafe-changes.patch'
    $apply = @('--directory=Assets/Scripts/Client/Source', '--whitespace=nowarn')
    git apply @apply --reverse --check $patch *> $null
    if ($LASTEXITCODE -eq 0) { Write-Host 'patch already applied' }
    else {
        git apply @apply $patch
        if ($LASTEXITCODE -ne 0) { throw 'patch did not apply: is this the RobotCafe version the patch was made against?' }
        Write-Host 'patch applied'
    }
} finally { Pop-Location }

Write-Host "`nDone. Open G1RoomNav.hproj in Lucky Engine (scene Tidy) and let it build the scripts."
