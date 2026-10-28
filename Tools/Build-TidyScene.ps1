# Builds the combined "Tidy" scene in the open Lucky Engine editor: the box room (ground, walls, counter), the items,
# lights and a camera around the RobotCafe G1 and the cafe furniture that make_tidy_base.py copied in, then wires up
# the TidyWaiter controller. Rendering stays rasterised (no path tracing).
#
# Every step prints what it does and stops on the first failure.
#
# Before running:
#   1. The editor is open on the G1RoomNav project (any scene, no unsaved changes).
#   2. Signed in to LuckyHub only matters for recording, not for building.
# Run from the project folder:   powershell -ExecutionPolicy Bypass -File Tools\Build-TidyScene.ps1
#   -SkipBase     the base scene already exists (skip make_tidy_base.py)
#   -SkipCameras  do not bind the recording camera in the scene's recorder settings
param([switch]$SkipBase, [switch]$SkipCameras)

$ErrorActionPreference = 'Stop'
$here = Split-Path $MyInvocation.MyCommand.Path
$project = Split-Path $here
$call = Join-Path $here 'call.ps1'
$sceneFile = Join-Path $project 'Assets\Scenes\Tidy.hscene'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Call($name, $json = '{}') { & $call $name $json }
function Id($text) { $m = [regex]::Match($text, 'UUID:\s*(\d+)'); if (-not $m.Success) { throw "no entity id in: $text" }; $m.Groups[1].Value }
function Wait-Scene($pattern, $seconds = 40) {
    $until = (Get-Date).AddSeconds($seconds)
    do { Start-Sleep -Seconds 2; $c = Call get_current_scene } while ($c -notmatch $pattern -and (Get-Date) -lt $until)
    if ($c -notmatch $pattern) { throw "scene did not load ($pattern): $c" }
    Start-Sleep -Seconds 3
}
function Prop($id, $component, $name, $value) {
    $r = Call set_entity_property ('{"entityID":"' + $id + '","componentName":"' + $component + '","propertyName":"' + $name + '","value":"' + $value + '"}')
    if ($r -notmatch 'SUCCESS') { throw "set $name on $id failed: $r" }
}

# A scaled cube with a MuJoCo box collider. $dynamic adds a free joint so the layout shuffle can move it.
function New-Box($name, $x, $y, $z, $sx, $sy, $sz, $r, $g, $b, [bool]$dynamic) {
    $o = Call create_game_object ('{"name":"' + $name + '","shape":"Cube","x":' + $x + ',"y":' + $y + ',"z":' + $z +
        ',"scaleX":' + $sx + ',"scaleY":' + $sy + ',"scaleZ":' + $sz + ',"colorR":' + $r + ',"colorG":' + $g + ',"colorB":' + $b + ',"roughness":0.8,"addCollider":false}')
    $id = Id $o
    $static = if ($dynamic) { 'false' } else { 'true' }
    $m = Call make_mujoco_dynamic_body ('{"entityID":"' + $id + '","shape":"box","isStatic":' + $static + '}')
    if ($m -notmatch 'SUCCESS') { throw "make_mujoco_dynamic_body failed for ${name}: $m" }
    Write-Host ("   {0,-14} {1}" -f $name, $(if ($dynamic) { 'dynamic' } else { 'static' }))
    return $id
}

# ---------------------------------------------------------------------------------------------------------
Step 'Base scene (cafe G1 + robot assets)'
if (-not $SkipBase) { python -I (Join-Path $here 'make_tidy_base.py'); if ($LASTEXITCODE -ne 0) { throw 'make_tidy_base.py failed' } }
if (-not (Test-Path $sceneFile)) { throw "missing $sceneFile" }

Step 'Open the scene'
Call refresh_assets | Out-Null
$unsaved = Call harness/get_unsaved_changes
if ($unsaved -notmatch '"dirty_items":\[\]') { throw "unsaved changes in the editor: $unsaved" }
Call open_scene '{"scenePath":"Scenes\\Tidy.hscene","on_unsaved":"fail"}' | Out-Null
Wait-Scene 'Tidy'
$g1 = Id (Call search_entities '{"scriptClass":"CafeWaiter"}')
Write-Host "   robot entity $g1"

Step 'Compile the scripts'
$build = Call build_script_assembly
if ($build -notmatch 'SUCCESS') { Write-Host $build; throw 'script build failed' }
Write-Host '   build ok'

# ---------------------------------------------------------------------------------------------------------
Step 'Room: ground and walls (static)'
New-Box 'Ground'     0 -0.1  0   14 0.2 14   0.35 0.35 0.33 $false | Out-Null
New-Box 'Wall_North' 0  1.3 -3.1 8.4 2.6 0.2 0.85 0.82 0.76 $false | Out-Null
New-Box 'Wall_South' 0  1.3  3.1 8.4 2.6 0.2 0.85 0.82 0.76 $false | Out-Null
New-Box 'Wall_East'  4.1 1.3 0   0.2 2.6 6.4 0.85 0.82 0.76 $false | Out-Null
New-Box 'Wall_West' -4.1 1.3 0   0.2 2.6 6.4 0.85 0.82 0.76 $false | Out-Null

Step 'Counter (1.0 m box, where the items start)'
$counter = New-Box 'Counter' 0 0.5 -2.65  1.8 1.0 0.6  0.80 0.78 0.74 $false

Step 'Furniture: the cafe props that make_tidy_base.py copied into the scene'
# Nothing is created here: the dining table (TableCommunal_01, static), 4 chairs, 2 bar stools, 2 small tables and 2 plants
# are already in Tidy.hscene with the cafe's meshes, textures and colliders. Check they arrived.
$table = $null
foreach ($name in @('TableCommunal_01', 'Chair_01', 'Chair_02', 'Chair_03', 'Chair_04', 'BarStool_01', 'BarStool_02', 'TableSquare_01', 'TableBistro_01', 'FiddleFig_01', 'SnakePlant_01')) {
    $found = Call search_entities ('{"namePattern":"' + $name + '"}')
    $m = [regex]::Match($found, '- ' + $name + ' \(UUID: (\d+)\)')
    if (-not $m.Success) { throw "prop $name is not in the scene; re-run make_tidy_base.py" }
    if ($name -eq 'TableCommunal_01') { $table = $m.Groups[1].Value }
    Write-Host ("   {0,-18} {1}" -f $name, ($found -split "`n" | Where-Object { $_ -match [regex]::Escape($name) } | Select-Object -First 1).Trim())
}
if (-not $table) { throw 'dining table not found' }
Write-Host '   Check that TableCommunal_01 shows no MujocoJoint above (it must be static) and the others do.'

Step 'Items: tapered blocks on the counter (origin at the base, mesh collider, light and grippy)'
foreach ($i in 1..3) {
    $x = -0.6 + 0.6 * ($i - 1)
    $o = Call import_and_spawn_mesh ('{"sourcePath":"' + ($project -replace '\\', '/') + '/Assets/Props/Block/Frustum.gltf","entityName":"Item_' + $i + '","x":' + $x + ',"y":1.002,"z":-2.41,"scale":1.0}')
    $id = Id $o
    $m = Call make_mujoco_dynamic_body ('{"entityID":"' + $id + '","shape":"mesh"}')
    if ($m -notmatch 'SUCCESS') { throw "Item_$i collider failed: $m" }
    Prop $id 'MujocoMeshCollider' 'Density' '300'                      # density 300 + friction 2 carried best in the cafe tests
    Prop $id 'MujocoMeshCollider' 'Friction' '2 0.005 0.0001'
    Prop $id 'MujocoJoint' 'Damping' '0'                               # as the cafe's cup
    Prop $id 'MujocoJoint' 'Armature' '0'
    Write-Host "   Item_$i"
}

Step 'Lights and camera (plain deferred rendering: this GPU has no ray tracing)'
$sun = Id (Call create_entity '{"name":"Sun","x":0,"y":3.9,"z":0}')
Call add_component ('{"entityID":"' + $sun + '","componentType":"DirectionalLight"}') | Out-Null
Prop $sun 'Transform' 'Rotation' '5.8309 0.39095 3.5223'
foreach ($p in @(@('Light_A', -2, 2.5, 0.5), @('Light_B', 2, 2.5, 0.5), @('Light_C', 0, 2.5, -1.5))) {
    $l = Id (Call create_entity ('{"name":"' + $p[0] + '","x":' + $p[1] + ',"y":' + $p[2] + ',"z":' + $p[3] + '}'))
    Call add_component ('{"entityID":"' + $l + '","componentType":"PointLight"}') | Out-Null
    Prop $l 'PointLight' 'Intensity' '3'
    Prop $l 'PointLight' 'Radius' '10'
}
$cam = Id (Call create_entity '{"name":"Cam_Overview","x":0,"y":8,"z":4.2}')
Call add_component ('{"entityID":"' + $cam + '","componentType":"Camera","meshHandle":"Perspective"}') | Out-Null
Prop $cam 'Transform' 'Rotation' '-1.15 0 0'

Step 'Controller: swap the cafe script for TidyWaiter and wire it up'
Call remove_component ('{"entityID":"' + $g1 + '","componentType":"Script"}') | Out-Null
$r = Call add_component ('{"entityID":"' + $g1 + '","componentType":"Script","meshHandle":"Cafe.Robotics.TidyWaiter"}')
if ($r -notmatch 'SUCCESS') { throw "attaching TidyWaiter failed: $r" }
Prop $g1 'Script' 'Counter' $counter
Prop $g1 'Script' 'Table' $table
Write-Host '   Counter, Table assigned. ItemNames / FurnitureNames already match the entities above.'

Step 'Save'
Call open_scene '{"scenePath":"Scenes\\Tidy.hscene","on_unsaved":"save"}' | Out-Null
Start-Sleep -Seconds 8
$unsaved = Call harness/get_unsaved_changes
Write-Host "   $unsaved"

# ---------------------------------------------------------------------------------------------------------
if (-not $SkipCameras) {
    Step 'Bind Cam_Overview in the scene recorder settings (1280x720, 50 fps, rasterised)'
    $t = [IO.File]::ReadAllText($sceneFile)
    $old = "    Cameras:`n      []"
    if (-not $t.Contains($old)) { Write-Host '   no empty Cameras list found (line endings?); bind the camera in Settings > Scene > Recorder instead' }
    else {
        $lines = @(
            "      - EntityID: $cam", '        CameraName: cam_overview', '        VideoWidth: 1280', '        VideoHeight: 720',
            '        FrameRate: FPS_50', '        RenderMode: 0', '        PTQuality: 3', '        PTSamples: 1024', '        PTBounces: 8',
            '        PTSubFrames: 6', '        PTDenoise: true', '        PTDenoiser: 3', '        PTDLSSMode: 0', '        RecordMask: false',
            '        DepthMode: Off', '        DepthScale: 0.001', '        SensorModel: None', '        SensorMaxRange: 10',
            '        SensorMinRange: 0.1', '        SensorDetectFloor: 0.03', '        SensorBaseline: 0.05', '        SensorSubpixelSigma: 0.08',
            '        SensorDebugChannel: Off', '        Enabled: true')
        $t = $t.Replace($old, "    Cameras:`n" + ($lines -join "`n"))
        [IO.File]::WriteAllText($sceneFile, $t, (New-Object Text.UTF8Encoding($false)))
        # The editor prefers its autosave over the saved file, and ignores a reopen of the scene it already has:
        # switch away, remove the autosave, switch back.
        Call open_scene '{"scenePath":"Scenes\\Main.hscene","on_unsaved":"discard"}' | Out-Null
        Wait-Scene 'Main'
        $auto = "$sceneFile.auto"
        if (Test-Path $auto) { Remove-Item $auto }
        Call open_scene '{"scenePath":"Scenes\\Tidy.hscene","on_unsaved":"discard"}' | Out-Null
        Wait-Scene 'Tidy'
        Write-Host '   bound and reloaded'
    }
}

Step 'Done'
Write-Host 'Next: Tools\Run-TidyTest.ps1 -Episodes 1  (nothing has been run yet; expect compile and tuning fixes).'
