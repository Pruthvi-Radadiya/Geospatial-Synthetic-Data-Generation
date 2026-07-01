using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;

public class BuildingGenerator : MonoBehaviour
{
    [Header("Fetch Settings")]
    [Tooltip("Radius in metres around the agent to fetch buildings")]
    public float fetchRadius = 150f;

    [Header("Building Defaults")]
    [Tooltip("Metres per floor -- used when only level count is available")]
    public float metresPerFloor = 3.5f;

    [Tooltip("Fallback height if OSM has no height or level data")]
    public float defaultHeight = 10f;

    private double _originLat;
    private double _originLng;
    private bool   _originSet = false;

    private GameObject _buildingContainer;
    private Dictionary<int, Texture2D> _streetViewTextures;

    private double _latestRequestLat;
    private double _latestRequestLng;
    private GPSTracker _gps;

    void Awake() => _gps = FindObjectOfType<GPSTracker>();

    public static event Action<double, double> OnBuildingsFetched;

    private class BuildingData
    {
        public List<Vector2d> FootprintGPS = new List<Vector2d>();
        public float Height;
    }

    private struct Vector2d
    {
        public double Lat, Lng;
        public Vector2d(double lat, double lng) { Lat = lat; Lng = lng; }
    }

    void OnEnable()
    {
        GPSTracker.OnNewTileEntered   += HandleNewTile;
        TileFetcher.OnStreetViewReady += HandleStreetViewReady;
    }

    void OnDisable()
    {
        GPSTracker.OnNewTileEntered   -= HandleNewTile;
        TileFetcher.OnStreetViewReady -= HandleStreetViewReady;
    }

    private void HandleStreetViewReady(double lat, double lng, Dictionary<int, Texture2D> textures)
    {
        _streetViewTextures = textures;
    }

    private void HandleNewTile(double lat, double lng)
    {
        if (!_originSet) { _originLat = lat; _originLng = lng; _originSet = true; }
        _latestRequestLat = lat;
        _latestRequestLng = lng;
        StartCoroutine(FetchBuildings(lat, lng));
    }

    private IEnumerator FetchBuildings(double lat, double lng)
    {
        string query = $"[out:json][timeout:25];" +
                       $"(way[\"building\"](around:{fetchRadius},{lat:F6},{lng:F6}););" +
                       $"out body;>;out skel qt;";

        string url = "https://overpass-api.de/api/interpreter?data=" +
                     UnityWebRequest.EscapeURL(query);

        Debug.Log($"[BuildingGenerator] Fetching buildings at {lat:F6}, {lng:F6}");

        int retries = 3;
        string responseText = null;

        for (int i = 0; i < retries; i++)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = 30;
            yield return req.SendWebRequest();

            if (lat != _latestRequestLat || lng != _latestRequestLng)
            {
                Debug.Log($"[BuildingGenerator] Discarded stale response for {lat:F6},{lng:F6}");
                yield break;
            }

            if (req.result == UnityWebRequest.Result.Success)
            {
                responseText = req.downloadHandler.text;
                break;
            }

            Debug.LogWarning($"[BuildingGenerator] Overpass API failed (attempt {i+1}/{retries}): {req.error}");
            if (i < retries - 1) yield return new WaitForSeconds(2f);
        }

        if (string.IsNullOrEmpty(responseText))
        {
            Debug.LogWarning($"[BuildingGenerator] Giving up on buildings after {retries} attempts.");
            OnBuildingsFetched?.Invoke(lat, lng);
            yield break;
        }

        List<BuildingData> buildings = ParseOSMBuildings(responseText);

        while (TerrainGenerator.LatestLat != lat || TerrainGenerator.LatestLng != lng)
            yield return null;

        Debug.Log($"[BuildingGenerator] Spawning {buildings.Count} buildings");
        SpawnBuildings(buildings, lat, lng);
        OnBuildingsFetched?.Invoke(lat, lng);
    }

    private List<BuildingData> ParseOSMBuildings(string json)
    {
        var buildings = new List<BuildingData>();
        var nodeMap   = new Dictionary<long, Vector2d>();

        string[] parts = json.Split(new string[] { "\"type\":" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            string trimmed = part.TrimStart();
            if (!trimmed.StartsWith("\"node\"")) continue;
            long   id  = ExtractLong(part,   "\"id\"");
            double lat = ExtractDouble(part,  "\"lat\"");
            double lon = ExtractDouble(part,  "\"lon\"");
            if (id != 0) nodeMap[id] = new Vector2d(lat, lon);
        }

        foreach (string part in parts)
        {
            string trimmed = part.TrimStart();
            if (!trimmed.StartsWith("\"way\"")) continue;
            if (!part.Contains("\"building\"")) continue;

            List<long> nodeIds = ExtractNodeIdArray(part);
            if (nodeIds.Count < 3) continue;

            var building = new BuildingData { Height = ExtractBuildingHeight(part) };
            foreach (long nodeId in nodeIds)
                if (nodeMap.TryGetValue(nodeId, out Vector2d coord))
                    building.FootprintGPS.Add(coord);

            if (building.FootprintGPS.Count >= 3)
                buildings.Add(building);
        }

        return buildings;
    }

    private void SpawnBuildings(List<BuildingData> buildings, double lat, double lng)
    {
        if (_buildingContainer != null) Destroy(_buildingContainer);
        _buildingContainer = new GameObject($"Buildings_{lat:F4}_{lng:F4}");
        _buildingContainer.transform.position = Vector3.zero;

        int spawned = 0;
        foreach (var data in buildings)
            if (TrySpawnBuilding(data)) spawned++;

        Debug.Log($"[BuildingGenerator] Spawned {spawned}/{buildings.Count} buildings");
    }

    private float SampleGroundHeight(Vector3 worldPos)
    {
        var ray = new Ray(new Vector3(worldPos.x, 10000f, worldPos.z), Vector3.down);
        RaycastHit[] hits = Physics.RaycastAll(ray, 20000f);
        foreach (var hit in hits)
            if (hit.collider.GetComponent<TerrainGenerator>() != null || hit.collider.name.Contains("Terrain"))
                return hit.point.y;
        return 0f;
    }

    private bool TrySpawnBuilding(BuildingData data)
    {
        var footprint = new List<Vector3>();
        foreach (var gps in data.FootprintGPS)
        {
            Vector3 pos = GpsToUnity(gps.Lat, gps.Lng);
            pos.y = SampleGroundHeight(pos);
            footprint.Add(pos);
        }

        if (footprint.Count > 1 &&
            Vector3.Distance(footprint[0], footprint[footprint.Count - 1]) < 0.1f)
            footprint.RemoveAt(footprint.Count - 1);

        if (footprint.Count < 3) return false;

        Mesh mesh = BuildBuildingMesh(footprint, data.Height);
        if (mesh == null) return false;

        var go = new GameObject("Building");
        go.transform.SetParent(_buildingContainer.transform);
        go.transform.position = Vector3.zero;

        go.AddComponent<MeshFilter>().mesh = mesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.materials = new Material[]
        {
            CreateFacadeMaterial(footprint),
            CreateRoofMaterial(footprint)
        };

        var agent = FindObjectOfType<VehicleController>();
        bool agentInside = false;
        if (agent != null)
        {
            Bounds b = mesh.bounds;
            b.Expand(2f);
            if (b.Contains(agent.transform.position))
            {
                agentInside = true;
                Debug.Log("[BuildingGenerator] Agent inside building bounds, skipping collider.");
            }
        }

        if (!agentInside)
            go.AddComponent<MeshCollider>().sharedMesh = mesh;

        return true;
    }

    private Mesh BuildBuildingMesh(List<Vector3> footprint, float height)
    {
        var verts = new List<Vector3>();
        var tris  = new List<int>();
        var uvs   = new List<Vector2>();

        int n = footprint.Count;

        float averageY = 0f;
        foreach (var p in footprint) averageY += p.y;
        averageY /= n;
        float roofY = averageY + height;

        // Sub-mesh 0: walls
        for (int i = 0; i < n; i++)
        {
            Vector3 bl = new Vector3(footprint[i].x,           footprint[i].y - 2f,           footprint[i].z);
            Vector3 br = new Vector3(footprint[(i+1)%n].x,     footprint[(i+1)%n].y - 2f,     footprint[(i+1)%n].z);
            Vector3 tl = new Vector3(bl.x, roofY, bl.z);
            Vector3 tr = new Vector3(br.x, roofY, br.z);

            float wallWidth = Vector3.Distance(new Vector3(bl.x, 0, bl.z), new Vector3(br.x, 0, br.z));
            int v0 = verts.Count;
            verts.AddRange(new[] { bl, br, tl, tr });
            tris.AddRange(new[] { v0, v0+2, v0+1, v0+1, v0+2, v0+3 });
            float uMax = wallWidth / 5f;
            uvs.AddRange(new[] {
                new Vector2(0, 0), new Vector2(uMax, 0),
                new Vector2(0, 1), new Vector2(uMax, 1)
            });
        }

        int wallTriCount = tris.Count;

        // Sub-mesh 1: roof
        int roofBase = verts.Count;
        for (int i = 0; i < n; i++)
        {
            verts.Add(new Vector3(footprint[i].x, roofY, footprint[i].z));
            uvs.Add(new Vector2(footprint[i].x * 0.05f, footprint[i].z * 0.05f));
        }
        for (int i = 1; i < n - 1; i++)
            tris.AddRange(new[] { roofBase, roofBase + i + 1, roofBase + i });

        var mesh = new Mesh();
        mesh.vertices     = verts.ToArray();
        mesh.uv           = uvs.ToArray();
        mesh.subMeshCount = 2;
        mesh.SetTriangles(tris.GetRange(0, wallTriCount).ToArray(), 0);
        mesh.SetTriangles(tris.GetRange(wallTriCount, tris.Count - wallTriCount).ToArray(), 1);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // Walls: HDRP/Lit + procedural colour palette, seeded per building centroid
    private Material CreateFacadeMaterial(List<Vector3> footprint)
    {
        var mat = new Material(Shader.Find("HDRP/Lit"));

        int albedoId    = Shader.PropertyToID("_BaseColor");
        int albedoMapId = Shader.PropertyToID("_BaseColorMap");
        int smoothId    = Shader.PropertyToID("_Smoothness");
        int metallicId  = Shader.PropertyToID("_Metallic");

        mat.SetFloat(smoothId,   0.12f);
        mat.SetFloat(metallicId, 0.0f);

        Color[] palette = new Color[]
        {
            new Color(0.82f, 0.74f, 0.60f),
            new Color(0.68f, 0.68f, 0.66f),
            new Color(0.88f, 0.84f, 0.76f),
            new Color(0.74f, 0.56f, 0.46f),
            new Color(0.72f, 0.76f, 0.80f),
            new Color(0.64f, 0.60f, 0.54f),
            new Color(0.86f, 0.80f, 0.68f),
            new Color(0.58f, 0.62f, 0.58f),
        };

        Vector3 centre = Vector3.zero;
        foreach (var p in footprint) centre += p;
        centre /= footprint.Count;

        int hash = Mathf.Abs((int)(centre.x * 73856093f) ^ (int)(centre.z * 19349663f));
        Color baseColour = palette[hash % palette.Length];
        float bv = ((hash >> 4) % 20 - 10) * 0.008f;
        baseColour = new Color(
            Mathf.Clamp01(baseColour.r + bv),
            Mathf.Clamp01(baseColour.g + bv),
            Mathf.Clamp01(baseColour.b + bv), 1f);

        mat.SetColor(albedoId, baseColour);

        if (_streetViewTextures != null && _streetViewTextures.Count > 0)
        {
            float angle = Mathf.Atan2(centre.x, centre.z) * Mathf.Rad2Deg;
            if (angle < 0f) angle += 360f;
            int bestHeading = 0;
            float bestDiff  = float.MaxValue;
            foreach (int h in _streetViewTextures.Keys)
            {
                float diff = Mathf.Abs(Mathf.DeltaAngle(angle, h));
                if (diff < bestDiff) { bestDiff = diff; bestHeading = h; }
            }
            mat.SetTexture(albedoMapId, _streetViewTextures[bestHeading]);
            mat.SetColor(albedoId, Color.Lerp(baseColour, Color.white, 0.7f));
            mat.SetFloat(smoothId, 0.18f);
        }

        return mat;
    }

    // Roof: dark matte material
    private Material CreateRoofMaterial(List<Vector3> footprint)
    {
        var mat = new Material(Shader.Find("HDRP/Lit"));

        int albedoId   = Shader.PropertyToID("_BaseColor");
        int smoothId   = Shader.PropertyToID("_Smoothness");
        int metallicId = Shader.PropertyToID("_Metallic");

        Color[] roofPalette = new Color[]
        {
            new Color(0.18f, 0.17f, 0.16f),
            new Color(0.40f, 0.38f, 0.36f),
            new Color(0.52f, 0.26f, 0.16f),
            new Color(0.28f, 0.30f, 0.30f),
        };

        Vector3 centre = Vector3.zero;
        foreach (var p in footprint) centre += p;
        centre /= footprint.Count;

        int hash = Mathf.Abs((int)(centre.x * 73856093f) ^ (int)(centre.z * 19349663f));
        mat.SetColor(albedoId,   roofPalette[hash % roofPalette.Length]);
        mat.SetFloat(smoothId,   0.05f);
        mat.SetFloat(metallicId, 0.0f);
        return mat;
    }

    private Vector3 GpsToUnity(double lat, double lng, float y = 0f)
    {
        double dLat   = lat - _originLat;
        double dLng   = lng - _originLng;
        double latRad = _originLat * Math.PI / 180.0;
        float z = (float)(dLat * 111_320.0);
        float x = (float)(dLng * 6_378_137.0 * Math.Cos(latRad) * Math.PI / 180.0);
        return new Vector3(x, y, z);
    }

    private long ExtractLong(string text, string key)
    {
        int i = text.IndexOf(key); if (i < 0) return 0;
        int c = text.IndexOf(':', i);
        int e = text.IndexOfAny(new[] { ',', '}' }, c);
        return long.TryParse(text.Substring(c+1, e-c-1).Trim(), out long r) ? r : 0;
    }

    private double ExtractDouble(string text, string key)
    {
        int i = text.IndexOf(key); if (i < 0) return 0;
        int c = text.IndexOf(':', i);
        int e = text.IndexOfAny(new[] { ',', '}' }, c);
        return double.TryParse(text.Substring(c+1, e-c-1).Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double r) ? r : 0;
    }

    private float ExtractBuildingHeight(string wayText)
    {
        string h = ExtractTagValue(wayText, "\"building:height\"");
        if (string.IsNullOrEmpty(h)) h = ExtractTagValue(wayText, "\"height\"");
        if (!string.IsNullOrEmpty(h))
        {
            h = h.Replace("m", "").Replace(" ", "");
            if (float.TryParse(h, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float height))
                return height;
        }
        string lv = ExtractTagValue(wayText, "\"building:levels\"");
        if (!string.IsNullOrEmpty(lv) &&
            float.TryParse(lv, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float levels))
            return levels * metresPerFloor;
        return defaultHeight;
    }

    private string ExtractTagValue(string text, string key)
    {
        int i = text.IndexOf(key); if (i < 0) return "";
        int c = text.IndexOf(':', i); if (c < 0) return "";
        int vs = c + 1;
        while (vs < text.Length && text[vs] == ' ') vs++;
        if (vs >= text.Length) return "";
        if (text[vs] == '"')
        {
            int e = text.IndexOf('"', vs + 1); if (e < 0) return "";
            return text.Substring(vs + 1, e - vs - 1);
        }
        int end = text.IndexOfAny(new[] { ',', '}', '\n' }, vs); if (end < 0) return "";
        return text.Substring(vs, end - vs).Trim();
    }

    private List<long> ExtractNodeIdArray(string wayText)
    {
        var ids = new List<long>();
        int ns = wayText.IndexOf("\"nodes\""); if (ns < 0) return ids;
        int a  = wayText.IndexOf('[', ns);
        int b  = wayText.IndexOf(']', a);
        if (a < 0 || b < 0) return ids;
        foreach (string s in wayText.Substring(a+1, b-a-1).Split(','))
            if (long.TryParse(s.Trim(), out long id)) ids.Add(id);
        return ids;
    }
}