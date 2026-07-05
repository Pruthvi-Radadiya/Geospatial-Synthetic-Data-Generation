# Demo media

Screenshots from the local Unity project (`RealSceneGen`). Capture during **Play mode** with Cesium tiles loaded.

| File | Description |
|------|-------------|
| `chasecamera.png` | Chase camera over streamed photorealistic 3D tiles |
| `egocam.png` | Ego (first-person) sensor camera viewpoint |

## Optional captures (recommended before application)

### Green road proxy (vector-to-surface fusion)

1. Enter Play mode; wait ~15 s for Cesium colliders + OSM proxy.
2. Enable **Show Debug Mesh** on `RoadDrivingProxy`.
3. Screenshot Game view → save as `chase-proxy-debug.png`.

### Perception visualization (if PerceptionCamera enabled locally)

1. Enable `PerceptionCamera` on EgoCam in your local scene only.
2. Turn on depth or segmentation visualization in Perception settings.
3. Capture 1 frame → save as `perception-depth.png` or `perception-segmentation.png`.
4. Label in README as **WIP capture pipeline** — do not claim full dataset export.

Do **not** add LiDAR screenshots unless you implement LiDAR in the project.
