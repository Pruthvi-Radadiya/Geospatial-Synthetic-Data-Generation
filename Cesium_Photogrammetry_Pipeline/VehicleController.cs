using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class VehicleController : MonoBehaviour
{
    [Header("Vehicle Feel")]
    public float maxSpeed        = 15f;   // metres/sec (~54 km/h)
    public float acceleration    = 22f;   // how quickly it reaches max speed
    public float braking         = 25f;   // how quickly it stops 
    public float steerSpeed      = 90f;   // degrees per second turning

    [Header("Ground Detection")]
    [Tooltip("Which layers count as ground — Default includes terrain + buildings")]
    public LayerMask groundMask  = Physics.DefaultRaycastLayers;
    public float groundCheckDist = 0.6f;  // how far below to look for ground

    [Header("Vehicle Visual Scale (Adjustable in Inspector)")]
    public Vector3 chassisScale = new Vector3(2f, 0.7f, 4.2f); // Width, Height, Length
    public Vector3 cabinScale   = new Vector3(1.6f, 0.7f, 2f); 
    public float   wheelScale   = 0.8f;                         // Diameter of the tires
    public float   wheelWidth   = 0.24f;                        

    private Rigidbody _rb;
    private float     _currentSpeed = 0f;
    private bool      _isGrounded;

    // Spawn snapping state
    private bool      _isSpawned = false;

    // Steering input cache for FixedUpdate
    private float     _steerInput = 0f;

    // Visuals & Wheel Animation
    private Transform _wheelParentFL;
    private Transform _wheelParentFR;
    private Transform _wheelParentRL;
    private Transform _wheelParentRR;
    private float     _tireSpinAngle = 0f;



    void Awake()
    {
        _rb = GetComponent<Rigidbody>();

        // Rigidbody settings for a car-like feel - could be shifted to unity's Rigidbody defaults if desired
        _rb.mass              = 1500f;      // kg - realistic car weight
        _rb.linearDamping     = 1.5f;       // grounded linear damping
        _rb.angularDamping    = 5f;
        _rb.interpolation     = RigidbodyInterpolation.Interpolate;
        _rb.constraints       = RigidbodyConstraints.None;

        // Make sure gravity is ON
        _rb.useGravity = true;

        // Temporarily set kinematic to prevent falling into the void before tiles load
        _rb.isKinematic = true;

        // Add a box collider sized like a car body if none exists
        if (GetComponent<Collider>() == null)
        {
            var col         = gameObject.AddComponent<BoxCollider>();
            col.size        = new Vector3(chassisScale.x, chassisScale.y + cabinScale.y, chassisScale.z);
            col.center      = new Vector3(0f, (chassisScale.y + cabinScale.y) * 0.5f, 0f);
        }

        // Generate the procedural car model
        CreateProceduralCar();
    }



    private void CreateProceduralCar()
    {
        // Find and disable the original simple "car" cube if it exists
        Transform existingCar = transform.Find("car");
        Material chassisMat = null;
        if (existingCar != null)
        {
            var meshRenderer = existingCar.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                chassisMat = meshRenderer.sharedMaterial;
            }
            existingCar.gameObject.SetActive(false);
        }

        // Create a root parent for our beautiful procedural model components
        GameObject modelParent = new GameObject("ProceduralCar");
        modelParent.transform.SetParent(this.transform, false);
        modelParent.transform.localPosition = Vector3.zero;
        modelParent.transform.localRotation = Quaternion.identity;

        // 1. Lower Chassis
        GameObject chassis = GameObject.CreatePrimitive(PrimitiveType.Cube);
        chassis.name = "Chassis";
        Destroy(chassis.GetComponent<Collider>());
        chassis.transform.SetParent(modelParent.transform, false);
        chassis.transform.localPosition = new Vector3(0f, chassisScale.y * 0.5f + 0.1f, 0f);
        chassis.transform.localScale = chassisScale;
        if (chassisMat != null) chassis.GetComponent<MeshRenderer>().sharedMaterial = chassisMat;

        // 2. Cabin Structure
        GameObject cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cabin.name = "Cabin";
        Destroy(cabin.GetComponent<Collider>());
        cabin.transform.SetParent(modelParent.transform, false);
        cabin.transform.localPosition = new Vector3(0f, chassisScale.y + cabinScale.y * 0.5f + 0.1f, -chassisScale.z * 0.1f);
        cabin.transform.localScale = cabinScale;
        if (chassisMat != null) cabin.GetComponent<MeshRenderer>().sharedMaterial = chassisMat;

        // Create a dark tire material based on chassis material
        Material wheelMat = null;
        if (chassisMat != null)
        {
            wheelMat = new Material(chassisMat);
            wheelMat.color = new Color(0.12f, 0.12f, 0.12f);
        }

        // 3. Assemble and position the 4 wheels dynamically matching the chassis track/wheelbase
        float wheelRadius = wheelScale * 0.5f;
        float trackWidth  = chassisScale.x * 0.525f; // slightly outside chassis
        float wheelBase   = chassisScale.z * 0.32f;  // front/rear axle positions
        float wheelY      = wheelRadius;

        _wheelParentFL = CreateWheel(modelParent.transform, new Vector3(-trackWidth, wheelY, wheelBase), "Wheel_FL", wheelMat);
        _wheelParentFR = CreateWheel(modelParent.transform, new Vector3(trackWidth, wheelY, wheelBase), "Wheel_FR", wheelMat);
        _wheelParentRL = CreateWheel(modelParent.transform, new Vector3(-trackWidth, wheelY, -wheelBase), "Wheel_RL", wheelMat);
        _wheelParentRR = CreateWheel(modelParent.transform, new Vector3(trackWidth, wheelY, -wheelBase), "Wheel_RR", wheelMat);
    }

    private Transform CreateWheel(Transform parent, Vector3 localPos, string name, Material mat)
    {
        GameObject wParent = new GameObject(name);
        wParent.transform.SetParent(parent, false);
        wParent.transform.localPosition = localPos;
        wParent.transform.localRotation = Quaternion.identity;

        GameObject cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cylinder.name = "Tire";
        Destroy(cylinder.GetComponent<Collider>());
        cylinder.transform.SetParent(wParent.transform, false);
        cylinder.transform.localPosition = Vector3.zero;
        cylinder.transform.localRotation = Quaternion.Euler(0f, 0f, 90f); // Rotate axle horizontal
        cylinder.transform.localScale = new Vector3(wheelScale, wheelWidth * 0.5f, wheelScale); // Cylinder Y is height (width/2)

        if (mat != null) cylinder.GetComponent<MeshRenderer>().sharedMaterial = mat;

        return wParent.transform;
    }


    void Update()
    {
        if (!_isSpawned)
        {
            HandleSpawnSnapping();
            return;
        }

        // Input collection
        float steerInput = 0f;
        float driveInput = 0f;

        if (Keyboard.current != null)
        {
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) steerInput += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) steerInput -= 1f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) driveInput += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) driveInput -= 1f;
        }

        _steerInput = steerInput;

        // Speed calculation
        if (Mathf.Abs(driveInput) > 0.01f)
        {
            float targetSpeed = driveInput * maxSpeed;
            _currentSpeed = Mathf.MoveTowards(_currentSpeed, targetSpeed, acceleration * Time.deltaTime);
        }
        else
        {
            _currentSpeed = Mathf.MoveTowards(_currentSpeed, 0f, braking * Time.deltaTime);
        }

        // Animate Wheels
        float wheelRadius = wheelScale * 0.5f;
        float spinDelta = (_currentSpeed / (2f * Mathf.PI * wheelRadius)) * 360f * Time.deltaTime;
        _tireSpinAngle = (_tireSpinAngle + spinDelta) % 360f;

        float steerAngle = steerInput * 30f; // Max 30 degrees steering visual

        // Front wheels (Spin + Steering turn)
        if (_wheelParentFL != null) _wheelParentFL.localRotation = Quaternion.Euler(0f, steerAngle, _tireSpinAngle);
        if (_wheelParentFR != null) _wheelParentFR.localRotation = Quaternion.Euler(0f, steerAngle, _tireSpinAngle);

        // Rear wheels (Spin only)
        if (_wheelParentRL != null) _wheelParentRL.localRotation = Quaternion.Euler(0f, 0f, _tireSpinAngle);
        if (_wheelParentRR != null) _wheelParentRR.localRotation = Quaternion.Euler(0f, 0f, _tireSpinAngle);
    }



    private void HandleSpawnSnapping()
    {
        // Cast a ray downwards to find where the Cesium colliders are loading
        Vector3 rayOrigin = transform.position + transform.up * 500f;
        Ray ray = new Ray(rayOrigin, -transform.up);
        
        if (Physics.Raycast(ray, out RaycastHit hit, 1000f, groundMask))
        {
            // Ignore hits with the agent itself, its children, or trigger volumes
            if (hit.collider.gameObject != gameObject && !hit.transform.IsChildOf(transform) && !hit.collider.isTrigger)
            {
                transform.position = hit.point + transform.up * 0.15f; // vehicle sits slightly above the ground to avoid clipping

                Vector3 projectedForward = Vector3.ProjectOnPlane(transform.forward, hit.normal).normalized;
                transform.rotation = Quaternion.LookRotation(projectedForward, hit.normal);

                
                _rb.isKinematic = false;  // Release kinematic state to start standard physics
                _isSpawned = true;
                Debug.Log($"[VehicleController] Snapped successfully to terrain at elevation {hit.point.y:F2}m.");
            }
        }
    }

    void FixedUpdate()
    {
        if (!_isSpawned) return;

        // Terrain Fall Safety Snap
        // If the vehicle falls through the terrain mesh (e.g. during Cesium LOD tile swaps), 
        // raycast from high above down to snap it back onto the road.
        Vector3 safetyOrigin = transform.position + transform.up * 100f;
        Ray safetyRay = new Ray(safetyOrigin, -transform.up);
        if (Physics.Raycast(safetyRay, out RaycastHit safetyHit, 200f, groundMask))
        {
            if (safetyHit.collider.gameObject != gameObject && !safetyHit.transform.IsChildOf(transform) && !safetyHit.collider.isTrigger)
            {
                float heightDifference = Vector3.Dot(safetyHit.point - transform.position, transform.up);
                if (heightDifference > 0.5f) // We are at least 0.5m below the terrain surface
                {
                    Debug.LogWarning($"[VehicleController] Vehicle fell below terrain! Snapping back up from {transform.position.y:F2}m to {safetyHit.point.y:F2}m.");
                    transform.position = safetyHit.point + transform.up * 0.15f;
                    _rb.linearVelocity = Vector3.zero;
                    _rb.angularVelocity = Vector3.zero;
                }
            }
        }

        // Globe-Safe Ground check
        _isGrounded = Physics.Raycast(
            transform.position + transform.up * 0.1f,
            -transform.up,
            out RaycastHit groundHit,
            groundCheckDist + 0.1f,
            groundMask);

        // Steering & Slope Alignment (Globe-Safe & Rigidbody-compatible)
        Vector3 targetUp = transform.up;
        if (_isGrounded)
        {
            targetUp = groundHit.normal;
        }

        // Speed-adaptive steering using a wheel-angle formula
        float yawSpeed = 0f;
        if (Mathf.Abs(_currentSpeed) > 0.1f)
        {
            float steerDir = Mathf.Sign(_currentSpeed);
            
            float wheelbase = chassisScale.z * 0.64f; // ~2.7m base for a 4.2m chassis
            float targetSteerAngle = _steerInput * 30f; // degrees of steering wheel angle
            
            // Convert angle to rad and compute yaw rate: w = (v / L) * tan(theta)
            // Capped at 90 degrees/sec to maintain high-speed stability
            float computedYawRate = (Mathf.Abs(_currentSpeed) / wheelbase) * Mathf.Tan(targetSteerAngle * Mathf.Deg2Rad) * Mathf.Rad2Deg;
            computedYawRate = Mathf.Clamp(computedYawRate, -90f, 90f);
            
            yawSpeed = computedYawRate * steerDir;
        }

        // Apply yaw rotation around current local up vector
        float steerDelta = yawSpeed * Time.fixedDeltaTime;
        Quaternion yawRotation = transform.rotation * Quaternion.AngleAxis(steerDelta, transform.up);

        // Project the yaw-rotated forward vector onto the target ground normal plane to maintain forward direction
        Vector3 projectedForward = Vector3.ProjectOnPlane(yawRotation * Vector3.forward, targetUp).normalized;
        
        Quaternion finalRotation = Quaternion.LookRotation(projectedForward, targetUp);
        _rb.MoveRotation(finalRotation);
        _rb.angularVelocity = Vector3.zero;

        // Adaptive Damping
        _rb.linearDamping = _isGrounded ? 1.5f : 0.05f;

        // Movement & Traction Logic
        Vector3 forwardDir = transform.forward;
        if (_isGrounded)
        {
            forwardDir = Vector3.ProjectOnPlane(transform.forward, groundHit.normal).normalized;
        }

        if (_isGrounded)
        {
            // find sideways slip component
            Vector3 rightDir = Vector3.Cross(groundHit.normal, forwardDir).normalized;
            float lateralSpeed = Vector3.Dot(_rb.linearVelocity, rightDir);

            // simulate high tire grip
            float newLateralSpeed = lateralSpeed * Mathf.Clamp01(1f - 15f * Time.fixedDeltaTime);

            _rb.linearVelocity = forwardDir * _currentSpeed + rightDir * newLateralSpeed;
        }
        else
        {
            // Airborne: Let standard physics engine/gravity determine vertical speed.
            Vector3 horizVel = Vector3.ProjectOnPlane(_rb.linearVelocity, transform.up);
            _rb.linearVelocity = horizVel + transform.up * _rb.linearVelocity.y;
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = _isGrounded ? Color.green : Color.red;
        Gizmos.DrawLine(
            transform.position + transform.up * 0.1f,
            transform.position - transform.up * groundCheckDist);
    }
}