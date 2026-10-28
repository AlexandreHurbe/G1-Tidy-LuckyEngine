# Tidy: a humanoid clears a counter in a randomised room (Lucky Engine)

A Unitree G1 walks to a counter, picks up three cups one at a time, carries each to a dining table and sets it down in its own
spot. Every episode the room is different, and every episode can be recorded as a dataset: video, robot state and lidar.

Scene: `Assets/Scenes/Tidy.hscene` (12 x 9 m room, 10 windows, 6 point lights and a sun).

## What is randomised each episode

| What | How |
|---|---|
| Counter | Flush against a random wall (not in a corner), random position along it |
| Dining table | Anywhere clear, at least 0.8 m from the walls, random yaw |
| 10 pieces of furniture | 4 chairs, 2 bar stools, 2 small tables, 2 plants: random non-overlapping poses with gaps the robot can pass |
| 3 cups | Random points along the counter's front edge, random yaw |
| Delivery spots | Recomputed from the table's pose: three spots along its long edge |

Episode N uses `Seed + N`, so any episode can be replayed exactly. Before an episode starts, the layout is checked: every cup must
be pickable, every spot reachable, and the robot's own planner must find a route for each drive (start, counter, table, counter, ...).
An impossible layout is reshuffled (up to 10 tries).

## Results

Cafe cup, controller grasp settings untouched. One run per seed, same seeds replay identically.

| Version | Seeds | Episodes fully delivered | Cups delivered |
|---|---|---|---|
| 8 x 6 m room, fixed counter and table | 101-106, 201-206 | 10 / 12 | 34 / 36 |
| 8 x 6 m room, random counter and table | 12 episodes | 12 / 12 | 36 / 36 |
| **12 x 9 m room with windows (current scene)** | 101-103 | 2 / 3 | 7 / 9 |
| Same scene, recorded with video and lidar | 101-103 | 2 / 3 | 7 / 9 (identical to the unrecorded run: delivered cups land within 1.1-1.8 cm of their spots) |

The misses seen so far are a cup that did not rise with the hand and a cup that fell over on release (81-106 degrees). Nine cups
in the current room is a small sample, so I would not read the 7 / 9 as a rate.

## Recording

- One Observer episode per tidy-up, with a success flag and the task label "pick up the cups from the counter and place them side by
  side on the dining table". Reset and reshuffle happen between episodes, so they are not in any clip.
- Camera: `cam_overview`, 1280 x 720, 50 fps, fixed above the room (rasterised).
- Robot data: actuator commands and joint positions at 50 Hz, Parquet. Output goes to `DataSessions/`.
- **LiDAR:** a recorder binding (`lidar_head`, in the scene's `ObserverConfig.Lidars`) on the G1's `head_link`: 16 channels x 360
  azimuth bins, 10 Hz, +-15 degrees vertical, 0.1-20 m. `head_link`'s frame origin sits at waist height (about 0.8 m), not at the head,
  so the mount offset is (0.05, 0, 0.55): that puts the sensor on top of the head. At a 5 cm offset it sat inside the torso and
  saw only the back half of the room. Output is `lidar-lidar_head-000.parquet` plus a `.json` sidecar with the sensor settings and column
  schema; columns: timestamp, azimuth bin, channel, range (-1 = no hit), geom id, hit, hit normal (sensor frame).
- Checked on a 3-episode run (seed 101, replayable): per episode 8,489-8,735 frames of robot data at 50 Hz, 9.8-10.1 million lidar
  rows (about 1,700 sweeps of 5,760 rays), 94-95% of rays hit, hit ranges 0.2-13 m, and the success flags in `meta/episodes` match the
  controller's log (fail, success, success). One quirk: in episode 2 the lidar file ends with about 10 samples stamped 7 s after the
  last robot frame.
- Recording slows the simulation to about a third of real time.

## Using this repository

This holds my work only: the scene, the episode and randomisation code, the build tools, the patch and this README. It does not
include Lucky Robots' RobotCafe assets (the G1 robot, the cafe props and their textures) or the cafe controller source, which the
scene and `TidyWaiter` depend on. To rebuild:

1. Put this folder next to a copy of the RobotCafe project, and name it `G1RoomNav` (the tools expect `<parent>/RobotCafe` and `<parent>/G1RoomNav`).
2. Copy `RobotCafe/Assets/Scripts/Client/Source/CafeRobot/` into `Assets/Scripts/Client/Source/CafeRobot/` (leave out `Cinematics/`),
   and apply `RobotCafe-changes.patch` to it.
3. Copy `RobotCafe/Assets/Robots/UnitreeG1` and `RobotCafe/Assets/Cafe` into `Assets/`. `Tools/make_tidy_base.py` does this and
   rewrites the base scene; the checked-in `Assets/Scenes/Tidy.hscene` is the finished result and refers to those assets.
4. Open `G1RoomNav.hproj` in Lucky Engine and let it build the scripts.

## Run it

1. Open `G1RoomNav.hproj` in Lucky Engine, scene `Tidy`.
2. To record: sign in to LuckyHub (Profile button, bottom of the side bar). Without it the episodes run but nothing is recorded.
3. Press Play. `Seed`, `MaxEpisodes` (0 = endless) and `Record` are fields on the `UnitreeG1` entity's `TidyWaiter` script.

From the command line (editor open on the project): `Tools\Run-TidyTest.ps1 -Episodes 3 -Seed 101 -NoRecord` plays episodes
and prints the `[Tidy]` log.

## How it is built

The robot, its walk and pick-and-place controller are Lucky Robots' RobotCafe example (`Cafe.Robotics`). That code and its assets are
**not in this repository**; see "Using this repository". The episode logic is new:

| File | Job |
|---|---|
| [TidyWaiter.cs](Assets/Scripts/Client/Source/CafeRobot/TidyWaiter.cs) | Episode loop around the cafe's `DeliverCup`: reset, shuffle, check the layout and routes, deliver each cup to its spot, score, record |
| [LayoutRandomizer.cs](Assets/Scripts/Client/Source/Navigation/LayoutRandomizer.cs) | Random non-overlapping layouts; counter against a wall, table anywhere clear; poses written into MuJoCo free joints |
| [EdgeLayout.cs](Assets/Scripts/Client/Source/Navigation/EdgeLayout.cs) | Points and delivery spots along a counter or table edge |
| [FreeBodyPlacer.cs](Assets/Scripts/Client/Source/Navigation/FreeBodyPlacer.cs) | Writes a free body's pose (qpos) |
| [PropFootprint.cs](Assets/Scripts/Client/Source/Navigation/PropFootprint.cs) | Measures a prop's real footprint from its collider shapes (the cafe props are scale-1 meshes, so their transform says nothing about size) |
| [NavGrid.cs](Assets/Scripts/Client/Source/Navigation/NavGrid.cs), [Frames.cs](Assets/Scripts/Client/Source/Navigation/Frames.cs) | Clearance map, A* and world-frame helpers, adapted from the RobotCafe example (included so the files above compile) |

Two small changes to the cafe code are in [RobotCafe-changes.patch](RobotCafe-changes.patch): `GraspTuning.SlideTime` (was hard-coded)
and `ApproachPlanner.CorridorIgnoredBodies` (the planner's reach check treats each collider as a bounding sphere, so a long
single-box wall always looked in the way).

### Furniture bounds

The randomiser does not read the walls. `RoomMin` / `RoomMax` on `TidyWaiter` (X and Z) set the box the furniture goes in,
currently (-5.9, -4.4) to (5.9, 4.4), just inside the walls at +-6.0 x +-4.5. Move the walls and these must be changed by hand.

### The room

The four `Wall_*` boxes and `Ground` are the collision, with their meshes removed. The visible walls are separate visual-only
pieces: wall sections around each window opening, frames, ledges, a translucent glass pane, an interior floor and an outdoor lawn.
So the robot and the lidar treat each window as solid wall.

### Scene tooling (`Tools/`)

| Script | Job |
|---|---|
| `make_tidy_base.py` | Carries the cafe G1 and cafe props into this project and writes the base scene |
| `Build-TidyScene.ps1` | Builds the first room, counter, lights and camera in the open editor, attaches `TidyWaiter` |
| `swap_items_to_cup.py` | Replaces the three test blocks with the cafe's cups |
| `Build-Room.ps1` | Rebuilds the room as 12 x 9 m with windows |
| `Group-TidyScene.ps1` | Groups the hierarchy: `Environment`, `Randomised`, `Objectives`, `Lighting`, `Cameras` |
| `Run-TidyTest.ps1` | Play N episodes in the open editor and print the `[Tidy]` log |
| `call.ps1`, `mcp.ps1` | Call the editor's local MCP server from a shell |

## Limits

- The cup grasp is the cafe controller's, tuned for its `ToGoCup`; a different object shape (I tried a tapered block) needs new grasp
  tuning, and the set-down is where it fails.
- The robot always starts at (-3.0, 0, 0.5). Furniture, counter, table and cups are randomised; the robot is not.
- Windows are solid to the lidar (see "The room").
- Cups are placed side by side. Stacking is not attempted.
- The dataset's `meta/info.json` labels the robot `ur5e` (the recorder's default), not a G1.
- The G1 walker is a flat-floor policy: it climbs about 4.5 cm and falls on anything higher, so there are no stairs.
- Rendering is rasterised on a GTX 960 (no ray tracing).
