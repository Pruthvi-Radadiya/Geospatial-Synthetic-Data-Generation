using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;

public class TileFetcher : MonoBehaviour
{
    [Header("Google API Key")]
    [Tooltip("Paste your Google Cloud API key here")]
    public string apiKey = "YOUR_API_KEY_HERE";

    [Header("Street View Settings")]
    [Tooltip("Resolution of each Street View photo fetched")]
    public int imageSize = 640;

    [Tooltip("Field of view in degrees per photo — 90 gives good overlap at 8 directions")]
    public int fov = 90;

    // Fired when all 8 Street View images for a tile arrive
    // Dictionary key = heading in degrees (0, 45, 90 ... 315)
    public static event Action<double, double, Dictionary<int, Texture2D>> OnStreetViewReady;

    // Fired with a full grid of heights — TerrainGenerator uses this
    // float[,] is a 2D array of heights in metres, rows = Z axis, cols = X axis
    public static event Action<double, double, float[,]> OnElevationGridReady;

    // Fired when elevation data for a tile arrives
    public static event Action<double, double, float> OnElevationReady;

    // Queue prevents overlapping fetches if agent moves fast
    private Queue<(double lat, double lng)> _fetchQueue = new Queue<(double, double)>();
    private bool _isFetching = false;

    // The 8 compass headings we fetch — covers full 360°
    private readonly int[] _headings = { 0, 45, 90, 135, 180, 225, 270, 315 };

    void OnEnable()
    {
        // Subscribe to the GPS tracker's tile event
        GPSTracker.OnNewTileEntered += QueueTileFetch;
    }

    void OnDisable()
    {
        // Always unsubscribe to prevent memory leaks
        GPSTracker.OnNewTileEntered -= QueueTileFetch;
    }

    // ── Queue system ─────────────────────────────────────────────────────────
    // Called by GPSTracker every time agent enters a new tile
    private void QueueTileFetch(double lat, double lng)
    {
        _fetchQueue.Enqueue((lat, lng));
        Debug.Log($"[TileFetcher] Queued fetch for: {lat:F6}, {lng:F6}  (queue size: {_fetchQueue.Count})");

        if (!_isFetching)
            StartCoroutine(ProcessQueue());
    }

    private IEnumerator ProcessQueue()
    {
        _isFetching = true;

        while (_fetchQueue.Count > 0)
        {
            var (lat, lng) = _fetchQueue.Dequeue();
            yield return StartCoroutine(FetchTileData(lat, lng));
        }

        _isFetching = false;
    }

    // ── Main fetch coroutine ─────────────────────────────────────────────────
    // Runs elevation + all 8 Street View fetches for one tile
    private IEnumerator FetchTileData(double lat, double lng)
    {
        Debug.Log($"[TileFetcher] Starting fetch for {lat:F6}, {lng:F6}");

        // --- 1. Fetch elevation first (lightweight JSON call) ----------------
        yield return StartCoroutine(FetchElevation(lat, lng));

        // --- 2. Fetch all 8 Street View directions --------------------------
        var textures = new Dictionary<int, Texture2D>();

        foreach (int heading in _headings)
        {
            yield return StartCoroutine(FetchStreetViewImage(lat, lng, heading, textures));
        }

        // --- 3. Fire the event so other scripts can use the textures --------
        if (textures.Count > 0)
        {
            Debug.Log($"[TileFetcher] Got {textures.Count}/8 Street View images for {lat:F6}, {lng:F6}");
            OnStreetViewReady?.Invoke(lat, lng, textures);
        }
        else
        {
            Debug.LogWarning($"[TileFetcher] No Street View coverage at {lat:F6}, {lng:F6}");
        }
    }

    // ── Elevation API (grid version) ─────────────────────────────────────────
    [Header("Elevation Grid Settings")]
    [Tooltip("How many samples per side of the grid — 9 gives an 81-point grid")]
    public int elevationGridSize = 9;

    [Tooltip("How wide the grid is in metres — should match tile size")]
    public float elevationGridExtent = 50f;

    void Awake()
    {
        // Awake logic removed as TerrainGenerator was deleted
    }

    private IEnumerator FetchElevation(double lat, double lng)
    {
        // Build a grid of GPS points centred on the agent position
        // e.g. 9x9 = 81 points spread over a 50m x 50m area
        List<string> locations = new List<string>();
        float step = elevationGridExtent / (elevationGridSize - 1);
        
        double originLat = lat;
        var gps = FindFirstObjectByType<GPSTracker>();
        if (gps != null) originLat = gps.initialLatitude;

        for (int row = 0; row < elevationGridSize; row++)
        {
            for (int col = 0; col < elevationGridSize; col++)
            {
                // Offset each sample point from centre in metres, then convert to GPS
                float offsetZ = (row - elevationGridSize / 2) * step;
                float offsetX = (col - elevationGridSize / 2) * step;

                double sampleLat = lat + offsetZ / 111_320.0;
                double sampleLng = lng + offsetX / (6_378_137.0 *
                                Math.Cos(originLat * Math.PI / 180.0) * Math.PI / 180.0);

                locations.Add($"{sampleLat:F6},{sampleLng:F6}");
            }
        }

        // Join all points into one API call — much cheaper than 81 separate calls
        string locationsParam = string.Join("|", locations);
        string url = $"https://maps.googleapis.com/maps/api/elevation/json" +
                    $"?locations={locationsParam}&key={apiKey}";

        using var req = UnityWebRequest.Get(url);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[TileFetcher] Elevation grid request failed: {req.error}");
            yield break;
        }

        float[,] heightGrid = ParseElevationGrid(req.downloadHandler.text, elevationGridSize);

        // Still fire the single-point event for anything that needs just one height
        OnElevationReady?.Invoke(lat, lng, heightGrid[elevationGridSize / 2, elevationGridSize / 2]);

        // Fire the grid event for the terrain generator
        OnElevationGridReady?.Invoke(lat, lng, heightGrid);

        Debug.Log($"[TileFetcher] Elevation grid ready — centre height: " +
                $"{heightGrid[elevationGridSize/2, elevationGridSize/2]:F1}m");
    }

    // Parses the array of elevation results from the JSON response
    private float[,] ParseElevationGrid(string json, int gridSize)
    {
        float[,] grid = new float[gridSize, gridSize];
        int index = 0;
        int searchFrom = 0;

        for (int row = 0; row < gridSize; row++)
        {
            for (int col = 0; col < gridSize; col++)
            {
                int elevIndex = json.IndexOf("\"elevation\"", searchFrom);
                if (elevIndex < 0) break;

                int colon = json.IndexOf(':', elevIndex);
                int comma = json.IndexOf(',', colon);
                if (comma < 0) comma = json.IndexOf('}', colon);

                string val = json.Substring(colon + 1, comma - colon - 1).Trim();
                float.TryParse(val,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out grid[row, col]);

                searchFrom = comma;
                index++;
            }
        }

        return grid;
    }

    // ── Street View API ──────────────────────────────────────────────────────
    // Fetches one photo at a given heading and adds it to the dictionary
    private IEnumerator FetchStreetViewImage(
        double lat, double lng, int heading, Dictionary<int, Texture2D> results)
    {
        string url = $"https://maps.googleapis.com/maps/api/streetview" +
                     $"?size={imageSize}x{imageSize}" +
                     $"&location={lat:F6},{lng:F6}" +
                     $"&heading={heading}" +
                     $"&fov={fov}" +
                     $"&pitch=0" +
                     $"&key={apiKey}";

        using var req = UnityWebRequestTexture.GetTexture(url);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[TileFetcher] Street View failed at heading {heading}: {req.error}");
            yield break;
        }

        Texture2D tex = DownloadHandlerTexture.GetContent(req);
        tex.name = $"SV_{lat:F4}_{lng:F4}_H{heading}";

        // Street View returns a grey "no imagery" image if coverage is missing
        // We detect that by checking if it's nearly uniform grey
        if (!IsGreyNoImageryResponse(tex))
            results[heading] = tex;
        else
            Debug.LogWarning($"[TileFetcher] No Street View coverage at heading {heading}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Pulls the elevation value out of the Google Elevation JSON response
    // Example response: {"results":[{"elevation":35.12345,"location":{...}}],"status":"OK"}
    private float ParseElevationFromJson(string json)
    {
        // Simple manual parse — no need for a JSON library for this small response
        int elevIndex = json.IndexOf("\"elevation\"");
        if (elevIndex < 0) return 0f;

        int colonIndex = json.IndexOf(':', elevIndex);
        int commaIndex = json.IndexOf(',', colonIndex);
        if (commaIndex < 0) commaIndex = json.IndexOf('}', colonIndex);

        string valueStr = json.Substring(colonIndex + 1, commaIndex - colonIndex - 1).Trim();
        return float.TryParse(valueStr,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float result) ? result : 0f;
    }

    // Google returns a 640x640 grey placeholder when no Street View exists
    // We sample a few pixels — if they're all near the same grey, it's the placeholder
    private bool IsGreyNoImageryResponse(Texture2D tex)
    {
        // Sample 5 pixels spread across the image
        Color[] samples = {
            tex.GetPixel(10, 10),
            tex.GetPixel(320, 320),
            tex.GetPixel(630, 10),
            tex.GetPixel(10, 630),
            tex.GetPixel(630, 630)
        };

        foreach (Color c in samples)
        {
            // Real photos have colour variation — grey placeholder is near (0.74, 0.74, 0.74)
            float diff = Mathf.Abs(c.r - c.g) + Mathf.Abs(c.g - c.b);
            if (diff > 0.05f) return false;  // has colour = real image
        }
        return true;
    }
}

