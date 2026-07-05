using UnityEngine;
using UnityEngine.InputSystem;

/// Controls the chase camera for driving visualization.
/// ChaseCamera GameObject- child of the vehicle.
/// on the Cesium globe, and toggles displaying it in the Game view when 'C' is pressed.

[RequireComponent(typeof(Camera))]
public class ChaseCameraController : MonoBehaviour
{
    [Header("Ego Camera Reference")]
    [Tooltip("Reference to the EgoCameraController. Auto-detected from sibling 'EgoCam' if left empty.")]
    public EgoCameraController egoCameraController;

    [Header("Runtime State")]
    [SerializeField]
    private bool _isActive = true;

    private Camera _camera;
    private Transform _vehicleTransform;

    void Awake()
    {
        _camera = GetComponent<Camera>();

        _vehicleTransform = transform.parent;

        if (egoCameraController == null && _vehicleTransform != null)
        {
            Transform egoCamTransform = _vehicleTransform.Find("EgoCam");
            if (egoCamTransform != null)
            {
                egoCameraController = egoCamTransform.GetComponent<EgoCameraController>();
            }
        }

        // Configure camera explicitly for Display 0 (Game view)
        _camera.targetDisplay = 0;
        _camera.targetTexture = null;

        ApplyChaseCameraState();
        Debug.Log("[ChaseCameraController] Initialized rigidly as a child of the vehicle.");
    }

    void Start()
    {
        // Apply initial ego camera state to match chase cam being active
        ApplyEgoCameraState();

        Camera[] cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
        foreach (var c in cameras)
        {
            Debug.Log($"[CameraFinder] Name: {c.name}, Tag: {c.tag}, Enabled: {c.enabled}, Display: {c.targetDisplay}, Depth: {c.depth}, ActiveInHierarchy: {c.gameObject.activeInHierarchy}, TargetTexture: {(c.targetTexture != null ? c.targetTexture.name : "null")}");
        }
    }

    void Update()
    {
        // Toggle camera view with 'C' key
        if (Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame)
        {
            _isActive = !_isActive;

            ApplyChaseCameraState();
            ApplyEgoCameraState();

            Debug.Log($"[ChaseCameraController] Switched to {(_isActive ? "CHASE" : "EGO")} camera view");
        }
    }

    private void ApplyChaseCameraState()
    {
        // Disable camera component first to destroy current viewport buffers
        _camera.enabled = false;

        if (_isActive)
        {
            _camera.targetTexture = null;
            _camera.targetDisplay = 0;
            _camera.depth = 10;
            
            // Re-enable camera to force Unity to rebuild viewport buffers for Display 0
            _camera.enabled = true;
            gameObject.tag = "MainCamera";
        }
        else
        {
            _camera.depth = 0;
            gameObject.tag = "Untagged";
            // Remain disabled
        }

        string textureName = _camera.targetTexture != null ? _camera.targetTexture.name : "null";
        Debug.Log($"[ChaseCameraController] ApplyChaseCameraState: targetDisplay={_camera.targetDisplay}, targetTexture={textureName}, enabled={_camera.enabled}, depth={_camera.depth}, tag={gameObject.tag}");
    }

    private void ApplyEgoCameraState()
    {
        if (egoCameraController == null) return;

        if (_isActive)
        {
            // Chase cam is active -> ego cam goes back to data-capture-only mode (Display 1)
            egoCameraController.HideFromScreen();
        }
        else
        {
            // Chase cam is disabled -> ego cam takes over the Game view (Display 0)
            egoCameraController.ShowOnScreen();
        }
    }

    public bool IsActive => _isActive;

    public void SetActive(bool active)
    {
        _isActive = active;
        ApplyChaseCameraState();
        ApplyEgoCameraState();
    }
}
