# Geospatial Synthetic Data Generation

Portfolio project demonstrating **geospatial data ingestion, tiling, and transformation** into simulation-ready 3D environments. Two complementary pipelines show how raw map and imagery sources can be preprocessed at runtime and converted into structured geometry anchored to real-world coordinates.

1. **Procedural OSM Pipeline** - ingests elevation rasters, street-level imagery, and OSM vector data; tiles and transforms them into terrain, buildings, and road meshes.
2. **Cesium Photogrammetry Pipeline** - streams satellite-derived 3D Tiles and captures structured sensor output from photorealistic globe geometry.

> **Note:** This is a **scripts-only portfolio repo** (Unity C#). The full Unity project (scenes, HDRP assets, Cesium configuration) is intentionally excluded. A separate Python/GDAL production stack is not shown here - see [Relevance to Data Plane Engineering](#relevance-to-data-plane-engineering) for how this maps to pipeline-oriented roles.

---

## Overview

Geospatial ML platforms need reliable preprocessing: ingest heterogeneous sources, tile them consistently, transform coordinates correctly, and produce downstream-ready outputs. Manual authoring does not scale across continents of satellite, aerial, and map data.

This project prototypes that problem in a simulation context. The agent's position is tracked in **WGS-84 GPS**, and the surrounding environment is either **reconstructed from APIs and vector map data** or **streamed from photogrammetric 3D Tiles**. Both pipelines use **event-driven, tile-based processing** with queueing, stale-response guards, and retry logic - patterns that transfer directly to high-throughput imagery pipelines.

**Default demo locations**

| Pipeline | Default coordinates | Location |
|---|---|---|
| Procedural OSM | 48.8584, 2.2945 | Paris (Eiffel Tower area) |
| Cesium Photogrammetry | 48.893697, 8.694218 | Germany (Karlsruhe region) |

---

## Two Approaches Compared

| | Procedural OSM Pipeline | Cesium Photogrammetry Pipeline |
|---|---|---|
| **Data source** | Google Elevation API, Google Street View API, Overpass/OSM | Cesium ion 3D Tiles (photogrammetry mesh) |
| **World generation** | Runtime mesh building from API responses | Pre-authored globe tiles streamed by Cesium |
| **Visual fidelity** | Stylized procedural geometry; Street View textures on facades | Photorealistic real-world geometry |
| **Geospatial math** | Manual WGS-84 / Haversine conversion in `GPSTracker` | `CesiumGlobeAnchor` handles Earth curvature |
| **Agent control** | `AgentMover` (keyboard, terrain-snapped) | `VehicleController` (rigidbody car physics, globe-aware) |
| **Output products** | Terrain mesh, building footprints (extruded), road polylines (meshed), facade textures | Photorealistic mesh tiles, perception capture frames |
| **External APIs** | Google Maps (key required), Overpass (public) | Cesium ion token (in full project, not in this repo) |
| **Scripts in this repo** | 7 | 3 |

### When to use which

- **Procedural OSM** scales to any GPS coordinate where Google and OSM have coverage, with full control over mesh generation and a lightweight dependency footprint (HTTP + JSON parsing only).
- **Cesium Photogrammetry** prioritizes alignment to real-world photogrammetric geometry - closer to satellite/aerial 3D reconstruction workflows - at the cost of Cesium streaming infrastructure.

---

## Relevance to Data Plane Engineering

This repo is a **Unity/C# prototype**, not a production Python/GDAL service. It is included in my portfolio because it demonstrates geospatial pipeline thinking that overlaps with preprocessing and product-generation work:

| Data Plane concern | How this project demonstrates it |
|---|---|
| **Tiling & streaming** | 50 m spatial tiles trigger fetch → transform → rebuild cycles as the agent moves |
| **Raster ingestion** | Google Elevation API grids (batched 9×9 samples) drive terrain mesh generation |
| **Vector ingestion** | Overpass/OSM queries for building footprints and highway centerlines |
| **Coordinate systems** | WGS-84 conversions with latitude-dependent longitude scaling; Cesium globe anchoring |
| **Format interoperability** | JSON (Google, Overpass) → internal structures → 3D meshes and textures |
| **Pipeline resilience** | Fetch queues, stale-response discard, 3× retry on Overpass failures |
| **Memory-conscious updates** | Previous tile geometry destroyed before rebuilding (streaming, not accumulating) |
| **3D-ready outputs** | Extruded building meshes, road strips snapped to terrain, photogrammetry tile streaming |

**What this repo does not cover** (but appears in my professional background): Python/FastAPI services, GDAL/rasterio/GeoTIFF/COG processing, PostgreSQL, Redis worker pools, GeoPackage export. Those skills come from production work at understand.ai (ETL importers, calibration validation, MongoDB-backed pipelines) and are described on my CV.

---

## Architecture

### Procedural OSM Pipeline

Tile-based streaming: every 50 m the agent moves, `GPSTracker` fires `OnNewTileEntered`, which triggers a coordinated fetch-and-rebuild cycle.

```mermaid
flowchart TD
    AgentMover[AgentMover] --> GPSTracker
    GPSTracker -->|"OnNewTileEntered(lat, lng)"| TileFetcher
    GPSTracker -->|"OnNewTileEntered"| BuildingGenerator
    TileFetcher -->|"OnElevationGridReady"| TerrainGenerator
    TileFetcher -->|"OnStreetViewReady"| StreetViewRenderer
    TileFetcher -->|"OnStreetViewReady"| BuildingGenerator
    TerrainGenerator -->|"OnTerrainRebuilt"| BuildingGenerator
    BuildingGenerator -->|"OnBuildingsFetched"| RoadNetworkRenderer
    TileFetcher --> GoogleElevation[Google Elevation API]
    TileFetcher --> GoogleSV[Google Street View API]
    BuildingGenerator --> OverpassB[Overpass API - buildings]
    RoadNetworkRenderer --> OverpassR[Overpass API - roads]
```

**Event chain per tile**

1. `GPSTracker` detects 50 m movement → fires `OnNewTileEntered`.
2. `TileFetcher` queues the tile, fetches elevation grid + 8 Street View headings (0°–315°).
3. `TerrainGenerator` rebuilds a streaming mesh from the elevation grid and snaps the agent.
4. `BuildingGenerator` queries OSM buildings, waits for terrain sync, extrudes footprints, textures facades from the nearest Street View heading.
5. `RoadNetworkRenderer` fetches OSM highways after buildings complete, builds asphalt strips snapped to terrain.
6. `StreetViewRenderer` builds a hidden 360° photo ring (texture source for buildings).

**Resilience patterns**

- Fetch queue in `TileFetcher` prevents overlapping API calls when the agent moves quickly.
- Stale-response guards in `BuildingGenerator` and `RoadNetworkRenderer` discard results if the agent has already moved to a new tile.
- Retry logic (3 attempts) on Overpass API failures.

### Cesium Photogrammetry Pipeline

Globe-anchored driving with a dedicated perception capture camera.

```mermaid
flowchart TD
    VehicleController[VehicleController] --> GPSTrackerC[GPSTracker]
    GPSTrackerC --> CesiumAnchor[CesiumGlobeAnchor]
    CesiumAnchor --> CesiumTiles[Cesium 3D Tiles]
    EgoCameraController --> PerceptionCam[Unity PerceptionCamera]
    EgoCameraController --> RenderTexture[HDRP RenderTexture capture]
    VehicleController -->|"raycast spawn + fall safety"| CesiumTiles
    ChaseCamera[ChaseCamera - full project] -->|"ShowOnScreen / HideFromScreen"| EgoCameraController
```

**Key behaviors**

- `GPSTracker` reads lat/lng/height from `CesiumGlobeAnchor` instead of manual Haversine math.
- `VehicleController` stays kinematic until a downward raycast hits loaded Cesium colliders, then enables physics. Includes fall-through recovery during LOD tile swaps.
- `EgoCameraController` renders to an HDRP `RenderTexture` for background dataset capture while a cloned screen-view camera can be toggled on/off. Uses reflection to bypass Unity Perception's hardcoded screen blit so the chase camera and capture camera do not conflict.

---

## Script Reference

### `Procedural_OSM_Pipeline/` (7 scripts)

| Script | Responsibility |
|---|---|
| `GPSTracker.cs` | Tracks agent movement in WGS-84 coordinates using Haversine math. Fires `OnNewTileEntered` every `tileSizeMetres` (default 50 m). Provides `GpsToUnity()` for other scripts. |
| `TileFetcher.cs` | Central API hub. Queues tile fetches; calls Google Elevation (batched grid) and Street View (8 headings). Detects grey "no imagery" placeholders. Fires `OnElevationGridReady`, `OnStreetViewReady`. |
| `TerrainGenerator.cs` | Builds a streaming `Mesh` + `MeshCollider` from elevation grids. Positions terrain at the tile's GPS location. Snaps agent after physics update. |
| `BuildingGenerator.cs` | Fetches OSM building footprints via Overpass. Extrudes walls/roof meshes, applies procedural palette + Street View facade textures. Skips colliders if agent is inside bounds. |
| `RoadNetworkRenderer.cs` | Fetches drivable OSM highways. Builds road strips with type-dependent widths, snapped to terrain height. Chains off `BuildingGenerator.OnBuildingsFetched`. |
| `StreetViewRenderer.cs` | Listens to `OnStreetViewReady`, builds a ring of photo panels (hidden; used as texture pipeline). |
| `AgentMover.cs` | WASD/arrow keyboard movement with terrain raycast snapping. Drives the procedural pipeline agent. |

### `Cesium_Photogrammetry_Pipeline/` (3 scripts)

| Script | Responsibility |
|---|---|
| `GPSTracker.cs` | Cesium-backed GPS tracker. Sets initial position on `CesiumGlobeAnchor`, reads live coordinates each frame, fires `OnNewTileEntered` on 50 m movement. |
| `VehicleController.cs` | Rigidbody vehicle with procedural car mesh, wheel animation, globe-aware steering (yaw rate from bicycle model), slope alignment, spawn snapping, and fall-through safety. |
| `EgoCameraController.cs` | Perception sensor camera. HDRP RenderTexture capture, dynamic screen clone, reflection-based Perception blit bypass, integration with chase camera toggle. |

---

## Key Technical Highlights

**Geospatial preprocessing**

- WGS-84 coordinate conversion with latitude-dependent longitude scaling (`cos(lat)` correction).
- Tile-based streaming architecture decoupled via C# events - fetchers and renderers are independently composable stages.
- Elevation grid batching (up to 81 points per API call) to reduce request overhead.

**Data ingestion & validation**

- Overpass QL queries for OSM buildings and highways with retry and stale-response handling.
- Street View grey-placeholder detection to filter invalid imagery before downstream use.
- Custom JSON parsing without external dependencies (self-contained portfolio scripts).

**Geometry generation (postprocessing analogue)**

- Runtime mesh generation for terrain, extruded building footprints (wall + roof sub-meshes), and road strips.
- Ground-height raycasting ensures vector features conform to underlying elevation.
- Street View heading selection for facade texturing based on building centroid bearing.

**Photogrammetry & capture**

- Cesium 3D Tile streaming with globe-anchored positioning.
- Unity Perception integration with HDRP RenderTexture capture and custom render-pipeline hooks.

---

## Setup Notes

These scripts were developed in **Unity 6** (6000.3.7f1) with **HDRP 17.3.0**.

### Required packages (full Unity project)

| Package | Used by |
|---|---|
| `com.unity.render-pipelines.high-definition` | Both pipelines |
| `com.unity.inputsystem` | `AgentMover`, `VehicleController` |
| `com.unity.perception` | `EgoCameraController` |
| Cesium for Unity | Cesium pipeline (`CesiumGlobeAnchor`) |

### API keys

- **Google Maps Platform** - enable Elevation API and Street View Static API. Set `apiKey` on `TileFetcher` (placeholder: `YOUR_API_KEY_HERE`). Never commit real keys.
- **Overpass API** - public endpoint (`overpass-api.de`); no key required. Respect rate limits.
- **Cesium ion** - configured in the full Unity project, not in these standalone scripts.

### Scene wiring (high level)

**Procedural OSM**

1. Add `GPSTracker` + `AgentMover` to the agent GameObject.
2. Add `TileFetcher`, `TerrainGenerator`, `BuildingGenerator`, `RoadNetworkRenderer`, `StreetViewRenderer` to scene managers.
3. Assign `agentTransform` on `TerrainGenerator`.
4. Set initial GPS in `GPSTracker` Inspector.

**Cesium Photogrammetry**

1. Set up Cesium georeference + 3D Tileset in the full project.
2. Add `CesiumGlobeAnchor` + `GPSTracker` + `VehicleController` to the vehicle.
3. Add `PerceptionCamera` + `EgoCameraController` to the ego sensor camera.
4. Wire chase camera `ShowOnScreen` / `HideFromScreen` calls (see full project).

### Controls

- **WASD / Arrow keys** - move agent (procedural) or drive vehicle (Cesium).

---

## Demo / Screenshots

<!-- Add your demo media here, for example:
![Procedural OSM demo](docs/procedural-demo.gif)
![Cesium capture](docs/cesium-capture.png)
[YouTube walkthrough](https://youtube.com/...)
-->

_Demo GIFs and screenshots can be added to a `docs/` folder and linked above._

---

## Repository Structure

```
Geospatial-Synthetic-Data-Generation/
├── README.md
├── .gitignore
├── Procedural_OSM_Pipeline/
│   ├── GPSTracker.cs
│   ├── TileFetcher.cs
│   ├── TerrainGenerator.cs
│   ├── BuildingGenerator.cs
│   ├── RoadNetworkRenderer.cs
│   ├── StreetViewRenderer.cs
│   └── AgentMover.cs
└── Cesium_Photogrammetry_Pipeline/
    ├── GPSTracker.cs
    ├── VehicleController.cs
    └── EgoCameraController.cs
```

---

## What Is Not Included

- Full Unity project, scenes, materials, and prefabs
- Cesium ion configuration and tileset assets
- Unity Perception dataset configuration (labelers, serializers)
- API keys or tokens

---

## Author

**Pruthvi Radadiya** - Software Engineer (Perception & Data Pipelines)

- GitHub: [@Pruthvi-Radadiya](https://github.com/Pruthvi-Radadiya)
- LinkedIn: [pruthvi-radadiya](https://www.linkedin.com/in/pruthvi-radadiya)
- Location: Pforzheim, Germany

**Background:** Production ETL and data-quality pipelines for autonomous-driving datasets (understand.ai). This independent project extends that geospatial interest into runtime environment generation from map and photogrammetry sources.
