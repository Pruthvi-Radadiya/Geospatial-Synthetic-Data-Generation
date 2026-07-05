# Demo media

Screenshots and recordings come from the local Unity project **`RealSceneGen`** (Plastic SCM). Each pipeline lives on a **different branch** — switch before capturing.

| Plastic branch | Pipeline | Suggested files |
|---|---|---|
| `/main/using-cesium-map` | Cesium photogrammetry | `cesium-*.png`, `cesium-driving.mp4` |
| `/main` | Procedural OSM | `procedural-*.png`, `procedural-walkthrough.mp4` |

## How to switch branch

**In Unity:** Version Control window → **Branches** → select branch → **Switch to this branch**

**In terminal (from project folder):**
```powershell
cd d:\Unity\RealSceneGen
cm switch br:/main                      # procedural OSM
cm switch br:/main/using-cesium-map     # Cesium
```

Re-open the scene if Unity prompts you. API keys stay in your local Inspector — they are not in this GitHub repo.

---

## Cesium branch (`/main/using-cesium-map`)

Already captured:

| File | Description |
|------|-------------|
| `chasecamera.png` | Chase camera over streamed photorealistic 3D tiles |
| `egocam.png` | Ego (first-person) sensor camera viewpoint |

**To add (recommended):**

1. Switch to `br:/main/using-cesium-map`, enter **Play**, wait ~15 s for tiles + OSM proxy.
2. **Proxy debug mesh:** enable **Show Debug Mesh** on `RoadDrivingProxy` → screenshot → `cesium-proxy-debug.png`
3. **Screen recording:** 30–60 s driving on roads → `cesium-driving.mp4` (or GIF)
4. **Perception (optional):** enable `PerceptionCamera` locally → capture depth/segmentation HUD → `cesium-perception-depth.png`

---

## Procedural OSM branch (`/main`)

**You must switch to this branch** — these scenes are not visible on the Cesium branch.

1. `cm switch br:/main` (or use Unity Version Control UI).
2. Open the main scene, enter **Play**, walk/drive with **WASD**.
3. Wait for terrain + buildings + roads to generate (~10–20 s after moving).

Suggested captures:

| File | What to show |
|------|-------------|
| `procedural-overview.png` | Elevated view: terrain mesh + extruded OSM buildings |
| `procedural-roads.png` | Road strips snapped to terrain |
| `procedural-streetview.png` | Street View textures on building facades (if visible) |
| `procedural-walkthrough.mp4` | Short clip moving through generated city block |

Save all files in this folder (`docs/demo/`), then uncomment the matching blocks in the root `README.md`.

---

## What not to add

- LiDAR point clouds (not implemented in either branch)
- Full Unity Perception Solo dataset exports (too large for GitHub)
- Scenes or files containing API keys
