# Puts the Tidy scene's root entities under empty group parents (identity transform at the origin, so world positions are unchanged).
#   & Tools\Group-TidyScene.ps1        (editor open on the Tidy scene, Edit mode; save the scene afterwards)
# Safe to rerun: a group that already exists is reused and only unparented entities are moved into it.
$ErrorActionPreference = 'Stop'
$call = Join-Path (Split-Path $MyInvocation.MyCommand.Path) 'call.ps1'
function Call($name, $json = '{}') { & $call $name $json }
function Find($name) { [regex]::Match((Call search_entities ('{"namePattern":"' + $name + '"}')), "- $name \(UUID:\s*(\d+)\)").Groups[1].Value }

if ((Call get_current_scene) -notmatch 'Tidy') { throw 'open the Tidy scene first' }

$groups = [ordered]@{
    'Environment' = 'Ground', 'Wall_North', 'Wall_South', 'Wall_East', 'Wall_West'
    'Randomised'  = 'Chair_01', 'Chair_02', 'Chair_03', 'Chair_04', 'BarStool_01', 'BarStool_02', 'TableBistro_01', 'TableSquare_01', 'SnakePlant_01', 'FiddleFig_01'
    'Objectives'  = 'Counter', 'TableCommunal_01', 'Item_1', 'Item_2', 'Item_3'
    'Lighting'    = 'Sun', 'Light_A', 'Light_B', 'Light_C'
    'Cameras'     = 'Cam_Overview'
}
foreach ($g in $groups.Keys) {
    $gid = Find $g
    if (-not $gid) {
        Call create_entity ('{"name":"' + $g + '"}') | Out-Null
        $gid = Find $g
        if (-not $gid) { throw "could not create group $g" }
    }
    foreach ($n in $groups[$g]) {
        $id = Find $n
        if (-not $id) { throw "$n not found" }
        $r = Call parent_entity ('{"childID":"' + $id + '","parentID":"' + $gid + '"}')
        Write-Host "$g <- $n : $r"
    }
}
