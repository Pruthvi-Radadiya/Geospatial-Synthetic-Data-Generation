using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;

public class RoadNetworkRenderer : MonoBehaviour
{
    [Header("Fetch Settings")]
    public float fetchRadius = 200f;

    [Header("Road Widths (metres)")]
    public float motorwayWidth    = 9f;
    public float primaryWidth     = 7f;
    public float secondaryWidth   = 5.5f;
    public float residentialWidth = 4f;
    public float serviceWidth     = 2.5f;

    // GPS origin - same pattern as BuildingGenerator
    private double  _originLat, _originLng;
    private bool    _originSet = false;
    private GameObject _roadContainer;
    private double  _latestLat, _latestLng;
    private GPSTracker _gps;

    void Awake() => _gps = FindFirstObjectByType<GPSTracker>();

    // Data class
    private class RoadData
    {
        public List<Vector2d> Points = new List<Vector2d>();
        public string Type = "residential";
    }

    private struct Vector2d
    {
        public double Lat, Lng;
        public Vector2d(double lat, double lng) { Lat=lat; Lng=lng; }
    }

    //Lifecycle
    void OnEnable()  => BuildingGenerator.OnBuildingsFetched += HandleNewTile;
    void OnDisable() => BuildingGenerator.OnBuildingsFetched -= HandleNewTile;

    private void HandleNewTile(double lat, double lng)
    {
        if (!_originSet) { _originLat=lat; _originLng=lng; _originSet=true; }
        _latestLat = lat; _latestLng = lng;
        StartCoroutine(FetchRoads(lat, lng));
    }

    // Overpass fetch
    private IEnumerator FetchRoads(double lat, double lng)
    {
        // Only fetch drivable road types — exclude footpaths, bike lanes etc.
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

            // Discard stale response if tile changed while fetching
            if (lat != _latestLat || lng != _latestLng) yield break;

            if (req.result == UnityWebRequest.Result.Success)
            {
                responseText = req.downloadHandler.text;
                break;
            }

            Debug.LogWarning($"[RoadRenderer] Overpass API failed (attempt {i+1}/{retries}): {req.error}");
            if (i < retries - 1) yield return new WaitForSeconds(2f);
        }

        if (string.IsNullOrEmpty(responseText))
        {
            Debug.LogWarning($"[RoadRenderer] Giving up on roads after {retries} attempts.");
            yield break;
        }

        var roads = ParseRoads(responseText);
        Debug.Log($"[RoadRenderer] Rendering {roads.Count} roads");
        SpawnRoads(roads, lat, lng);
    }

    // OSM parser
    private List<RoadData> ParseRoads(string json)
    {
        var roads   = new List<RoadData>();
        var nodeMap = new Dictionary<long, Vector2d>();

        string[] parts = json.Split(
            new[] { "\"type\":" }, StringSplitOptions.RemoveEmptyEntries);

        // Pass 1 — collect GPS nodes
        foreach (string p in parts)
        {
            if (!p.TrimStart().StartsWith("\"node\"")) continue;
            long   id  = ExtractLong(p,   "\"id\"");
            double lat = ExtractDouble(p, "\"lat\"");
            double lng = ExtractDouble(p, "\"lon\"");
            if (id != 0) nodeMap[id] = new Vector2d(lat, lng);
        }

        // Pass 2 — resolve road ways
        foreach (string p in parts)
        {
            if (!p.TrimStart().StartsWith("\"way\"")) continue;
            if (!p.Contains("\"highway\"")) continue;

            var road = new RoadData { Type = ExtractTagValue(p, "\"highway\"") };
            foreach (long id in ExtractNodeIds(p))
                if (nodeMap.TryGetValue(id, out Vector2d c)) road.Points.Add(c);

            if (road.Points.Count >= 2) roads.Add(road);
        }

        return roads;
    }

    // Scene construction
    private void SpawnRoads(List<RoadData> roads, double lat, double lng)
    {
        if (_roadContainer != null) Destroy(_roadContainer);
        _roadContainer = new GameObject($"Roads_{lat:F4}_{lng:F4}");
        _roadContainer.transform.position = Vector3.zero;

        // Dark asphalt material
        var mat = new Material(Shader.Find("HDRP/Lit"));
        mat.SetColor("_BaseColor", new Color(0.13f, 0.13f, 0.13f));
        mat.SetFloat("_Smoothness", 0.2f);

        foreach (var road in roads)
            BuildRoadMesh(road, mat);
    }

    private void BuildRoadMesh(RoadData road, Material mat)
    {
        float halfW  = GetRoadWidth(road.Type) / 2f;
        float yBump  = 0.3f;  // sits above terrain to prevent z-fighting

        var verts = new List<Vector3>();
        var tris  = new List<int>();
        var uvs   = new List<Vector2>();

        for (int i = 0; i < road.Points.Count - 1; i++)
        {
            Vector3 p1 = GpsToUnity(road.Points[i].Lat,   road.Points[i].Lng);
            Vector3 p2 = GpsToUnity(road.Points[i+1].Lat, road.Points[i+1].Lng);

            // Snap each endpoint to whatever is directly below it (terrain surface)
            p1.y = SampleGroundHeight(p1) + yBump;
            p2.y = SampleGroundHeight(p2) + yBump;

            // Road width direction = perpendicular to travel direction
            Vector3 dir  = (p2 - p1);
            if (dir.sqrMagnitude < 0.001f) continue;
            dir.Normalize();
            Vector3 perp = new Vector3(dir.z, 0, -dir.x) * halfW;

            int v = verts.Count;
            verts.Add(p1 - perp);
            verts.Add(p1 + perp);
            verts.Add(p2 - perp);
            verts.Add(p2 + perp);

            tris.AddRange(new[] { v, v+2, v+1,   v+1, v+2, v+3 });

            float segLen = Vector3.Distance(p1, p2) / (halfW * 2f);
            uvs.AddRange(new[] {
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(0, segLen), new Vector2(1, segLen)
            });
        }

        if (verts.Count == 0) return;

        var mesh = new Mesh();
        mesh.vertices  = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.uv        = uvs.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var go = new GameObject($"Road_{road.Type}");
        go.transform.SetParent(_roadContainer.transform);
        go.transform.position = Vector3.zero;
        go.AddComponent<MeshFilter>().mesh = mesh;
        go.AddComponent<MeshRenderer>().material = mat;
    }

    // Helpers

    // Raycast downward from above to find terrain surface height
    private float SampleGroundHeight(Vector3 worldPos)
    {
        var ray = new Ray(new Vector3(worldPos.x, 10000f, worldPos.z), Vector3.down);
        RaycastHit[] hits = Physics.RaycastAll(ray, 20000f);
        
        foreach (var hit in hits)
        {
            if (hit.collider.GetComponent<TerrainGenerator>() != null || hit.collider.gameObject.name.Contains("Terrain"))
            {
                return hit.point.y;
            }
        }
        
        return 0f;
    }

    private float GetRoadWidth(string type) => type switch
    {
        "motorway" or "trunk"                        => motorwayWidth,
        "primary"  or "primary_link"                 => primaryWidth,
        "secondary" or "secondary_link"              => secondaryWidth,
        "residential" or "living_street"             => residentialWidth,
        "service"                                    => serviceWidth,
        _                                            => residentialWidth
    };

    private Vector3 GpsToUnity(double lat, double lng, float y = 0f)
    {
        double dLat   = lat - _originLat;
        double dLng   = lng - _originLng;
        double latRad = _originLat * Math.PI / 180.0;
        float z = (float)(dLat * 111_320.0);
        float x = (float)(dLng * 6_378_137.0 * Math.Cos(latRad) * Math.PI / 180.0);
        return new Vector3(x, y, z);
    }

    // Minimal JSON helpers
    private long ExtractLong(string s, string k) {
        int i=s.IndexOf(k); if(i<0) return 0;
        int c=s.IndexOf(':',i), e=s.IndexOfAny(new[]{',','}'},c);
        return long.TryParse(s.Substring(c+1,e-c-1).Trim(), out long r)?r:0; }

    private double ExtractDouble(string s, string k) {
        int i=s.IndexOf(k); if(i<0) return 0;
        int c=s.IndexOf(':',i), e=s.IndexOfAny(new[]{',','}'},c);
        return double.TryParse(s.Substring(c+1,e-c-1).Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double r)?r:0; }

    private string ExtractTagValue(string s, string k) {
        int i=s.IndexOf(k); if(i<0) return "";
        int c=s.IndexOf(':',i); if(c<0) return "";
        int vs=c+1; while(vs<s.Length && s[vs]==' ') vs++;
        if(vs>=s.Length) return "";
        if(s[vs]=='"'){int e=s.IndexOf('"',vs+1); return e<0?"":s.Substring(vs+1,e-vs-1);}
        int end=s.IndexOfAny(new[]{',','}','\n'},vs); return end<0?"":s.Substring(vs,end-vs).Trim(); }

    private List<long> ExtractNodeIds(string s) {
        var ids=new List<long>();
        int ns=s.IndexOf("\"nodes\""); if(ns<0) return ids;
        int a=s.IndexOf('[',ns), b=s.IndexOf(']',a); if(a<0||b<0) return ids;
        foreach(string v in s.Substring(a+1,b-a-1).Split(','))
            if(long.TryParse(v.Trim(),out long id)) ids.Add(id);
        return ids; }
}