using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class TerrainGenerator : MonoBehaviour
{
    [Header("References")]
    public Transform agentTransform;

    [Header("API")]
    [Tooltip("Same Google API key as TileFetcher")]
    public string apiKey = "";

    [Header("Terrain Size")]
    [Tooltip("Half-width of terrain in metres — 200 means 400x400m total")]
    public float terrainExtent = 200f;

    [Tooltip("Grid resolution each side — 9x9=81 points, within API 512 limit")]
    public int gridSize = 9;

    [Header("Streaming")]
    [Tooltip("Re-fetch elevation when agent moves this far from last fetch centre")]
    public float refetchDistance = 90f;

    // Internal state
    private Mesh   _mesh;
    private float  _baseElevation;
    private bool   _baseSet      = false;
    private Vector3 _lastFetchWorldPos = Vector3.one * float.MaxValue;
    private GPSTracker _gps;

    public static double LatestLat { get; private set; } = double.MinValue;
    public static double LatestLng { get; private set; } = double.MinValue;

    public static event Action<double, double> OnTerrainRebuilt;

    // Unity lifecycle
    void Awake()

    {
        _gps  = FindFirstObjectByType<GPSTracker>();

        _mesh = new Mesh { name = "StreamingTerrain" };
        GetComponent<MeshFilter>().mesh = _mesh;

        // Dark ground colour - roads will sit on top with a darker overlay
        var mr = GetComponent<MeshRenderer>();
        mr.material = new Material(Shader.Find("HDRP/Lit"));
        mr.material.SetColor("_BaseColor", new Color(0.38f, 0.36f, 0.30f));

        // emergency ground so agent doesn't fall through on startup
        BuildFlatMesh();
        EnsureColliders();
    }

    void OnEnable()
    {
        TileFetcher.OnElevationGridReady += HandleElevationGridReady;
    }

    void OnDisable()
    {
        TileFetcher.OnElevationGridReady -= HandleElevationGridReady;
    }

    // Elevation handling
    private void HandleElevationGridReady(double lat, double lng, float[,] grid)
    {
        // Ensure grid size matches
        if (grid.GetLength(0) != gridSize || grid.GetLength(1) != gridSize)
        {
            Debug.LogWarning("[TerrainGenerator] Grid size mismatch with TileFetcher");
            return;
        }

        float centreH = grid[gridSize / 2, gridSize / 2];

        if (!_baseSet) { _baseElevation = centreH; _baseSet = true; }

        // Position the terrain exactly where the GPS coordinate of the tile is
        Vector3 worldPos = _gps.GpsToUnity(lat, lng);
        transform.position = worldPos;

        BuildMesh(grid);

        Physics.SyncTransforms();

        var ap = agentTransform.position;
        StartCoroutine(SnapAgentAfterPhysicsUpdate(ap, centreH));

        Debug.Log($"[TerrainGenerator] Rebuilt at {lat:F5},{lng:F5} h={centreH:F1}m");
        LatestLat = lat;
        LatestLng = lng;

        OnTerrainRebuilt?.Invoke(lat, lng);
    }

    private IEnumerator SnapAgentAfterPhysicsUpdate(Vector3 ap, float centreH)
    {
        yield return new WaitForFixedUpdate();

        var agentRb = agentTransform.GetComponent<Rigidbody>();
        var ray = new Ray(new Vector3(ap.x, 10000f, ap.z), Vector3.down);
        RaycastHit[] hits = Physics.RaycastAll(ray, 20000f);
        bool foundGround = false;
        
        foreach (var hit in hits)
        {
            if (hit.collider.gameObject == gameObject || hit.collider.name.Contains("Terrain"))
            {
                float newY = hit.point.y + 1.5f;
                if (agentRb != null) { agentRb.position = new Vector3(ap.x, newY, ap.z); }
                else { agentTransform.position = new Vector3(ap.x, newY, ap.z); }
                foundGround = true;
                break;
            }
        }
        
        if (!foundGround) 
        {
            float relH = centreH - _baseElevation;
            float newY = relH + 1.5f;
            if (agentRb != null) { agentRb.position = new Vector3(ap.x, newY, ap.z); }
            else { agentTransform.position = new Vector3(ap.x, newY, ap.z); }
        }
    }

    // Mesh construction
    private void BuildFlatMesh()
    {
        BuildMesh(new float[gridSize, gridSize]);
    }

    private void BuildMesh(float[,] heights)
    {
        int total = gridSize * gridSize;
        var verts = new Vector3[total];
        var uvs   = new Vector2[total];
        float step       = (terrainExtent * 2f) / (gridSize - 1);
        float halfExtent = terrainExtent;

        for (int row = 0; row < gridSize; row++)
        for (int col = 0; col < gridSize; col++)
        {
            int i    = row * gridSize + col;
            float x  = col * step - halfExtent;
            float z  = row * step - halfExtent;
            float y  = _baseSet ? heights[row, col] - _baseElevation : 0f;
            verts[i] = new Vector3(x, y, z);
            uvs[i]   = new Vector2((float)col / (gridSize - 1),
                                   (float)row / (gridSize - 1));
        }

        int[] tris = new int[(gridSize - 1) * (gridSize - 1) * 6];
        int t = 0;
        for (int row = 0; row < gridSize - 1; row++)
        for (int col = 0; col < gridSize - 1; col++)
        {
            int bl = row * gridSize + col;
            int br = bl + 1, tl = bl + gridSize, tr = tl + 1;
            tris[t++]=bl; tris[t++]=tl; tris[t++]=br;
            tris[t++]=br; tris[t++]=tl; tris[t++]=tr;
        }

        _mesh = new Mesh { name = "StreamingTerrain" };
        _mesh.vertices  = verts;
        _mesh.triangles = tris;
        _mesh.uv        = uvs;
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();

        GetComponent<MeshFilter>().mesh = _mesh;
        EnsureColliders();
    }

    private void EnsureColliders()
    {
        // Remove any placeholder BoxCollider that might intercept raycasts
        var bc = GetComponent<BoxCollider>();
        if (bc != null) Destroy(bc);

        // MeshCollider for accurate terrain surface collision
        var mc = GetComponent<MeshCollider>();
        if (mc == null) mc = gameObject.AddComponent<MeshCollider>();
        mc.sharedMesh = null;
        mc.sharedMesh = _mesh;
    }

    private float[,] ParseGrid(string json)
    {
        var grid = new float[gridSize, gridSize];
        int from = 0;
        for (int row = 0; row < gridSize; row++)
        for (int col = 0; col < gridSize; col++)
        {
            int ei = json.IndexOf("\"elevation\"", from); if (ei < 0) break;
            int c  = json.IndexOf(':', ei);
            int e  = json.IndexOfAny(new[] { ',', '}' }, c);
            float.TryParse(json.Substring(c+1, e-c-1).Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out grid[row, col]);
            from = e;
        }
        return grid;
    }
}