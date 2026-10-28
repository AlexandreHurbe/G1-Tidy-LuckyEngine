# Rebuilds the Tidy scene's room as a larger room (12 x 9 m inside) with windows, in the open editor (Edit mode).
#   & Tools\Build-Room.ps1            then save the scene (open another scene with on_unsaved=save, or Ctrl+S)
#
# The four Wall_* boxes and Ground keep their names and colliders (the controller and the nav map use them) but are
# resized and hidden. What you see is built from visual-only pieces (no collider): wall segments around the window
# openings, frames, a translucent glass pane per window, an interior floor and an outdoor lawn. So the windows look
# like windows, but the walls stay solid for the robot and for the lidar.
# Needs the glass pane template: Assets\Materials\Glass_Test_Material.hmaterial with Transparent: true, AlphaFactor 0.2,
# and ten entities Glass_01..Glass_10 sharing it (duplicates of one pane).
# TidyWaiter.RoomMin/RoomMax (furniture bounds) are set here too.
$ErrorActionPreference = 'Stop'
$call = Join-Path (Split-Path $MyInvocation.MyCommand.Path) 'call.ps1'
function Call($name, $json = '{}') { & $call $name $json }
function Find($name) { [regex]::Match((Call search_entities ('{"namePattern":"' + $name + '"}')), "- $name \(UUID:\s*(\d+)\)").Groups[1].Value }
function Need($name) { $id = Find $name; if (-not $id) { throw "$name not found" }; $id }
function Prop($id, $component, $name, $value) {
    $r = Call set_entity_property ('{"entityID":"' + $id + '","componentName":"' + $component + '","propertyName":"' + $name + '","value":"' + $value + '"}')
    if ($r -notmatch 'SUCCESS') { throw "set $name on $id failed: $r" }
}
$inv = [Globalization.CultureInfo]::InvariantCulture
function F($v) { ([double]$v).ToString('0.####', $inv) }
function V3($x, $y, $z) { "$(F $x) $(F $y) $(F $z)" }

# Inside dimensions and wall build-up.
$HX = 6.0; $HZ = 4.5; $H = 3.0; $T = 0.2          # half sizes of the room, wall height, wall thickness
$SillH = 1.1; $WinTop = 2.4; $WinW = 1.8; $WinH = $WinTop - $SillH; $WinMid = ($SillH + $WinTop) / 2

if (Find 'Floor') { throw 'the room was already built (entity Floor exists)' }
if ((Call get_current_scene) -notmatch 'Tidy') { throw 'open the Tidy scene first' }
$env = Need 'Environment'; $lighting = Need 'Lighting'

# Takes the mesh off (the box collider's size comes from the transform scale, not from the mesh).
function Hide($id) {
    $r = Call remove_component ('{"entityID":"' + $id + '","componentType":"StaticMesh"}')
    if ($r -notmatch 'SUCCESS') { throw "removing the mesh from $id failed: $r" }
}

# ---- collision shell: same entities as before, bigger and hidden ------------------------------------------
$walls = @{
    Wall_North = @{ p = (V3 0 ($H / 2) (-$HZ - $T / 2)); s = (V3 (2 * $HX + 2 * $T) $H $T) }
    Wall_South = @{ p = (V3 0 ($H / 2) ($HZ + $T / 2));  s = (V3 (2 * $HX + 2 * $T) $H $T) }
    Wall_East  = @{ p = (V3 ($HX + $T / 2) ($H / 2) 0);  s = (V3 $T $H (2 * $HZ + 2 * $T)) }
    Wall_West  = @{ p = (V3 (-$HX - $T / 2) ($H / 2) 0); s = (V3 $T $H (2 * $HZ + 2 * $T)) }
}
foreach ($n in $walls.Keys) {
    $id = Need $n
    Prop $id 'Transform' 'Position' $walls[$n].p
    Prop $id 'Transform' 'Scale' $walls[$n].s
    Hide $id
}
$ground = Need 'Ground'
Prop $ground 'Transform' 'Scale' (V3 60 0.2 60)
Hide $ground

# ---- visual-only pieces ------------------------------------------------------------------------------------
function Vis($name, $x, $y, $z, $sx, $sy, $sz, $r, $g, $b, $rough = 0.8) {
    $o = Call create_game_object ('{"name":"' + $name + '","shape":"Cube","x":' + (F $x) + ',"y":' + (F $y) + ',"z":' + (F $z) +
        ',"scaleX":' + (F $sx) + ',"scaleY":' + (F $sy) + ',"scaleZ":' + (F $sz) + ',"colorR":' + $r + ',"colorG":' + $g + ',"colorB":' + $b +
        ',"roughness":' + $rough + ',"addCollider":false}')
    $id = [regex]::Match($o, 'UUID:\s*(\d+)').Groups[1].Value
    if (-not $id) { throw "create $name failed: $o" }
    Call parent_entity ('{"childID":"' + $id + '","parentID":"' + $env + '"}') | Out-Null
}
Vis 'Floor' 0 0 0 (2 * $HX + 2 * $T) 0.01 (2 * $HZ + 2 * $T) 0.55 0.42 0.30 0.6
Vis 'Lawn' 0 -0.0105 0 60 0.01 60 0.25 0.40 0.20 1.0

# A wall piece along one wall: $t0..$t1 along the wall, $y0..$y1 in height. $w = N/S/E/W.
function Piece($name, $w, $t0, $t1, $y0, $y1, $depth = $T, $off = 0, $col = @(0.85, 0.82, 0.76)) {
    $len = $t1 - $t0; $c = ($t0 + $t1) / 2; $ym = ($y0 + $y1) / 2; $h = $y1 - $y0
    switch ($w) {
        'N' { Vis $name $c $ym (-$HZ - $T / 2 + $off) $len $h $depth $col[0] $col[1] $col[2] }
        'S' { Vis $name $c $ym ($HZ + $T / 2 - $off) $len $h $depth $col[0] $col[1] $col[2] }
        'E' { Vis $name ($HX + $T / 2 - $off) $ym $c $depth $h $len $col[0] $col[1] $col[2] }
        'W' { Vis $name (-$HX - $T / 2 + $off) $ym $c $depth $h $len $col[0] $col[1] $col[2] }
    }
}
# Windows: centres along each wall (N/S run along x, E/W along z).
$windows = [ordered]@{ N = @(-3.6, 0, 3.6); S = @(-3.6, 0, 3.6); E = @(-2.25, 2.25); W = @(-2.25, 2.25) }
$frame = @(0.93, 0.93, 0.90)
$glass = @{ N = 1; S = 4; E = 7; W = 9 }   # first Glass_NN index per wall
$ops = @()
foreach ($w in $windows.Keys) {
    $half = if ($w -in 'N', 'S') { $HX + $T } else { $HZ }
    $cs = $windows[$w]
    Piece "Wall_${w}_sill" $w (-$half) $half 0 $SillH
    Piece "Wall_${w}_lintel" $w (-$half) $half $WinTop $H
    $edge = -$half; $k = 0
    foreach ($c in $cs) {
        $k++
        Piece "Wall_${w}_pier$k" $w $edge ($c - $WinW / 2) $SillH $WinTop
        $edge = $c + $WinW / 2
        Piece "Window_${w}${k}_mullion" $w ($c - 0.025) ($c + 0.025) $SillH $WinTop 0.08 0 $frame
        Piece "Window_${w}${k}_transom" $w ($c - $WinW / 2) ($c + $WinW / 2) ($WinMid - 0.025) ($WinMid + 0.025) 0.08 0 $frame
        Piece "Window_${w}${k}_ledge" $w ($c - $WinW / 2 - 0.05) ($c + $WinW / 2 + 0.05) ($SillH) ($SillH + 0.04) 0.3 0.2 $frame
        # glass pane: thin along the wall's normal axis
        $gid = Need ('Glass_{0:00}' -f ($glass[$w] + $k - 1))
        $px, $pz, $sx, $sz = switch ($w) {
            'N' { $c; (-$HZ - $T / 2); $WinW; 0.02 }
            'S' { $c; ($HZ + $T / 2); $WinW; 0.02 }
            'E' { ($HX + $T / 2); $c; 0.02; $WinW }
            'W' { (-$HX - $T / 2); $c; 0.02; $WinW }
        }
        $ops += '{"entityID":"' + $gid + '","componentName":"Transform","propertyName":"Position","value":"' + (V3 $px $WinMid $pz) + '"}'
        $ops += '{"entityID":"' + $gid + '","componentName":"Transform","propertyName":"Scale","value":"' + (V3 $sx $WinH $sz) + '"}'
        Call parent_entity ('{"childID":"' + $gid + '","parentID":"' + $env + '"}') | Out-Null
    }
    Piece "Wall_${w}_pier$($k + 1)" $w $edge $half $SillH $WinTop
}
Call batch_set_entity_property ('{"operations":[' + ($ops -join ',') + ']}') | Out-Null
$test = Find 'Glass_Test'; if ($test) { Call destroy_entity ('{"entityID":"' + $test + '"}') | Out-Null }

# ---- lights, camera, furniture bounds ----------------------------------------------------------------------
$lightPos = @{ Light_A = @(-4, 2.7, -2.5); Light_B = @(0, 2.7, -2.5); Light_C = @(4, 2.7, -2.5) }
foreach ($n in $lightPos.Keys) { Prop (Need $n) 'Transform' 'Position' (V3 $lightPos[$n][0] $lightPos[$n][1] $lightPos[$n][2]) }
foreach ($p in @(@('Light_D', -4, 2.7, 2.5), @('Light_E', 0, 2.7, 2.5), @('Light_F', 4, 2.7, 2.5))) {
    $l = [regex]::Match((Call create_entity ('{"name":"' + $p[0] + '","x":' + $p[1] + ',"y":' + $p[2] + ',"z":' + $p[3] + '}')), 'UUID:\s*(\d+)').Groups[1].Value
    Call add_component ('{"entityID":"' + $l + '","componentType":"PointLight"}') | Out-Null
    Prop $l 'PointLight' 'Intensity' '3'
    Prop $l 'PointLight' 'Radius' '10'
    Call parent_entity ('{"childID":"' + $l + '","parentID":"' + $lighting + '"}') | Out-Null
}
Prop (Need 'Cam_Overview') 'Transform' 'Position' (V3 0 13 5.8)
$robot = [regex]::Match((Call search_entities '{"scriptClass":"TidyWaiter"}'), 'UUID:\s*(\d+)').Groups[1].Value
Prop $robot 'Script' 'RoomMin' (V3 (-$HX + 0.1) 0 (-$HZ + 0.1))
Prop $robot 'Script' 'RoomMax' (V3 ($HX - 0.1) 0 ($HZ - 0.1))
Write-Host 'Room built. Save the scene, then run Tools\Run-TidyTest.ps1.'
