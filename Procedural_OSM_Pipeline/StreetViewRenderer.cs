using UnityEngine;
using System.Collections.Generic;

public class StreetViewRenderer : MonoBehaviour
{
    [Header("Panel Settings")]
    [Tooltip("How far from the agent each photo panel sits")]
    public float ringRadius = 40f;

    [Tooltip("How tall each photo panel is in Unity metres")]
    public float panelHeight = 20f;

    [Tooltip("How wide each photo panel is in Unity metres")]
    public float panelWidth = 30f;

    // Parent object that holds all panels for the current tile
    // We destroy and rebuild this every time a new tile loads
    private GameObject _currentRing;

    void OnEnable()
    {
        TileFetcher.OnStreetViewReady += BuildPhotoRing;
    }

    void OnDisable()
    {
        TileFetcher.OnStreetViewReady -= BuildPhotoRing;
    }

    // ── Main builder ─────────────────────────────────────────────────────────
    private void BuildPhotoRing(double lat, double lng, Dictionary<int, Texture2D> textures)
    {
        // Remove the previous tile's panels before building new ones
        if (_currentRing != null)
            Destroy(_currentRing);

        // New empty parent to keep the Hierarchy clean
        _currentRing = new GameObject($"StreetViewRing_{lat:F4}_{lng:F4}");
        _currentRing.transform.SetParent(this.transform);
        _currentRing.transform.localPosition = Vector3.zero;

        foreach (var kvp in textures)
        {
            int heading = kvp.Key;       // 0, 45, 90 ... 315
            Texture2D tex = kvp.Value;

            CreatePanel(heading, tex);
        }

        Debug.Log($"[StreetViewRenderer] Built ring with {textures.Count} panels " +
                  $"at {lat:F6}, {lng:F6}");
    }

    // ── Panel builder ─────────────────────────────────────────────────────────
    private void CreatePanel(int heading, Texture2D tex)
    {
        // 1. Create a Unity Quad (a flat rectangle, 1x1 by default)
        GameObject panel = GameObject.CreatePrimitive(PrimitiveType.Quad);
        panel.name = $"SV_Panel_{heading}deg";
        panel.transform.SetParent(_currentRing.transform);

        // 2. Position it in a ring around the agent
        //    heading 0 = North (Unity +Z), 90 = East (Unity +X)
        float headingRad = heading * Mathf.Deg2Rad;
        float x = Mathf.Sin(headingRad) * ringRadius;
        float z = Mathf.Cos(headingRad) * ringRadius;
        float y = panelHeight / 2f;   // centre the panel vertically at eye level

        panel.transform.localPosition = new Vector3(x, y, z);

        // 3. Rotate so the panel faces inward toward the agent
        panel.transform.LookAt(this.transform.position + Vector3.up * y);

        // 4. Scale to our desired panel dimensions
        panel.transform.localScale = new Vector3(panelWidth, panelHeight, 1f);

        // 5. Apply the Street View texture to the panel's material
        MeshRenderer renderer = panel.GetComponent<MeshRenderer>();

        // Use Unlit shader — we want to see the raw photo without Unity lighting
        // affecting or washing out the colours
        Material mat = new Material(Shader.Find("Unlit/Texture"));
        mat.mainTexture = tex;
        renderer.material = mat;

        // 6. Remove the collider — we don't want physics on photo panels
        DestroyImmediate(panel.GetComponent<MeshCollider>());

        // Hide the panel from all cameras — it exists only as a texture source
        // The textures are already applied to buildings, we don't need to see the panels
        panel.layer = LayerMask.NameToLayer("TransparentFX"); // invisible layer trick
        renderer.enabled = false;
    }

    // ── Scene view helper ─────────────────────────────────────────────────────
    // Draws the ring radius in the Scene view so you can see the layout
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, ringRadius);
    }
}
