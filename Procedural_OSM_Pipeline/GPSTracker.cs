using UnityEngine;
using System;

public class GPSTracker : MonoBehaviour
{
    [Header("Initial Real-World Coordinates")]
    [Tooltip("Paste any real-world GPS latitude here, e.g. 48.8584 for Eiffel Tower")]
    public double initialLatitude  = 48.8584;   // default: Paris
    public double initialLongitude = 2.2945;

    [Header("Tile Settings")]
    [Tooltip("How many metres the agent must move before we fetch new map data")]
    public float tileSizeMetres = 50f;

    // These update every frame - other scripts read these
    [HideInInspector] public double currentLatitude;
    [HideInInspector] public double currentLongitude;

    // Fired when agent crosses into a new tile - tile scripts listen to this
    public static event Action<double, double> OnNewTileEntered;

    private Vector3 _lastTileFetchPosition;
    private Vector3 _originWorldPosition;

    // Earth radius in metres (WGS-84 mean)
    private const double EarthRadius = 6_378_137.0;

    void Start()
    {
        // Remember where in Unity space we started
        _originWorldPosition    = transform.position;
        _lastTileFetchPosition  = transform.position;

        // GPS starts at whatever the Inspector values are
        currentLatitude  = initialLatitude;
        currentLongitude = initialLongitude;

        Debug.Log($"[GPSTracker] Spawned at GPS: {currentLatitude:F6}, {currentLongitude:F6}");

        // Fire once immediately so the first tile loads on spawn
        OnNewTileEntered?.Invoke(currentLatitude, currentLongitude);
    }

    void Update()
    {
        // 1. How far has the agent moved from its Unity spawn point?
        Vector3 worldDelta = transform.position - _originWorldPosition;

        // 2. Convert Unity metres -> GPS offset using inverse Haversine
        //    Unity X = East/West,  Unity Z = North/South
        currentLatitude  = initialLatitude  + MetresToLatDegrees(worldDelta.z);
        currentLongitude = initialLongitude + MetresToLonDegrees(worldDelta.x, initialLatitude);

        // 3. Check if we've moved far enough to need new tile data
        float distanceSinceLastFetch = Vector3.Distance(
            new Vector3(transform.position.x, 0, transform.position.z),
            new Vector3(_lastTileFetchPosition.x, 0, _lastTileFetchPosition.z)
        );

        if (distanceSinceLastFetch >= tileSizeMetres)
        {
            _lastTileFetchPosition = transform.position;
            Debug.Log($"[GPSTracker] New tile at GPS: {currentLatitude:F6}, {currentLongitude:F6}");
            OnNewTileEntered?.Invoke(currentLatitude, currentLongitude);
        }
    }

    // Coordinate math

    // Convert metres northward into degrees of latitude
    // Simple at this scale: 1 degree lat ≈ 111,320 metres everywhere on Earth
    private double MetresToLatDegrees(double metres)
    {
        return metres / 111_320.0;
    }

    // Convert metres eastward into degrees of longitude
    // Longitude degrees SHRINK as you move toward the poles - cos(lat) corrects for that
    private double MetresToLonDegrees(double metres, double atLatitude)
    {
        double latRad = atLatitude * Math.PI / 180.0;
        return metres / (EarthRadius * Math.Cos(latRad)) * (180.0 / Math.PI);
    }

    // Public helper

    public Vector3 GpsToUnity(double lat, double lng)
    {
        double dLat = lat - initialLatitude;
        double dLng = lng - initialLongitude;
        double latRad = initialLatitude * Math.PI / 180.0;
        float z = (float)(dLat * 111_320.0);
        float x = (float)(dLng * EarthRadius * Math.Cos(latRad) * Math.PI / 180.0);
        return new Vector3(x, 0f, z) + new Vector3(_originWorldPosition.x, 0f, _originWorldPosition.z);
    }

    public string GetGPSString()
    {
        return $"{currentLatitude:F6}, {currentLongitude:F6}";
    }

    // Draw the tile boundary in the Scene
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(_lastTileFetchPosition, tileSizeMetres);
    }
}