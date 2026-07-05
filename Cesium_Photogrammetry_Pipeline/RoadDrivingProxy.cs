using UnityEngine;
using UnityEngine.Networking;
using CesiumForUnity;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;

public class RoadDrivingProxy : MonoBehaviour
{
    public static event Action OnProxyReady;

    public static bool TryGetSpawnHint(out Vector3 position, out Vector3 normal)
    {
        position = _lastSpawnHintPosition;
        normal = _lastSpawnHintNormal;
        return _hasLastSpawnHint;
    }

    private static Vector3 _lastSpawnHintPosition;
    private static Vector3 _lastSpawnHintNormal;
    private static bool _hasLastSpawnHint;

    [Header("Fetch Settings")]
    public float fetchRadius = 200f;

    [Header("Physics Proxy")]
    [Tooltip("Default total width of invisible driving strip in metres")]
    public float proxyWidth = 10f;
    public float heightOffset = 1.0f;
    [Tooltip("Max distance between height samples along each OSM link")]
    public float maxSegmentLength = 8f;
    public string proxyLayerName = "RoadProxy";

    [Header("Guard Rails")]
    public float guardRailHeight = 1.2f;
    public float guardRailThickness = 0.2f;
    [Tooltip("Radius around each junction where guard rails open for crossing traffic")]
    public float junctionClearanceRadius = 7f;

    [Header("Debug")]
    [Tooltip("Draw a semi-transparent mesh so the driving proxy is visible in Game view")]
    public bool showDebugMesh = true;
    public Color debugDeckColor = new Color(0.15f, 0.85f, 0.25f, 0.45f);

    [Header("Height Sampling")]
    public LayerMask cesiumSampleMask = ~0;
    [Tooltip("Max seconds to wait for Cesium mesh colliders before building proxy height")]
    public float cesiumColliderWaitSeconds = 20f;
    [Tooltip("Height delta between samples that triggers extra subdivision")]
    public float gradeResampleThreshold = 1f;
    [Tooltip("Seconds between height refinement passes while tiles stream in")]
    public float cesiumResampleIntervalSeconds = 1.5f;
    [Tooltip("Max seconds to keep refining proxy heights after build")]
    public float cesiumResampleMaxSeconds = 30f;
    [Tooltip("Min metres agent must move from last proxy build centre before full rebuild")]
    public float proxyRebuildDistance = 100f;
    [Tooltip("Metres ahead of agent to require Cesium colliders before building")]
    public float cesiumForwardProbeDistance = 20f;
    [Tooltip("Metres around the agent to keep re-projecting proxy segments as Cesium tiles stream in")]
    public float agentRefineRadius = 150f;
    [Tooltip("Metres above the ellipsoid sample position to start the terrain-finding downward ray.\n" +
             "Must be greater than the maximum terrain elevation difference above _georeference.height in your scene.\n" +
             "500 m covers most hilly/mountainous terrain. Increase if proxy still sinks on extreme hills.")]
    public float skyProbeHeight = 500f;

    private enum HeightConfidence { Pending, HitCesium, Interpolated }

    private struct HeightSample
    {
        public double Lat;
        public double Lng;
        public Vector3 EllipsoidPos;
        public Vector3 WorldCenter;
        public Vector3 LocalUp;
        public HeightConfidence Confidence;
    }

    private class SegmentRecord
    {
        public RoadNode NodeA;
        public RoadNode NodeB;
        public float HalfWidth;
        public string RoadType;
        public int SegIndex;
        public GameObject Root;
        public readonly List<HeightSample> Samples = new List<HeightSample>();
    }

    private GameObject _proxyContainer;
    private GPSTracker _gps;
    private CesiumGeoreference _georeference;
    private CesiumGlobeAnchor _agentAnchor;
    private int _proxyLayer = -1;
    private double _latestLat;
    private double _latestLng;
    private double _proxyBuildLat;
    private double _proxyBuildLng;
    private bool _hasProxyBuildCenter;
    private float _agentGroundAlongUp;
    private bool _hasAgentGroundRef;
    private Coroutine _refineCoroutine;
    private Material _debugDeckMaterial;
    private readonly List<Vector3> _junctionWorldPositions = new List<Vector3>();
    private readonly List<Vector3> _roadCenterPoints = new List<Vector3>();
    private readonly List<SegmentRecord> _segmentRecords = new List<SegmentRecord>();
    private readonly Dictionary<long, HeightSample> _nodeHeightCache = new Dictionary<long, HeightSample>();
    private readonly HashSet<(long, long)> _segmentEdgeKeys = new HashSet<(long, long)>();
    private List<RoadData> _cachedRoads;
    private bool _hasInitialBuild;

    private class RoadData
    {
        public List<RoadNode> Nodes = new List<RoadNode>();
        public string Type = "residential";
        public int Lanes;
        public float WidthMeters;
    }

    private struct RoadNode
    {
        public long Id;
        public double Lat, Lng;
        public RoadNode(long id, double lat, double lng) { Id = id; Lat = lat; Lng = lng; }
    }

    void Awake()
    {
        _gps = FindFirstObjectByType<GPSTracker>();
        _georeference = FindFirstObjectByType<CesiumGeoreference>();
        if (_gps != null)
            _agentAnchor = _gps.GetComponent<CesiumGlobeAnchor>();

        _proxyLayer = LayerMask.NameToLayer(proxyLayerName);
        if (_proxyLayer < 0)
            Debug.LogWarning($"[RoadDrivingProxy] Layer '{proxyLayerName}' not found.");

        if (cesiumSampleMask == ~0 && _proxyLayer >= 0)
            cesiumSampleMask = ~(1 << _proxyLayer);
    }

    void OnEnable()
    {
        GPSTracker.OnNewTileEntered += HandleNewTile;
        if (_refineCoroutine == null)
            _refineCoroutine = StartCoroutine(ContinuousRefineLoop());
    }

    void OnDisable()
    {
        GPSTracker.OnNewTileEntered -= HandleNewTile;
        if (_refineCoroutine != null)
        {
            StopCoroutine(_refineCoroutine);
            _refineCoroutine = null;
        }
    }

    private void HandleNewTile(double lat, double lng)
    {
        Debug.Log($"[RoadDrivingProxy] Tile update: {lat:F6}, {lng:F6}");
        _latestLat = lat;
        _latestLng = lng;

        if (!_hasInitialBuild)
            StartCoroutine(FetchAndBuildProxy(lat, lng));
        else
            StartCoroutine(FetchAndAppendRoads(lat, lng));
    }

    private IEnumerator FetchAndBuildProxy(double lat, double lng)
    {
        string query =
            $"[out:json][timeout:25];" +
            $"(way[\"highway\"~\"^(motorway|trunk|primary|secondary|tertiary|" +
            $"residential|service|living_street|unclassified)$\"]" +
            $"(around:{fetchRadius},{lat:F6},{lng:F6}););" +
            $"out body;>;out skel qt;";

        string url = "https://overpass-api.de/api/interpreter?data=" +
                     UnityWebRequest.EscapeURL(query);

        int retries = 3;
        string responseText = null;

        for (int i = 0; i < retries; i++)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = 30;
            yield return req.SendWebRequest();

            if (lat != _latestLat || lng != _latestLng)
                yield break;

            if (req.result == UnityWebRequest.Result.Success)
            {
                responseText = req.downloadHandler.text;
                break;
            }

            Debug.LogWarning($"[RoadDrivingProxy] Overpass failed (attempt {i + 1}/{retries}): {req.error}");
            if (i < retries - 1)
                yield return new WaitForSeconds(2f);
        }

        if (string.IsNullOrEmpty(responseText))
        {
            Debug.LogWarning($"[RoadDrivingProxy] Giving up after {retries} attempts.");
            yield break;
        }

        if (lat != _latestLat || lng != _latestLng)
            yield break;

        var (roads, junctionNodeIds) = ParseRoads(responseText);
        Debug.Log($"[RoadDrivingProxy] Fetched {roads.Count} roads and {junctionNodeIds.Count} junctions at {lat:F6}, {lng:F6}");

        yield return WaitForCesiumColliders();

        if (lat != _latestLat || lng != _latestLng)
            yield break;

        BuildJunctionPositions(roads, junctionNodeIds);
        yield return BuildProxyMeshesCoroutine(roads, lat, lng);
    }

    private IEnumerator FetchAndAppendRoads(double lat, double lng)
    {
        string query =
            $"[out:json][timeout:25];" +
            $"(way[\"highway\"~\"^(motorway|trunk|primary|secondary|tertiary|" +
            $"residential|service|living_street|unclassified)$\"]" +
            $"(around:{fetchRadius},{lat:F6},{lng:F6}););" +
            $"out body;>;out skel qt;";

        string url = "https://overpass-api.de/api/interpreter?data=" +
                     UnityWebRequest.EscapeURL(query);

        string responseText = null;
        for (int i = 0; i < 3; i++)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = 30;
            yield return req.SendWebRequest();

            if (lat != _latestLat || lng != _latestLng)
                yield break;

            if (req.result == UnityWebRequest.Result.Success)
            {
                responseText = req.downloadHandler.text;
                break;
            }

            if (i < 2)
                yield return new WaitForSeconds(2f);
        }

        if (string.IsNullOrEmpty(responseText) || lat != _latestLat || lng != _latestLng)
            yield break;

        if (_proxyContainer == null)
            yield break;

        yield return WaitForCesiumNearAgent();

        var (roads, junctionNodeIds) = ParseRoads(responseText);
        MergeCachedRoads(roads);
        MergeNodeHeights(roads);
        BuildJunctionPositions(roads, junctionNodeIds);

        int added = 0;
        foreach (var road in roads)
        {
            float halfW = GetRoadHalfWidth(road);
            for (int i = 0; i < road.Nodes.Count - 1; i++)
            {
                if (lat != _latestLat || lng != _latestLng)
                    yield break;

                if (!TryAddOSMLink(road.Nodes[i], road.Nodes[i + 1], halfW, road.Type, i))
                    continue;

                added++;
                if (added % 8 == 0)
                    yield return null;
            }
        }

        _proxyBuildLat = lat;
        _proxyBuildLng = lng;
        RefineNearAgent();

        if (added > 0)
            Debug.Log($"[RoadDrivingProxy] Appended {added} road segments around {lat:F4}, {lng:F4}. Total segments: {_segmentRecords.Count}.");
    }

    private IEnumerator BuildProxyMeshesCoroutine(List<RoadData> roads, double lat, double lng)
    {
        GameObject oldContainer = _proxyContainer;

        _proxyContainer = new GameObject($"RoadProxy_{lat:F4}_{lng:F4}");
        if (_georeference != null)
            _proxyContainer.transform.SetParent(_georeference.transform, false);

        _roadCenterPoints.Clear();
        _segmentRecords.Clear();
        _nodeHeightCache.Clear();
        _segmentEdgeKeys.Clear();
        _cachedRoads = roads;
        PreResolveNodeHeights(roads);
        int segmentCount = 0;

        foreach (var road in roads)
        {
            float halfW = GetRoadHalfWidth(road);
            for (int i = 0; i < road.Nodes.Count - 1; i++)
            {
                if (lat != _latestLat || lng != _latestLng)
                    yield break;

                if (!TryAddOSMLink(road.Nodes[i], road.Nodes[i + 1], halfW, road.Type, i))
                    continue;

                segmentCount++;
                yield return null;
            }
        }

        ApplyNodeCacheToAllSegments(nearAgentOnly: false);

        if (oldContainer != null)
            Destroy(oldContainer);

        if (segmentCount == 0 && _segmentRecords.Count == 0)
        {
            Debug.LogWarning("[RoadDrivingProxy] No road segments found — check Overpass results or coordinate conversion.");
            yield break;
        }

        if (segmentCount == 0)
            Debug.Log("[RoadDrivingProxy] No Cesium-projected deck yet — driving on Cesium until tiles stream in.");

        _proxyBuildLat = lat;
        _proxyBuildLng = lng;
        _hasProxyBuildCenter = true;

        int pending = CountPendingSamples();
        Debug.Log($"[RoadDrivingProxy] Built proxy mesh for tile {lat:F4}, {lng:F4} ({pending} samples pending refinement).");

        RefineNearAgent();
        yield return NotifyProxyReady();

        _hasInitialBuild = true;
    }

    private IEnumerator WaitForCesiumColliders()
    {
        float elapsed = 0f;
        _hasAgentGroundRef = false;

        while (elapsed < cesiumColliderWaitSeconds)
        {
            if (_agentAnchor != null)
            {
                Vector3 up = _agentAnchor.transform.up;
                double refHeight = GetReferenceEllipsoidHeight();
                double agentLat = _agentAnchor.longitudeLatitudeHeight.y;
                double agentLng = _agentAnchor.longitudeLatitudeHeight.x;

                Vector3 agentPos = GpsToUnity(agentLat, agentLng, refHeight);

                // Probe from sky so hills don't put the origin underground
                Vector3 agentSkyProbe = agentPos + up * skyProbeHeight;
                bool agentHit = TryGetRoadSurfaceHitFrom(agentSkyProbe, up, out RaycastHit agentGroundHit);

                if (agentHit)
                {
                    _agentGroundAlongUp = Vector3.Dot(agentGroundHit.point - agentPos, up);
                    _hasAgentGroundRef = true;
                }

                Vector3 forward = Vector3.ProjectOnPlane(_agentAnchor.transform.forward, up).normalized;
                Vector3 aheadPos = agentPos + forward * cesiumForwardProbeDistance;
                Vector3 aheadUp = GetLocalUp(aheadPos);
                Vector3 aheadSkyProbe = aheadPos + aheadUp * skyProbeHeight;
                bool aheadHit = TryGetRoadSurfaceHitFrom(aheadSkyProbe, aheadUp, out _);

                if (agentHit && aheadHit)
                    yield break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        Debug.LogWarning("[RoadDrivingProxy] Timed out waiting for Cesium colliders — proxy segments will build as tiles load.");
    }

    private IEnumerator WaitForCesiumNearAgent(float maxSeconds = 4f)
    {
        float elapsed = 0f;
        while (elapsed < maxSeconds)
        {
            if (_agentAnchor != null)
            {
                Vector3 up = _agentAnchor.transform.up;
                Vector3 pos = _agentAnchor.transform.position;
                if (TryGetRoadSurfaceHit(pos, up, out _))
                    yield break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }
    }

    // Window used by the live vehicle/agent ground-detection probe (TryGetRoadSurfaceHit).
    // Keep these small — the vehicle is always near the ground surface.
    private const float GroundWindowAboveMetres = 10f;
    private const float GroundWindowBelowMetres = 30f;

    private double GetReferenceEllipsoidHeight()
    {
        if (_georeference != null)
            return _georeference.height;
        return _gps != null ? _gps.initialHeight : 400.0;
    }

    /// Live vehicle/agent ground probe: fires from a short window above <paramref name="samplePos"/>.
    /// Used for vehicle ground detection and Cesium-collider-ready checks.
    /// Kept independent with small windows so false hits from distant terrain are avoided.
    private bool TryGetRoadSurfaceHit(Vector3 samplePos, Vector3 localUp, out RaycastHit bestHit)
    {
        bestHit = default;
        RaycastHit[] hits = Physics.RaycastAll(
            samplePos + localUp * (GroundWindowAboveMetres + 5f),
            -localUp,
            GroundWindowAboveMetres + GroundWindowBelowMetres + 10f,
            cesiumSampleMask);

        float bestAlongUp = -GroundWindowBelowMetres;
        bool found = false;

        foreach (RaycastHit h in hits)
        {
            if (_proxyLayer >= 0 && h.collider.gameObject.layer == _proxyLayer) continue;
            if (_gps != null && h.collider.transform.IsChildOf(_gps.transform)) continue;

            float alongUp = Vector3.Dot(h.point - samplePos, localUp);
            if (alongUp > GroundWindowAboveMetres || alongUp < -GroundWindowBelowMetres) continue;
            if (alongUp <= bestAlongUp) continue;

            bestAlongUp = alongUp;
            bestHit = h;
            found = true;
        }

        return found;
    }

    /// <summary>
    /// Sky-probe terrain finder: fires downward from an explicitly elevated
    /// <paramref name="probeOrigin"/> to guarantee the ray starts above hilly terrain.
    /// Used only during proxy mesh build — NOT for live vehicle ground detection.
    /// <para>
    /// A minimum-depth guard (2 m below probe) prevents false hits from Cesium tiles
    /// that are temporarily at an unexpected altitude during LOD transitions on second play.
    /// </para>
    /// </summary>
    private bool TryGetRoadSurfaceHitFrom(Vector3 probeOrigin, Vector3 localUp, out RaycastHit bestHit)
    {
        bestHit = default;
        // Ray extends (skyProbeHeight + 600 m) downward:
        //   skyProbeHeight m to get back to refHeight, then 600 m further for deep valleys.
        float rayLength = skyProbeHeight + 600f;
        RaycastHit[] hits = Physics.RaycastAll(
            probeOrigin,
            -localUp,
            rayLength,
            cesiumSampleMask);

        float bestAlongUp = float.MinValue;
        bool found = false;

        foreach (RaycastHit h in hits)
        {
            if (_proxyLayer >= 0 && h.collider.gameObject.layer == _proxyLayer) continue;
            if (_gps != null && h.collider.transform.IsChildOf(_gps.transform)) continue;

            float alongUp = Vector3.Dot(h.point - probeOrigin, localUp);

            // CRITICAL guard: reject hits less than 2 m below the probe origin.
            // On the second Unity play-session, Cesium can briefly have tile colliders
            // at an unexpected high altitude (LOD transition), causing a hit near the
            // probe itself.  That would place WorldCenter ~skyProbeHeight above terrain.
            if (alongUp > -2f) continue;
            if (alongUp < -rayLength) continue;
            if (alongUp <= bestAlongUp) continue;

            bestAlongUp = alongUp;
            bestHit = h;
            found = true;
        }

        return found;
    }

    private void BuildJunctionPositions(List<RoadData> roads, HashSet<long> junctionNodeIds)
    {
        _junctionWorldPositions.Clear();
        if (junctionNodeIds.Count == 0)
            return;

        var seen = new HashSet<long>();

        foreach (var road in roads)
        {
            foreach (var node in road.Nodes)
            {
                if (!junctionNodeIds.Contains(node.Id) || !seen.Add(node.Id))
                    continue;

                var sample = GetNodeSample(node);
                if (sample.Confidence == HeightConfidence.HitCesium)
                    _junctionWorldPositions.Add(sample.WorldCenter);
            }
        }
    }

    private IEnumerator NotifyProxyReady()
    {
        yield return null;
        Physics.SyncTransforms();
        ComputeSpawnHint();
        OnProxyReady?.Invoke();
    }

    private void ComputeSpawnHint()
    {
        _hasLastSpawnHint = false;

        if (_proxyContainer == null || _agentAnchor == null)
            return;

        Vector3 agentPos = _agentAnchor.transform.position;
        Vector3 up = _agentAnchor.transform.up;

        Vector3 samplePoint = agentPos;
        if (_roadCenterPoints.Count > 0)
        {
            float bestPlanarDist = float.MaxValue;
            foreach (Vector3 center in _roadCenterPoints)
            {
                float planarDist = Vector3.ProjectOnPlane(center - agentPos, up).magnitude;
                if (planarDist >= bestPlanarDist)
                    continue;

                bestPlanarDist = planarDist;
                samplePoint = center;
            }

            Debug.Log($"[RoadDrivingProxy] Nearest road center offset {Vector3.ProjectOnPlane(samplePoint - agentPos, up).magnitude:F1}m.");
        }

        Vector3 rayOrigin = samplePoint + up * 10f;
        if (_proxyLayer >= 0)
        {
            int layerMask = 1 << _proxyLayer;
            if (Physics.SphereCast(rayOrigin, 1.5f, -up, out RaycastHit hit, 30f, layerMask)
                && hit.collider != null
                && hit.collider.gameObject.name == "Deck")
            {
                _lastSpawnHintPosition = hit.point;
                _lastSpawnHintNormal = hit.normal;
                _hasLastSpawnHint = true;
                return;
            }
        }

        Collider bestDeck = null;
        float closestPlanarDist = float.MaxValue;
        Vector3 bestPoint = samplePoint;

        foreach (Collider col in _proxyContainer.GetComponentsInChildren<Collider>())
        {
            if (col.gameObject.name != "Deck")
                continue;

            Vector3 closest = col.ClosestPoint(samplePoint);
            float planarDist = Vector3.ProjectOnPlane(closest - samplePoint, up).magnitude;
            if (planarDist >= closestPlanarDist)
                continue;

            closestPlanarDist = planarDist;
            bestPoint = closest;
            bestDeck = col;
        }

        if (bestDeck != null)
        {
            _lastSpawnHintPosition = bestPoint;
            _lastSpawnHintNormal = up;
            _hasLastSpawnHint = true;
            return;
        }

        _lastSpawnHintPosition = samplePoint;
        _lastSpawnHintNormal = up;
        _hasLastSpawnHint = true;

        if (TryGetRoadSurfaceHit(samplePoint, up, out RaycastHit cesiumHit))
        {
            _lastSpawnHintPosition = cesiumHit.point + up * heightOffset;
            _lastSpawnHintNormal = cesiumHit.normal.sqrMagnitude > 0.01f ? cesiumHit.normal : up;
        }

        Debug.LogWarning("[RoadDrivingProxy] Using Cesium spawn hint (no deck mesh yet).");
    }

    private float GetRoadHalfWidth(RoadData road)
    {
        if (road.WidthMeters > 0f)
            return road.WidthMeters * 0.5f;

        if (road.Lanes > 0)
            return road.Lanes * 1.75f;

        return GetDefaultHalfWidth(road.Type);
    }

    private float GetDefaultHalfWidth(string roadType)
    {
        return roadType switch
        {
            "motorway"      => 7f,
            "trunk"         => 6f,
            "primary"       => 5f,
            "secondary"     => 4f,
            "tertiary"      => 3.5f,
            "unclassified"  => 3f,
            "residential"   => 2.75f,
            "living_street" => 2.5f,
            "service"       => 2.25f,
            _               => proxyWidth * 0.5f
        };
    }

    private void PreResolveNodeHeights(List<RoadData> roads)
    {
        _nodeHeightCache.Clear();
        double refHeight = GetReferenceEllipsoidHeight();
        var seen = new HashSet<long>();

        foreach (var road in roads)
        {
            foreach (var node in road.Nodes)
            {
                if (!seen.Add(node.Id))
                    continue;

                Vector3 pos = GpsToUnity(node.Lat, node.Lng, refHeight);
                Vector3 up = GetLocalUp(pos);
                var sample = CreateHeightSample(node.Lat, node.Lng, pos, up);
                TryResolveSampleHeight(ref sample);
                StoreNodeSample(node.Id, sample);
            }
        }
    }

    private static (long, long) EdgeKey(long nodeA, long nodeB)
    {
        return nodeA < nodeB ? (nodeA, nodeB) : (nodeB, nodeA);
    }

    private void MergeCachedRoads(List<RoadData> roads)
    {
        if (_cachedRoads == null)
        {
            _cachedRoads = roads;
            return;
        }

        _cachedRoads.AddRange(roads);
    }

    private void MergeNodeHeights(List<RoadData> roads)
    {
        double refHeight = GetReferenceEllipsoidHeight();
        var seen = new HashSet<long>(_nodeHeightCache.Keys);

        foreach (var road in roads)
        {
            foreach (var node in road.Nodes)
            {
                if (!seen.Add(node.Id))
                    continue;

                Vector3 pos = GpsToUnity(node.Lat, node.Lng, refHeight);
                Vector3 up = GetLocalUp(pos);
                var sample = CreateHeightSample(node.Lat, node.Lng, pos, up);
                TryResolveSampleHeight(ref sample);
                StoreNodeSample(node.Id, sample);
            }
        }
    }

    private HeightSample GetNodeSample(RoadNode node)
    {
        if (_nodeHeightCache.TryGetValue(node.Id, out HeightSample cached)
            && cached.Confidence == HeightConfidence.HitCesium)
            return cached;

        double refHeight = GetReferenceEllipsoidHeight();
        Vector3 pos = GpsToUnity(node.Lat, node.Lng, refHeight);
        Vector3 up = GetLocalUp(pos);
        var sample = CreateHeightSample(node.Lat, node.Lng, pos, up);
        TryResolveSampleHeight(ref sample);
        StoreNodeSample(node.Id, sample);
        return sample;
    }

    private void StoreNodeSample(long nodeId, HeightSample sample)
    {
        if (sample.Confidence != HeightConfidence.HitCesium)
            return;

        _nodeHeightCache[nodeId] = sample;
    }

    private bool TryGetBuildableRange(IReadOnlyList<HeightSample> samples, out int start, out int end)
    {
        start = -1;
        end = -1;
        float bestScore = float.MaxValue;
        Vector3 agentPos = _agentAnchor != null ? _agentAnchor.transform.position : Vector3.zero;
        Vector3 up = _agentAnchor != null ? _agentAnchor.transform.up : Vector3.up;
        bool hasAgent = _agentAnchor != null;

        int i = 0;
        while (i < samples.Count)
        {
            if (samples[i].Confidence == HeightConfidence.Pending)
            {
                i++;
                continue;
            }

            int runStart = i;
            int runEnd = i;
            int hitCount = samples[i].Confidence == HeightConfidence.HitCesium ? 1 : 0;

            while (runEnd + 1 < samples.Count && samples[runEnd + 1].Confidence != HeightConfidence.Pending)
            {
                runEnd++;
                if (samples[runEnd].Confidence == HeightConfidence.HitCesium)
                    hitCount++;
            }

            if (runEnd > runStart && hitCount >= 1)
            {
                float minDist = 0f;
                if (hasAgent)
                {
                    minDist = float.MaxValue;
                    for (int k = runStart; k <= runEnd; k++)
                    {
                        float d = Vector3.ProjectOnPlane(samples[k].EllipsoidPos - agentPos, up).magnitude;
                        if (d < minDist)
                            minDist = d;
                    }
                }

                float score = hasAgent ? minDist - (runEnd - runStart) * 0.05f : -(runEnd - runStart);
                if (score < bestScore)
                {
                    bestScore = score;
                    start = runStart;
                    end = runEnd;
                }
            }

            i = runEnd + 1;
        }

        return start >= 0;
    }

    private void ApplyNodeCacheToAllSegments(bool nearAgentOnly = false)
    {
        Vector3 agentPos = _agentAnchor != null ? _agentAnchor.transform.position : Vector3.zero;
        Vector3 up = _agentAnchor != null ? _agentAnchor.transform.up : Vector3.up;
        float radiusSqr = agentRefineRadius * agentRefineRadius;

        _roadCenterPoints.Clear();
        foreach (var record in _segmentRecords)
        {
            if (record.Samples.Count < 2)
                continue;

            if (nearAgentOnly && !IsSegmentNearAgent(record, agentPos, up, radiusSqr))
            {
                if (record.Root != null)
                {
                    for (int i = 0; i < record.Samples.Count; i++)
                    {
                        if (record.Samples[i].Confidence != HeightConfidence.Pending)
                            _roadCenterPoints.Add(record.Samples[i].WorldCenter);
                    }
                }
                continue;
            }

            record.Samples[0] = GetNodeSample(record.NodeA);
            record.Samples[record.Samples.Count - 1] = GetNodeSample(record.NodeB);

            for (int i = 1; i < record.Samples.Count - 1; i++)
            {
                HeightSample interior = record.Samples[i];
                if (interior.Confidence == HeightConfidence.HitCesium)
                    continue;

                interior.LocalUp = GetLocalUp(interior.EllipsoidPos);
                TryResolveSampleHeight(ref interior);
                record.Samples[i] = interior;
            }

            InterpolatePendingSamples(record.Samples);

            if (!TryGetBuildableRange(record.Samples, out int buildStart, out int buildEnd))
            {
                if (record.Root != null)
                {
                    Destroy(record.Root);
                    record.Root = null;
                }
                continue;
            }

            if (record.Root == null)
            {
                var segmentRoot = new GameObject($"Proxy_{record.RoadType}_{record.SegIndex}");
                segmentRoot.transform.SetParent(_proxyContainer.transform, false);
                segmentRoot.transform.localPosition = Vector3.zero;
                segmentRoot.transform.localRotation = Quaternion.identity;
                record.Root = segmentRoot;
            }

            RebuildSegmentMesh(record, buildStart, buildEnd);

            for (int i = buildStart; i <= buildEnd; i++)
                _roadCenterPoints.Add(record.Samples[i].WorldCenter);
        }
    }

    private bool TryAddSegment(
        RoadNode a, RoadNode b,
        float halfW,
        string roadType,
        int segIndex)
    {
        if (!_segmentEdgeKeys.Add(EdgeKey(a.Id, b.Id)))
            return false;

        List<HeightSample> samples = BuildSampleLine(a, b);
        if (samples.Count < 2)
            return false;

        var record = new SegmentRecord
        {
            NodeA = a,
            NodeB = b,
            HalfWidth = halfW,
            RoadType = roadType,
            SegIndex = segIndex
        };
        record.Samples.AddRange(samples);
        _segmentRecords.Add(record);

        if (!TryGetBuildableRange(samples, out int buildStart, out int buildEnd))
            return false;

        var segmentRoot = new GameObject($"Proxy_{roadType}_{segIndex}");
        segmentRoot.transform.SetParent(_proxyContainer.transform, false);
        segmentRoot.transform.localPosition = Vector3.zero;
        segmentRoot.transform.localRotation = Quaternion.identity;
        record.Root = segmentRoot;

        if (!RebuildSegmentMesh(record, buildStart, buildEnd))
            return false;

        for (int i = buildStart; i <= buildEnd; i++)
            _roadCenterPoints.Add(record.Samples[i].WorldCenter);

        return true;
    }

    private bool TryAddOSMLink(
        RoadNode a, RoadNode b,
        float halfW,
        string roadType,
        int segIndex)
    {
        double refHeight = GetReferenceEllipsoidHeight();
        Vector3 estP1 = GpsToUnity(a.Lat, a.Lng, refHeight);
        Vector3 estP2 = GpsToUnity(b.Lat, b.Lng, refHeight);
        float estSpan = Vector3.Distance(estP1, estP2);
        int chunks = Mathf.Max(1, Mathf.CeilToInt(estSpan / maxSegmentLength));

        if (chunks == 1)
            return TryAddSegment(a, b, halfW, roadType, segIndex);

        bool anyBuilt = false;
        for (int c = 0; c < chunks; c++)
        {
            float t0 = c / (float)chunks;
            float t1 = (c + 1) / (float)chunks;

            RoadNode subA = c == 0
                ? a
                : MakeSyntheticNode(a, b, t0);
            RoadNode subB = c == chunks - 1
                ? b
                : MakeSyntheticNode(a, b, t1);

            if (TryAddSegment(subA, subB, halfW, roadType, segIndex * 1000 + c))
                anyBuilt = true;
        }

        return anyBuilt;
    }

    private static RoadNode MakeSyntheticNode(RoadNode a, RoadNode b, float t)
    {
        unchecked
        {
            int tKey = Mathf.RoundToInt(t * 100000f);
            long id = -(Math.Abs(a.Id) * 73856093L ^ Math.Abs(b.Id) * 19349663L ^ tKey);
            return new RoadNode(
                id,
                a.Lat + (b.Lat - a.Lat) * t,
                a.Lng + (b.Lng - a.Lng) * t);
        }
    }

    private List<HeightSample> BuildSampleLine(RoadNode a, RoadNode b)
    {
        double refHeight = GetReferenceEllipsoidHeight();
        Vector3 estP1 = GpsToUnity(a.Lat, a.Lng, refHeight);
        Vector3 estP2 = GpsToUnity(b.Lat, b.Lng, refHeight);
        float estSpan = Vector3.Distance(estP1, estP2);
        if (estSpan < 0.1f)
            return new List<HeightSample>();

        int steps = Mathf.Max(1, Mathf.CeilToInt(estSpan / maxSegmentLength));
        var samples = new List<HeightSample>(steps + 1);

        samples.Add(GetNodeSample(a));

        for (int i = 1; i < steps; i++)
        {
            float t = i / (float)steps;
            double lat = a.Lat + (b.Lat - a.Lat) * t;
            double lng = a.Lng + (b.Lng - a.Lng) * t;
            Vector3 pos = GpsToUnity(lat, lng, refHeight);
            Vector3 up = GetLocalUp(pos);
            var sample = CreateHeightSample(lat, lng, pos, up);
            TryResolveSampleHeight(ref sample);
            samples.Add(sample);
        }

        samples.Add(GetNodeSample(b));

        SubdivideSteepGrades(samples);
        InterpolatePendingSamples(samples);

        return samples;
    }

    private void SubdivideSteepGrades(List<HeightSample> samples)
    {
        bool inserted = true;
        int guard = 0;

        while (inserted && guard < 32)
        {
            inserted = false;
            guard++;

            for (int i = samples.Count - 1; i > 0; i--)
            {
                HeightSample prev = samples[i - 1];
                HeightSample next = samples[i];
                if (prev.Confidence == HeightConfidence.Pending || next.Confidence == HeightConfidence.Pending)
                    continue;

                float delta = HeightDeltaAlongUp(prev, next);
                if (delta <= gradeResampleThreshold)
                    continue;

                double midLat = (prev.Lat + next.Lat) * 0.5;
                double midLng = (prev.Lng + next.Lng) * 0.5;
                double refHeight = GetReferenceEllipsoidHeight();
                Vector3 pos = GpsToUnity(midLat, midLng, refHeight);
                Vector3 up = GetLocalUp(pos);
                var mid = CreateHeightSample(midLat, midLng, pos, up);
                TryResolveSampleHeight(ref mid);

                samples.Insert(i, mid);
                inserted = true;
                break;
            }
        }

        InterpolatePendingSamples(samples);
    }

    private static HeightSample CreateHeightSample(double lat, double lng, Vector3 ellipsoidPos, Vector3 localUp)
    {
        return new HeightSample
        {
            Lat = lat,
            Lng = lng,
            EllipsoidPos = ellipsoidPos,
            LocalUp = localUp,
            WorldCenter = ellipsoidPos,
            Confidence = HeightConfidence.Pending
        };
    }

    private bool TryResolveSampleHeight(ref HeightSample sample)
    {
        // Fire from a sky-high probe position so the ray always starts above the
        // Cesium terrain regardless of how hilly the area is. Using the flat
        // reference ellipsoid height as the origin can place the start point
        // underground on hills, causing the raycast to miss and the proxy to sink.
        //
        // skyProbeHeight should exceed the max terrain elevation ABOVE _georeference.height
        // in your scene. The 2 m minimum-depth guard in TryGetRoadSurfaceHitFrom prevents
        // false hits from probe-level LOD artifacts without needing a separate sanity window.
        Vector3 skyProbe = sample.EllipsoidPos + sample.LocalUp * skyProbeHeight;
        if (TryGetRoadSurfaceHitFrom(skyProbe, sample.LocalUp, out RaycastHit hit))
        {
            // Only sanity we need: reject hits that are suspiciously far BELOW the
            // reference ellipsoid position (> 500 m below refHeight means likely a
            // spurious back-face or underground collider — keep as Pending instead).
            float heightFromEllipsoid = Vector3.Dot(hit.point - sample.EllipsoidPos, sample.LocalUp);
            if (heightFromEllipsoid > -500f)
            {
                sample.WorldCenter = hit.point + sample.LocalUp * heightOffset;
                sample.Confidence = HeightConfidence.HitCesium;
                return true;
            }
        }

        sample.WorldCenter = sample.EllipsoidPos;
        sample.Confidence = HeightConfidence.Pending;
        return false;
    }

    private static float HeightDeltaAlongUp(in HeightSample a, in HeightSample b)
    {
        return Mathf.Abs(Vector3.Dot(b.WorldCenter - a.WorldCenter, a.LocalUp));
    }

    private static void InterpolatePendingSamples(List<HeightSample> samples)
    {
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Confidence != HeightConfidence.Pending)
                continue;

            int prev = -1;
            int next = -1;
            for (int p = i - 1; p >= 0; p--)
            {
                if (samples[p].Confidence != HeightConfidence.Pending)
                {
                    prev = p;
                    break;
                }
            }

            for (int n = i + 1; n < samples.Count; n++)
            {
                if (samples[n].Confidence != HeightConfidence.Pending)
                {
                    next = n;
                    break;
                }
            }

            HeightSample s = samples[i];
            if (prev >= 0 && next >= 0)
            {
                float t = (i - prev) / (float)(next - prev);
                s.WorldCenter = Vector3.Lerp(samples[prev].WorldCenter, samples[next].WorldCenter, t);
                s.Confidence = HeightConfidence.Interpolated;
            }
            else if (prev >= 0)
            {
                s.WorldCenter = samples[prev].WorldCenter;
                s.Confidence = HeightConfidence.Interpolated;
            }
            else if (next >= 0)
            {
                s.WorldCenter = samples[next].WorldCenter;
                s.Confidence = HeightConfidence.Interpolated;
            }

            samples[i] = s;
        }
    }

    private bool RebuildSegmentMesh(SegmentRecord record, int startIndex, int endIndex)
    {
        if (record.Root == null || endIndex - startIndex < 1)
            return false;

        foreach (Transform child in record.Root.transform)
            Destroy(child.gameObject);

        int sampleCount = endIndex - startIndex + 1;
        int steps = sampleCount - 1;
        float halfW = record.HalfWidth;

        Transform geo = _georeference != null ? _georeference.transform : null;
        Vector3 ToLocal(Vector3 world) =>
            geo != null ? geo.InverseTransformPoint(world) : world;

        var centers = new Vector3[sampleCount];
        var ups = new Vector3[sampleCount];
        var railHeights = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            HeightSample sample = record.Samples[startIndex + i];
            centers[i] = sample.WorldCenter;
            ups[i] = sample.LocalUp;
            railHeights[i] = EvaluateRailHeight(centers[i], ups[i]);
        }

        var deckVerts = new List<Vector3>();
        var deckTris = new List<int>();
        var railVerts = new List<Vector3>();
        var railTris = new List<int>();
        const int railVertsPerStep = 8;
        const int deckVertsPerStep = 4;
        const float deckThickness = 0.2f;

        for (int i = 0; i <= steps; i++)
        {
            Vector3 tangent = i == 0
                ? centers[1] - centers[0]
                : i == steps
                    ? centers[steps] - centers[steps - 1]
                    : centers[i + 1] - centers[i - 1];

            Vector3 up = ups[i];
            Vector3 dir = Vector3.ProjectOnPlane(tangent, up);
            if (dir.sqrMagnitude < 0.0001f)
            {
                dir = i > 0 ? (centers[i] - centers[i - 1]) : (centers[i + 1] - centers[i]);
                dir = Vector3.ProjectOnPlane(dir, up).normalized;
            }
            else
            {
                dir.Normalize();
            }

            Vector3 perp = Vector3.Cross(up, dir).normalized;
            Vector3 roadL = centers[i] - perp * halfW;
            Vector3 roadR = centers[i] + perp * halfW;
            Vector3 roadLBottom = roadL - up * deckThickness;
            Vector3 roadRBottom = roadR - up * deckThickness;

            deckVerts.Add(ToLocal(roadL));
            deckVerts.Add(ToLocal(roadR));
            deckVerts.Add(ToLocal(roadLBottom));
            deckVerts.Add(ToLocal(roadRBottom));

            if (guardRailHeight > 0f)
            {
                float railH = railHeights[i];
                Vector3 railLInner = roadL + perp * guardRailThickness;
                Vector3 railRInner = roadR - perp * guardRailThickness;

                railVerts.Add(ToLocal(roadL));
                railVerts.Add(ToLocal(roadR));
                railVerts.Add(ToLocal(railLInner));
                railVerts.Add(ToLocal(railLInner + up * railH));
                railVerts.Add(ToLocal(roadL + up * railH));
                railVerts.Add(ToLocal(railRInner));
                railVerts.Add(ToLocal(railRInner + up * railH));
                railVerts.Add(ToLocal(roadR + up * railH));
            }
        }

        for (int i = 0; i < steps; i++)
        {
            int c = i * deckVertsPerStep;
            int n = (i + 1) * deckVertsPerStep;

            AddQuad(deckTris, c, c + 1, n + 1, n);
            AddQuad(deckTris, c + 2, n + 2, n + 3, c + 3);
            AddQuad(deckTris, c, n, n + 2, c + 2);
            AddQuad(deckTris, c + 1, c + 3, n + 3, n + 1);

            if (guardRailHeight <= 0f || railHeights[i] < 0.01f || railHeights[i + 1] < 0.01f)
                continue;

            int rc = i * railVertsPerStep;
            int rn = (i + 1) * railVertsPerStep;

            AddQuad(railTris, rc + 2, rn + 2, rn + 3, rc + 3);
            AddQuad(railTris, rc + 3, rn + 3, rn + 4, rc + 4);
            AddQuad(railTris, rc + 4, rn + 4, rn, rc);
            AddQuad(railTris, rc + 1, rc + 7, rn + 7, rn + 1);
            AddQuad(railTris, rc + 7, rc + 6, rn + 6, rn + 7);
            AddQuad(railTris, rc + 6, rc + 5, rn + 5, rn + 6);
        }

        CreateColliderChild(record.Root.transform, "Deck", deckVerts, deckTris);
        if (railVerts.Count >= 8 && railTris.Count >= 6)
            CreateColliderChild(record.Root.transform, "GuardRails", railVerts, railTris);

        return deckVerts.Count >= 4;
    }

    private IEnumerator ContinuousRefineLoop()
    {
        var wait = new WaitForSeconds(cesiumResampleIntervalSeconds);
        int lastBuiltCount = -1;

        while (true)
        {
            if (_proxyContainer != null && _cachedRoads != null && RefineNearAgent())
            {
                int built = CountBuiltSegments();
                if (built != lastBuiltCount)
                {
                    lastBuiltCount = built;
                    Debug.Log($"[RoadDrivingProxy] Proxy deck: {built} built, {CountDeferredSegments()} waiting for Cesium tiles.");
                }
            }

            yield return wait;
        }
    }

    private bool RefineNearAgent()
    {
        if (_cachedRoads == null || _agentAnchor == null || _proxyContainer == null)
            return false;

        Vector3 agentPos = _agentAnchor.transform.position;
        Vector3 up = _agentAnchor.transform.up;
        float radiusSqr = agentRefineRadius * agentRefineRadius;
        bool anyNear = false;

        foreach (var record in _segmentRecords)
        {
            if (!IsSegmentNearAgent(record, agentPos, up, radiusSqr))
                continue;

            anyNear = true;
            RefreshSegmentSamples(record);
        }

        if (!anyNear)
            return false;

        ApplyNodeCacheToAllSegments(nearAgentOnly: true);
        Physics.SyncTransforms();
        ComputeSpawnHint();
        return true;
    }

    private static bool IsSegmentNearAgent(SegmentRecord record, Vector3 agentPos, Vector3 up, float radiusSqr)
    {
        foreach (var sample in record.Samples)
        {
            Vector3 delta = sample.EllipsoidPos - agentPos;
            if (Vector3.ProjectOnPlane(delta, up).sqrMagnitude <= radiusSqr)
                return true;
        }

        return false;
    }

    private void RefreshSegmentSamples(SegmentRecord record)
    {
        if (record.Samples.Count < 2)
            return;

        record.Samples[0] = GetNodeSample(record.NodeA);
        record.Samples[record.Samples.Count - 1] = GetNodeSample(record.NodeB);

        for (int i = 1; i < record.Samples.Count - 1; i++)
        {
            HeightSample interior = record.Samples[i];
            if (interior.Confidence == HeightConfidence.HitCesium)
                continue;

            interior.LocalUp = GetLocalUp(interior.EllipsoidPos);
            TryResolveSampleHeight(ref interior);
            record.Samples[i] = interior;
        }

        InterpolatePendingSamples(record.Samples);
    }

    private int CountBuiltSegments()
    {
        int built = 0;
        foreach (var record in _segmentRecords)
        {
            if (record.Root != null)
                built++;
        }
        return built;
    }

    private int CountDeferredSegments()
    {
        int deferred = 0;
        foreach (var record in _segmentRecords)
        {
            if (record.Root == null)
                deferred++;
        }
        return deferred;
    }

    private int CountPendingSamples()
    {
        int count = 0;
        foreach (var record in _segmentRecords)
        {
            foreach (var sample in record.Samples)
            {
                if (sample.Confidence != HeightConfidence.HitCesium)
                    count++;
            }
        }
        return count;
    }

    private static float HaversineMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadius = 6_378_137.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLng = (lng2 - lng1) * Math.PI / 180.0;
        double a = Math.Sin(dLat * 0.5) * Math.Sin(dLat * 0.5)
            + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
            * Math.Sin(dLng * 0.5) * Math.Sin(dLng * 0.5);
        return (float)(earthRadius * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a)));
    }

    private void CreateColliderChild(Transform parent, string suffix, List<Vector3> verts, List<int> tris)
    {
        if (verts.Count < 3 || tris.Count < 3)
            return;

        var mesh = new Mesh { name = $"RoadProxy_{suffix}" };
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var go = new GameObject(suffix);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;

        if (_proxyLayer >= 0)
            go.layer = _proxyLayer;

        go.AddComponent<MeshFilter>().mesh = mesh;
        var col = go.AddComponent<MeshCollider>();
        col.sharedMesh = mesh;

        if (showDebugMesh && suffix == "Deck")
            ApplyDebugDeckRenderer(go, mesh);
    }

    private void ApplyDebugDeckRenderer(GameObject go, Mesh mesh)
    {
        if (_debugDeckMaterial == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Sprites/Default");
            _debugDeckMaterial = new Material(shader);
            _debugDeckMaterial.color = debugDeckColor;
            if (_debugDeckMaterial.HasProperty("_Surface"))
            {
                _debugDeckMaterial.SetFloat("_Surface", 1f);
                _debugDeckMaterial.SetFloat("_Blend", 0f);
                _debugDeckMaterial.renderQueue = 3000;
            }
        }

        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = _debugDeckMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private float EvaluateRailHeight(Vector3 worldPos, Vector3 localUp)
    {
        if (guardRailHeight <= 0f || junctionClearanceRadius <= 0f || _junctionWorldPositions.Count == 0)
            return guardRailHeight;

        float minDist = float.MaxValue;
        foreach (Vector3 junction in _junctionWorldPositions)
        {
            Vector3 delta = Vector3.ProjectOnPlane(worldPos - junction, localUp);
            float dist = delta.magnitude;
            if (dist < minDist)
                minDist = dist;
        }

        if (minDist >= junctionClearanceRadius)
            return guardRailHeight;

        float t = minDist / junctionClearanceRadius;
        return guardRailHeight * Mathf.SmoothStep(0f, 1f, t);
    }

    private void AddQuad(List<int> tris, int v1, int v2, int v3, int v4)
    {
        tris.Add(v1);
        tris.Add(v2);
        tris.Add(v3);
        tris.Add(v1);
        tris.Add(v3);
        tris.Add(v4);
    }


    private Vector3 GetLocalUp(Vector3 worldPos)
    {
        if (_georeference != null)
        {
            double3 ecef = _georeference.TransformUnityPositionToEarthCenteredEarthFixed(
                new double3(worldPos.x, worldPos.y, worldPos.z));
            double3 normalEcef = _georeference.ellipsoid.GeodeticSurfaceNormal(ecef);
            double3 unityNormal = _georeference.TransformEarthCenteredEarthFixedDirectionToUnity(normalEcef);
            return new Vector3((float)unityNormal.x, (float)unityNormal.y, (float)unityNormal.z).normalized;
        }

        if (_agentAnchor != null)
            return _agentAnchor.transform.up;

        return Vector3.up;
    }

    private Vector3 GpsToUnity(double lat, double lng, double heightMeters)
    {
        if (_georeference == null) return Vector3.zero;

        double3 ecef = _georeference.ellipsoid.LongitudeLatitudeHeightToCenteredFixed(
            new double3(lng, lat, heightMeters));
        double3 unity = _georeference.TransformEarthCenteredEarthFixedPositionToUnity(ecef);
        return new Vector3((float)unity.x, (float)unity.y, (float)unity.z);
    }

    private (List<RoadData>, HashSet<long>) ParseRoads(string json)
    {
        var roads = new List<RoadData>();
        var nodeMap = new Dictionary<long, (double Lat, double Lng)>();
        var nodeUsageCount = new Dictionary<long, int>();
        var wayNodeLists = new List<(string, List<long>, string, int, float)>();

        // Pass 1: Parse all elements into memory
        string[] parts = json.Split(new[] { "\"type\":" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string p in parts)
        {
            string trimmedPart = p.TrimStart();
            if (trimmedPart.StartsWith("\"node\""))
            {
                long id = ExtractLong(p, "\"id\"");
                double nLat = ExtractDouble(p, "\"lat\"");
                double nLng = ExtractDouble(p, "\"lon\"");
                if (id != 0) nodeMap[id] = (nLat, nLng);
            }
            else if (trimmedPart.StartsWith("\"way\""))
            {
                 if (!p.Contains("\"highway\"")) continue;
                string roadType = ExtractTagValue(p, "\"highway\"");
                int lanes = ExtractIntTag(p, "\"lanes\"");
                float width = ExtractWidthMetres(p);
                var nodeIds = ExtractNodeIds(p);
                wayNodeLists.Add((p, nodeIds, roadType, lanes, width));

                foreach (long nodeId in nodeIds)
                {
                    nodeUsageCount.TryGetValue(nodeId, out int currentCount);
                    nodeUsageCount[nodeId] = currentCount + 1;
                }
            }
        }

        // Pass 2: Identify junction nodes
        var junctionNodeIds = new HashSet<long>();
        foreach (var usage in nodeUsageCount)
        {
            if (usage.Value > 2)
            {
                junctionNodeIds.Add(usage.Key);
            }
        }

        // Pass 3: Build final RoadData objects
        foreach(var (p, nodeIds, roadType, lanes, width) in wayNodeLists)
        {
            var road = new RoadData { Type = roadType, Lanes = lanes, WidthMeters = width };
            foreach (long id in nodeIds)
            {
                if (nodeMap.TryGetValue(id, out var coords))
                {
                    road.Nodes.Add(new RoadNode(id, coords.Lat, coords.Lng));
                }
            }
            if (road.Nodes.Count >= 2) roads.Add(road);
        }

        return (roads, junctionNodeIds);
    }

    private int ExtractIntTag(string s, string k)
    {
        int i = s.IndexOf(k); if (i < 0) return 0;
        int c = s.IndexOf(':', i), e = s.IndexOfAny(new[] { ',', '}' }, c);
        return int.TryParse(s.Substring(c + 1, e - c - 1).Trim().Trim('"'), out int r) ? r : 0;
    }

    private float ExtractWidthMetres(string s)
    {
        string raw = ExtractTagValue(s, "\"width\"");
        if (string.IsNullOrEmpty(raw)) return 0f;

        raw = raw.Trim().Trim('"');
        if (raw.EndsWith(" m", StringComparison.OrdinalIgnoreCase))
            raw = raw.Substring(0, raw.Length - 2).Trim();

        return float.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float metres) ? metres : 0f;
    }

    private long ExtractLong(string s, string k)
    {
        int i = s.IndexOf(k); if (i < 0) return 0;
        int c = s.IndexOf(':', i), e = s.IndexOfAny(new[] { ',', '}' }, c);
        return long.TryParse(s.Substring(c + 1, e - c - 1).Trim(), out long r) ? r : 0;
    }

    private double ExtractDouble(string s, string k)
    {
        int i = s.IndexOf(k); if (i < 0) return 0;
        int c = s.IndexOf(':', i), e = s.IndexOfAny(new[] { ',', '}' }, c);
        return double.TryParse(s.Substring(c + 1, e - c - 1).Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double r) ? r : 0;
    }

    private string ExtractTagValue(string s, string k)
    {
        int i = s.IndexOf(k); if (i < 0) return "";
        int c = s.IndexOf(':', i); if (c < 0) return "";
        int vs = c + 1; while (vs < s.Length && s[vs] == ' ') vs++;
        if (vs >= s.Length) return "";
        if (s[vs] == '"') { int e = s.IndexOf('"', vs + 1); return e < 0 ? "" : s.Substring(vs + 1, e - vs - 1); }
        int end = s.IndexOfAny(new[] { ',', '}', '\n' }, vs);
        return end < 0 ? "" : s.Substring(vs, end - vs).Trim();
    }

    private List<long> ExtractNodeIds(string s)
    {
        var ids = new List<long>();
        int ns = s.IndexOf("\"nodes\""); if (ns < 0) return ids;
        int a = s.IndexOf('[', ns), b = s.IndexOf(']', a); if (a < 0 || b < 0) return ids;
        foreach (string v in s.Substring(a + 1, b - a - 1).Split(','))
            if (long.TryParse(v.Trim(), out long id)) ids.Add(id);
        return ids;
    }
}
