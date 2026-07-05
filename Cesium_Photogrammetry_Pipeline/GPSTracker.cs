using UnityEngine;
using CesiumForUnity;
using System;

public class GPSTracker : MonoBehaviour
{
    [Header("Initial Real-World Coordinates")]
    public double initialLatitude  = 48.893697;
    public double initialLongitude = 8.694218;
    public double initialHeight    = 400.0;

    [Header("Tile Settings")]
    public float tileSizeMetres = 50f;

    // Current GPS — read by TileFetcher and other scripts
    [HideInInspector] public double currentLatitude;
    [HideInInspector] public double currentLongitude;

    // Fired every 50m — TileFetcher listens to this
    public static event Action<double, double> OnNewTileEntered;

    private CesiumGlobeAnchor _anchor;
    private Vector3 _lastTilePosition;

    void Start()
    {
        _anchor = GetComponent<CesiumGlobeAnchor>();

        // Tell Cesium where on Earth the agent starts
        _anchor.longitudeLatitudeHeight = new Unity.Mathematics.double3(
            initialLongitude,
            initialLatitude,
            initialHeight
        );

        currentLatitude  = initialLatitude;
        currentLongitude = initialLongitude;
        _lastTilePosition = transform.position;

        Debug.Log($"[GPSTracker] Spawned at {currentLatitude:F6}, {currentLongitude:F6}");
        OnNewTileEntered?.Invoke(currentLatitude, currentLongitude);
    }

    void Update()
    {
        // Read current GPS directly from Cesium's anchor — no Haversine math needed
        // Cesium handles the Earth geometry for us
        var llh = _anchor.longitudeLatitudeHeight;
        currentLongitude = llh.x;
        currentLatitude  = llh.y;

        float dist = Vector3.Distance(
            new Vector3(transform.position.x, 0, transform.position.z),
            new Vector3(_lastTilePosition.x,  0, _lastTilePosition.z));

        if (dist >= tileSizeMetres)
        {
            _lastTilePosition = transform.position;
            Debug.Log($"[GPSTracker] New tile: {currentLatitude:F6}, {currentLongitude:F6}");
            OnNewTileEntered?.Invoke(currentLatitude, currentLongitude);
        }
    }

    public string GetGPSString() =>
        $"{currentLatitude:F6}, {currentLongitude:F6}";
}
