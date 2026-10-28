# Demo material

| File | What it is |
|---|---|
| `tidy_demo.mp4` | 87 s at 2x speed: the overview camera (left) and the head lidar drawn top-down (right) for one full episode. Base seed 101, episode 3, 3/3 cups delivered. |
| `preview.png` | A frame from the video |
| Dataset (not in the repository: [download `sample_session.zip` from the v1.0 release](https://github.com/AlexandreHurbe/G1-Tidy-LuckyEngine/releases/tag/v1.0), about 190 MB, or regenerate it, see below) | The recorded session: 3 episodes of robot data (Parquet), overview video (MP4) and lidar (Parquet plus a JSON sidecar per episode). Episode 1 failed (2 cups toppled on release), episodes 2 and 3 succeeded; the success flag is in `meta/episodes`. |

The video is drawn from `lidar-lidar_head-002.parquet` and `videos/observation.images.cam_overview/chunk-000/file-002.mp4` (episode 3).
Recorded with `Tools\Run-TidyTest.ps1 -Episodes 3 -Seed 101`, which replays the same three layouts every time. Running it in Lucky
Engine (signed in to LuckyHub) writes the dataset to `DataSessions/`; recording runs at about a third of real time, so expect
roughly half an hour for the three episodes.

