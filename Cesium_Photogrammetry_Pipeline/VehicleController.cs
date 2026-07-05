using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class VehicleController : MonoBehaviour
{
    [Header("Vehicle Feel")]
    public float maxSpeed        = 15f;
    public float acceleration    = 8f;
    public float braking         = 12f;
    [Tooltip("Maximum yaw turn rate in degrees per second")]
    public float steerSpeed      = 90f;
    [Tooltip("Maximum front-wheel steering angle in degrees")]
    public float maxSteerAngle   = 30f;

    [Header("Ground Detection")]
    [Tooltip("Physics ground layers — RoadProxy only on Cesium branch (not raw photogrammetry)")]
    public LayerMask groundMask;
    public float groundCheckDist = 0.6f;
    [Tooltip("Radius for extra ground samples used to smooth noisy Cesium mesh normals")]
    public float groundSampleRadius = 0.8f;
    [Tooltip("Minimum surface normal alignment with local up to count as drivable road deck")]
    [Range(0.4f, 1f)]
    public float minRoadSurfaceAlignment = 0.55f;
    [Tooltip("Seconds to wait for RoadProxy before falling back to Cesium terrain")]
    public float spawnFallbackSeconds = 25f;
    [Tooltip("Physics layers used when RoadProxy spawn fails")]
    public LayerMask fallbackGroundMask = ~0;
    [Tooltip("How quickly height is smoothed when driving on raw Cesium (no proxy deck)")]
    public float cesiumGroundSmoothSpeed = 8f;
    [Tooltip("How strongly the chassis follows smoothed Cesium height (reduces shaking)")]
    [Range(0f, 1f)]
    public float cesiumHeightFollowStrength = 0.65f;
    [Tooltip("Terrain tilt multiplier when on raw Cesium instead of proxy deck")]
    [Range(0f, 1f)]
    public float cesiumTerrainTiltScale = 0.12f;

    [Header("Terrain Following")]
    [Range(0f, 1f)]
    [Tooltip("How much the chassis tilts to match terrain (0 = stay level, 1 = full slope match)")]
    public float terrainTiltBlend = 0.35f;
    [Tooltip("How quickly the sampled ground normal is smoothed")]
    public float normalSmoothSpeed = 6f;
    [Tooltip("Height above ground when snapping on spawn or fall recovery")]
    public float groundClearance = 0.15f;
    [Tooltip("Fall-recovery triggers when this far below the detected surface")]
    public float fallRecoveryThreshold = 0.5f;
    [Tooltip("Blend vehicle height toward Cesium photogrammetry surface (0 = disabled, use when proxy-only ground)")]
    [Range(0f, 1f)]
    public float visualHeightBlendStrength = 0f;
    [Tooltip("Max vertical correction toward Cesium visual per frame")]
    public float visualHeightMaxStep = 0.35f;
    [Tooltip("Ray length when sampling Cesium visual surface")]
    public float visualHeightRayLength = 12f;
    [Tooltip("Layers treated as Cesium visual ground (exclude RoadProxy/Vehicle)")]
    public LayerMask cesiumVisualMask = ~0;

    [Header("Traction")]
    [Tooltip("How quickly sideways slip is removed (higher = more grip)")]
    public float lateralGrip = 15f;
    [Tooltip("Distance between front and rear axles in metres")]
    public float wheelbase = 2.7f;

    [Header("Rigidbody")]
    [Tooltip("When off, Rigidbody values set in the Inspector are kept as-is")]
    public bool applyRuntimeRigidbodySettings = true;
    public float mass = 1500f;
    public float groundedLinearDamping = 1.5f;
    public float airborneLinearDamping = 0.05f;
    public float angularDamping = 5f;

    [Header("Vehicle Visual Scale")]
    public Vector3 chassisScale = new Vector3(2f, 0.7f, 4.2f);
    public Vector3 cabinScale   = new Vector3(1.6f, 0.7f, 2f);
    public float   wheelScale   = 0.8f;
    public float   wheelWidth   = 0.24f;
    [Tooltip("Wheel track width as a fraction of chassis width")]
    [Range(0.3f, 0.8f)]
    public float trackWidthFactor = 0.525f;
    [Tooltip("Front/rear axle offset as a fraction of chassis length")]
    [Range(0.2f, 0.45f)]
    public float axleOffsetFactor = 0.32f;

    private Rigidbody _rb;
    private float     _currentSpeed = 0f;
    private bool      _isGrounded;
    private bool      _isSpawned = false;
    private bool      _spawnedOnRoadProxy = false;
    private float     _steerInput = 0f;
    private Vector3   _smoothedNormal = Vector3.up;

    private Transform _wheelParentFL;
    private Transform _wheelParentFR;
    private Transform _wheelParentRL;
    private Transform _wheelParentRR;
    private Transform _spinPivotFL;
    private Transform _spinPivotFR;
    private Transform _spinPivotRL;
    private Transform _spinPivotRR;
    private float     _tireSpinAngle = 0f;
    private float     _spawnWaitElapsed = 0f;
    private LayerMask _proxyGroundMask;
    private LayerMask _cesiumGroundMask;
    private bool      _drivingOnProxyDeck;
    private float     _smoothedGroundAlongUp;
    private bool      _groundSmoothInitialized;
    private bool      _loggedCesiumDriving;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _smoothedNormal = transform.up;

        if (groundMask.value == 0)
            groundMask = LayerMask.GetMask("RoadProxy");

        _proxyGroundMask = groundMask;
        _cesiumGroundMask = GetFallbackGroundMask();

        int vehicleLayer = LayerMask.NameToLayer("Vehicle");
        if (vehicleLayer >= 0)
        {
            gameObject.layer = vehicleLayer;
            Physics.IgnoreLayerCollision(vehicleLayer, 0, true);
        }

        if (applyRuntimeRigidbodySettings)
        {
            _rb.mass           = mass;
            _rb.linearDamping  = groundedLinearDamping;
            _rb.angularDamping = angularDamping;
            _rb.interpolation  = RigidbodyInterpolation.Interpolate;
            _rb.constraints    = RigidbodyConstraints.None;
        }

        _rb.useGravity = true;
        _rb.isKinematic = true;

        if (GetComponent<Collider>() == null)
        {
            var col    = gameObject.AddComponent<BoxCollider>();
            col.size   = new Vector3(chassisScale.x, chassisScale.y + cabinScale.y, chassisScale.z);
            col.center = new Vector3(0f, (chassisScale.y + cabinScale.y) * 0.5f, 0f);
        }

        CreateProceduralCar();
    }

    void OnEnable()  => RoadDrivingProxy.OnProxyReady += HandleProxyReady;
    void OnDisable() => RoadDrivingProxy.OnProxyReady -= HandleProxyReady;

    private void HandleProxyReady()
    {
        if (_spawnedOnRoadProxy)
            return;

        if (TrySpawnOnRoadProxy())
            return;

        Debug.LogWarning("[VehicleController] OnProxyReady received but spawn on RoadProxy failed.");
    }

    private bool TrySpawnOnRoadProxy()
    {
        if (RoadDrivingProxy.TryGetSpawnHint(out Vector3 hintPos, out Vector3 hintNormal))
        {
            Vector3 up = transform.up;
            transform.position = hintPos + hintNormal * groundClearance;
            ApplySpawnSnapFromNormal(hintNormal, "RoadProxy hint", roadProxy: true);
            return true;
        }

        if (TrySnapWithMask(_proxyGroundMask, out RaycastHit hit, forSpawn: true))
        {
            ApplySpawnSnap(hit, "RoadProxy", roadProxy: true);
            return true;
        }

        if (TrySnapWithMask(_cesiumGroundMask, out hit, forSpawn: true))
        {
            ApplySpawnSnap(hit, "Cesium surface", roadProxy: false);
            return true;
        }

        return false;
    }

    private void ApplySpawnSnapFromNormal(Vector3 normal, string source, bool roadProxy = false)
    {
        if (normal.sqrMagnitude < 0.0001f)
            normal = transform.up;

        Vector3 projectedForward = Vector3.ProjectOnPlane(transform.forward, normal).normalized;
        if (projectedForward.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.LookRotation(projectedForward, normal);

        _smoothedNormal = normal;
        _rb.isKinematic = false;
        _isSpawned = true;
        if (roadProxy) _spawnedOnRoadProxy = true;
        Debug.Log($"[VehicleController] Snapped to {source}.");
    }

    private bool TrySnapWithMask(LayerMask mask, out RaycastHit hit, bool forSpawn = false)
    {
        Vector3 up = transform.up;
        Vector3 origin = transform.position + up * 500f;
        RaycastHit[] hits = Physics.RaycastAll(new Ray(origin, -up), 1000f, mask);

        hit = default;
        float bestScore = float.MinValue;
        bool found = false;
        float maxAbove = forSpawn ? 80f : 3f;
        float minAlignment = forSpawn ? 0.2f : minRoadSurfaceAlignment;

        foreach (RaycastHit h in hits)
        {
            if (!IsValidGroundHit(h)) continue;

            float alongUp = Vector3.Dot(h.point - transform.position, up);
            if (alongUp > maxAbove || alongUp < -120f) continue;

            Vector3 normal = h.normal;
            float signedAlignment = Vector3.Dot(normal, up);
            float alignment = Mathf.Abs(signedAlignment);
            if (alignment < minAlignment) continue;

            float score = alongUp + alignment * 2f;
            if (h.collider != null && h.collider.gameObject.name == "Deck")
                score += 5f;

            if (score <= bestScore) continue;

            bestScore = score;
            hit = h;
            hit.normal = signedAlignment >= 0f ? normal : -normal;
            found = true;
        }

        return found;
    }

    private void ApplySpawnSnap(RaycastHit hit, string source, bool roadProxy = false)
    {
        transform.position = hit.point + hit.normal * groundClearance;

        Vector3 projectedForward = Vector3.ProjectOnPlane(transform.forward, hit.normal).normalized;
        if (projectedForward.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.LookRotation(projectedForward, hit.normal);

        _smoothedNormal = hit.normal;
        _rb.isKinematic = false;
        _isSpawned = true;
        if (roadProxy) _spawnedOnRoadProxy = true;
        Debug.Log($"[VehicleController] Snapped to {source} at elevation {hit.point.y:F2}m.");
    }

    private void CreateProceduralCar()
    {
        Transform existingCar = transform.Find("car");
        Material chassisMat = null;
        if (existingCar != null)
        {
            var meshRenderer = existingCar.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
                chassisMat = meshRenderer.sharedMaterial;
            existingCar.gameObject.SetActive(false);
        }

        GameObject modelParent = new GameObject("ProceduralCar");
        modelParent.transform.SetParent(transform, false);

        GameObject chassis = GameObject.CreatePrimitive(PrimitiveType.Cube);
        chassis.name = "Chassis";
        Destroy(chassis.GetComponent<Collider>());
        chassis.transform.SetParent(modelParent.transform, false);
        chassis.transform.localPosition = new Vector3(0f, chassisScale.y * 0.5f + 0.1f, 0f);
        chassis.transform.localScale = chassisScale;
        if (chassisMat != null) chassis.GetComponent<MeshRenderer>().sharedMaterial = chassisMat;

        GameObject cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cabin.name = "Cabin";
        Destroy(cabin.GetComponent<Collider>());
        cabin.transform.SetParent(modelParent.transform, false);
        cabin.transform.localPosition = new Vector3(0f, chassisScale.y + cabinScale.y * 0.5f + 0.1f, -chassisScale.z * 0.1f);
        cabin.transform.localScale = cabinScale;
        if (chassisMat != null) cabin.GetComponent<MeshRenderer>().sharedMaterial = chassisMat;

        Material wheelMat = null;
        if (chassisMat != null)
        {
            wheelMat = new Material(chassisMat);
            wheelMat.color = new Color(0.12f, 0.12f, 0.12f);
        }

        float wheelRadius = wheelScale * 0.5f;
        float trackWidth  = chassisScale.x * trackWidthFactor;
        float axleOffset  = chassisScale.z * axleOffsetFactor;
        float wheelY      = wheelRadius;

        (_wheelParentFL, _spinPivotFL) = CreateWheel(modelParent.transform, new Vector3(-trackWidth, wheelY, axleOffset), "Wheel_FL", wheelMat);
        (_wheelParentFR, _spinPivotFR) = CreateWheel(modelParent.transform, new Vector3(trackWidth, wheelY, axleOffset), "Wheel_FR", wheelMat);
        (_wheelParentRL, _spinPivotRL) = CreateWheel(modelParent.transform, new Vector3(-trackWidth, wheelY, -axleOffset), "Wheel_RL", wheelMat);
        (_wheelParentRR, _spinPivotRR) = CreateWheel(modelParent.transform, new Vector3(trackWidth, wheelY, -axleOffset), "Wheel_RR", wheelMat);
    }

    private (Transform steerPivot, Transform spinPivot) CreateWheel(Transform parent, Vector3 localPos, string name, Material mat)
    {
        GameObject steerPivot = new GameObject(name);
        steerPivot.transform.SetParent(parent, false);
        steerPivot.transform.localPosition = localPos;
        steerPivot.transform.localRotation = Quaternion.identity;

        GameObject spinPivot = new GameObject("Spin");
        spinPivot.transform.SetParent(steerPivot.transform, false);
        spinPivot.transform.localPosition = Vector3.zero;
        spinPivot.transform.localRotation = Quaternion.identity;

        GameObject cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cylinder.name = "Tire";
        Destroy(cylinder.GetComponent<Collider>());
        cylinder.transform.SetParent(spinPivot.transform, false);
        cylinder.transform.localPosition = Vector3.zero;
        cylinder.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
        cylinder.transform.localScale = new Vector3(wheelScale, wheelWidth * 0.5f, wheelScale);

        if (mat != null) cylinder.GetComponent<MeshRenderer>().sharedMaterial = mat;

        return (steerPivot.transform, spinPivot.transform);
    }

    void Update()
    {
        if (!_isSpawned)
        {
            HandleSpawnSnapping();
            return;
        }

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

        if (Mathf.Abs(driveInput) > 0.01f)
        {
            float targetSpeed = driveInput * maxSpeed;
            _currentSpeed = Mathf.MoveTowards(_currentSpeed, targetSpeed, acceleration * Time.deltaTime);
        }
        else
        {
            _currentSpeed = Mathf.MoveTowards(_currentSpeed, 0f, braking * Time.deltaTime);
        }
    }

    void FixedUpdate()
    {
        if (!_isSpawned) return;

        HandleFallRecovery();
        SampleGround(out RaycastHit groundHit);

        if (!_isGrounded && _rb.linearVelocity.y > 4f)
        {
            Vector3 v = _rb.linearVelocity;
            v = Vector3.ProjectOnPlane(v, transform.up);
            _rb.linearVelocity = v;
        }

        AnimateWheels();

        Vector3 targetUp = transform.up;
        if (_isGrounded)
        {
            float normalSpeed = _drivingOnProxyDeck ? normalSmoothSpeed : normalSmoothSpeed * 0.35f;
            _smoothedNormal = Vector3.Slerp(
                _smoothedNormal,
                groundHit.normal,
                normalSpeed * Time.fixedDeltaTime).normalized;

            float tiltBlend = _drivingOnProxyDeck
                ? terrainTiltBlend
                : terrainTiltBlend * cesiumTerrainTiltScale;
            targetUp = Vector3.Slerp(Vector3.up, _smoothedNormal, tiltBlend).normalized;
        }

        ApplySteeringAndRotation(targetUp);
        ApplyMovement(groundHit);
        ApplyCesiumGroundSmoothing(groundHit);
        ApplyVisualHeightCorrection(groundHit);
    }

    private void AnimateWheels()
    {
        float wheelRadius = wheelScale * 0.5f;
        float spinDelta = (_currentSpeed / (2f * Mathf.PI * wheelRadius)) * 360f * Time.fixedDeltaTime;
        _tireSpinAngle += spinDelta;

        float steerAngle = _steerInput * maxSteerAngle;
        Quaternion rollSpin = Quaternion.AngleAxis(_tireSpinAngle, Vector3.right);

        SetWheelVisual(_wheelParentFL, _spinPivotFL, steerAngle, rollSpin);
        SetWheelVisual(_wheelParentFR, _spinPivotFR, steerAngle, rollSpin);
        SetWheelVisual(_wheelParentRL, _spinPivotRL, 0f, rollSpin);
        SetWheelVisual(_wheelParentRR, _spinPivotRR, 0f, rollSpin);
    }

    private static void SetWheelVisual(Transform steerPivot, Transform spinPivot, float steerAngle, Quaternion rollSpin)
    {
        if (steerPivot == null || spinPivot == null) return;

        steerPivot.localRotation = Quaternion.AngleAxis(steerAngle, Vector3.up);
        spinPivot.localRotation = rollSpin;
    }

    private void HandleSpawnSnapping()
    {
        _spawnWaitElapsed += Time.deltaTime;

        if (TrySpawnOnRoadProxy())
            return;

        if (_spawnWaitElapsed >= spawnFallbackSeconds
            && TrySnapWithMask(GetFallbackGroundMask(), out RaycastHit fallbackHit, forSpawn: true))
        {
            ApplySpawnSnap(fallbackHit, "Cesium fallback", roadProxy: false);
            Debug.LogWarning("[VehicleController] RoadProxy spawn failed — used Cesium terrain fallback.");
        }
    }

    private LayerMask GetFallbackGroundMask()
    {
        if (fallbackGroundMask.value != 0)
            return fallbackGroundMask;

        int vehicleLayer = LayerMask.NameToLayer("Vehicle");
        int proxyLayer = LayerMask.NameToLayer("RoadProxy");
        LayerMask mask = ~0;
        if (vehicleLayer >= 0)
            mask &= ~(1 << vehicleLayer);
        if (proxyLayer >= 0)
            mask &= ~(1 << proxyLayer);
        return mask;
    }

    private void HandleFallRecovery()
    {
        Vector3 up = transform.up;
        Vector3 origin = transform.position + up * 1f;
        LayerMask recoveryMask = _proxyGroundMask | _cesiumGroundMask;
        if (!Physics.Raycast(new Ray(origin, -up), out RaycastHit safetyHit, 30f, recoveryMask)) return;
        if (!IsValidGroundHit(safetyHit)) return;

        float heightDifference = Vector3.Dot(safetyHit.point - transform.position, up);
        if (heightDifference <= fallRecoveryThreshold || heightDifference > 5f) return;

        Debug.LogWarning($"[VehicleController] Fell below terrain — snapping back to {safetyHit.point.y:F2}m.");
        transform.position = safetyHit.point + safetyHit.normal * groundClearance;
        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
    }

    private void SampleGround(out RaycastHit centerHit)
    {
        Vector3 up = transform.up;
        Vector3 origin = transform.position + up * 0.2f;
        centerHit = default;
        _isGrounded = false;
        _drivingOnProxyDeck = false;

        if (TryPickGroundHit(origin, up, _proxyGroundMask, groundCheckDist + 1.5f, preferDeck: true, out centerHit))
        {
            _drivingOnProxyDeck = centerHit.collider != null && centerHit.collider.gameObject.name == "Deck";
            _isGrounded = true;
            _groundSmoothInitialized = true;
            _loggedCesiumDriving = false;
            SmoothGroundNormal(ref centerHit, origin, up, groundCheckDist + 1.5f, _proxyGroundMask);
            return;
        }

        if (TryPickGroundHit(origin, up, _cesiumGroundMask, groundCheckDist + 3f, preferDeck: false, out centerHit))
        {
            _drivingOnProxyDeck = false;
            _isGrounded = true;

            float rawAlong = Vector3.Dot(centerHit.point - transform.position, up);
            if (!_groundSmoothInitialized)
            {
                _smoothedGroundAlongUp = rawAlong;
                _groundSmoothInitialized = true;
            }
            else
            {
                _smoothedGroundAlongUp = Mathf.Lerp(
                    _smoothedGroundAlongUp,
                    rawAlong,
                    Time.fixedDeltaTime * cesiumGroundSmoothSpeed);
            }

            centerHit.normal = Vector3.Slerp(up, centerHit.normal, 0.2f).normalized;

            if (!_loggedCesiumDriving)
            {
                _loggedCesiumDriving = true;
                Debug.Log("[VehicleController] No proxy deck under wheels — driving on Cesium photogrammetry until proxy builds ahead.");
            }
        }
    }

    private void SmoothGroundNormal(ref RaycastHit centerHit, Vector3 origin, Vector3 up, float rayLength, LayerMask mask)
    {
        Vector3 normalSum = centerHit.normal;
        int sampleCount = 1;

        Vector3 forwardOffset = transform.forward * groundSampleRadius;
        Vector3 rightOffset = transform.right * groundSampleRadius;
        Vector3[] offsets =
        {
            forwardOffset,
            -forwardOffset,
            rightOffset,
            -rightOffset
        };

        foreach (Vector3 offset in offsets)
        {
            if (!Physics.Raycast(origin + offset, -up, out RaycastHit sampleHit, rayLength, mask))
                continue;
            if (!IsValidGroundHit(sampleHit))
                continue;
            if (Vector3.Dot(sampleHit.normal, up) < minRoadSurfaceAlignment)
                continue;

            normalSum += sampleHit.normal;
            sampleCount++;
        }

        centerHit.normal = (normalSum / sampleCount).normalized;
    }

    private bool TryPickGroundHit(
        Vector3 origin,
        Vector3 up,
        LayerMask mask,
        float rayLength,
        bool preferDeck,
        out RaycastHit bestHit)
    {
        bestHit = default;
        float bestScore = float.MinValue;
        bool found = false;
        const float sphereRadius = 0.25f;

        RaycastHit[] hits = Physics.SphereCastAll(origin, sphereRadius, -up, rayLength, mask, QueryTriggerInteraction.Ignore);
        if (hits.Length == 0)
            hits = Physics.RaycastAll(origin, -up, rayLength, mask, QueryTriggerInteraction.Ignore);

        foreach (RaycastHit hit in hits)
        {
            if (!IsValidGroundHit(hit))
                continue;
            if (Vector3.Dot(hit.normal, up) < minRoadSurfaceAlignment)
                continue;

            float alongUp = Vector3.Dot(hit.point - transform.position, up);
            if (alongUp > rayLength || alongUp < -rayLength)
                continue;

            float score = alongUp + Mathf.Abs(Vector3.Dot(hit.normal, up)) * 2f;
            if (preferDeck && hit.collider != null && hit.collider.gameObject.name == "Deck")
                score += 10f;

            if (score <= bestScore)
                continue;

            bestScore = score;
            bestHit = hit;
            found = true;
        }

        return found;
    }

    private void ApplyCesiumGroundSmoothing(RaycastHit groundHit)
    {
        if (!_isGrounded || _drivingOnProxyDeck || cesiumHeightFollowStrength <= 0f)
            return;

        Vector3 up = transform.up;
        float currentAlong = Vector3.Dot(groundHit.point - transform.position, up);
        float error = _smoothedGroundAlongUp - currentAlong;
        if (Mathf.Abs(error) < 0.001f)
            return;

        _rb.MovePosition(_rb.position + up * (error * cesiumHeightFollowStrength));

        Vector3 vel = _rb.linearVelocity;
        float vertical = Vector3.Dot(vel, up);
        vel -= up * (vertical * 0.85f);
        _rb.linearVelocity = vel;
    }

    private void ApplyVisualHeightCorrection(RaycastHit proxyHit)
    {
        if (!_isGrounded || visualHeightBlendStrength <= 0f)
            return;

        if (!TryGetCesiumVisualHit(out RaycastHit visualHit))
            return;

        Vector3 up = transform.up;
        float proxyAlong = Vector3.Dot(proxyHit.point - transform.position, up);
        float visualAlong = Vector3.Dot(visualHit.point - transform.position, up);
        float delta = visualAlong - proxyAlong;

        if (Mathf.Abs(delta) > 5f)
            return;

        float step = Mathf.Clamp(delta * visualHeightBlendStrength, -visualHeightMaxStep, visualHeightMaxStep);
        if (Mathf.Abs(step) < 0.001f)
            return;

        _rb.MovePosition(_rb.position + up * step);
    }

    private bool TryGetCesiumVisualHit(out RaycastHit hit)
    {
        Vector3 up = transform.up;
        Vector3 origin = transform.position + up * 2f;
        LayerMask mask = GetCesiumVisualMask();

        if (Physics.Raycast(origin, -up, out hit, visualHeightRayLength, mask)
            && IsValidGroundHit(hit))
        {
            return true;
        }

        RaycastHit[] hits = Physics.RaycastAll(origin, -up, visualHeightRayLength, mask);
        hit = default;
        float bestAlong = float.MinValue;
        bool found = false;

        foreach (RaycastHit h in hits)
        {
            if (!IsValidGroundHit(h)) continue;

            float along = Vector3.Dot(h.point - transform.position, up);
            if (along > 3f || along < -visualHeightRayLength) continue;
            if (along <= bestAlong) continue;

            bestAlong = along;
            hit = h;
            found = true;
        }

        return found;
    }

    private LayerMask GetCesiumVisualMask()
    {
        if (cesiumVisualMask.value != 0)
            return cesiumVisualMask;

        return GetFallbackGroundMask();
    }

    private void ApplySteeringAndRotation(Vector3 targetUp)
    {
        float yawSpeed = 0f;
        if (_isGrounded && Mathf.Abs(_currentSpeed) > 0.1f)
        {
            float steerDir = Mathf.Sign(_currentSpeed);
            float targetSteerAngle = _steerInput * maxSteerAngle;
            float computedYawRate = (Mathf.Abs(_currentSpeed) / wheelbase)
                * Mathf.Tan(targetSteerAngle * Mathf.Deg2Rad) * Mathf.Rad2Deg;
            computedYawRate = Mathf.Clamp(computedYawRate, -steerSpeed, steerSpeed);
            yawSpeed = computedYawRate * steerDir;
        }

        float steerDelta = yawSpeed * Time.fixedDeltaTime;
        Quaternion yawRotation = transform.rotation * Quaternion.AngleAxis(steerDelta, transform.up);

        Vector3 projectedForward = Vector3.ProjectOnPlane(yawRotation * Vector3.forward, targetUp).normalized;
        if (projectedForward.sqrMagnitude < 0.001f)
            projectedForward = Vector3.ProjectOnPlane(transform.forward, targetUp).normalized;

        _rb.MoveRotation(Quaternion.LookRotation(projectedForward, targetUp));
        _rb.angularVelocity = Vector3.zero;
    }

    private void ApplyMovement(RaycastHit groundHit)
    {
        _rb.linearDamping = _isGrounded ? groundedLinearDamping : airborneLinearDamping;

        Vector3 forwardDir = transform.forward;
        if (_isGrounded)
            forwardDir = Vector3.ProjectOnPlane(transform.forward, groundHit.normal).normalized;

        if (!_isGrounded)
        {
            Vector3 horizVel = Vector3.ProjectOnPlane(_rb.linearVelocity, transform.up);
            _rb.linearVelocity = horizVel + transform.up * _rb.linearVelocity.y;
            return;
        }

        Vector3 rightDir = Vector3.Cross(groundHit.normal, forwardDir).normalized;
        float lateralSpeed = Vector3.Dot(_rb.linearVelocity, rightDir);
        float newLateralSpeed = lateralSpeed * Mathf.Clamp01(1f - lateralGrip * Time.fixedDeltaTime);
        _rb.linearVelocity = forwardDir * _currentSpeed + rightDir * newLateralSpeed;
    }

    private bool IsValidGroundHit(RaycastHit hit)
    {
        return hit.collider.gameObject != gameObject
            && !hit.transform.IsChildOf(transform)
            && !hit.collider.isTrigger;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = _isGrounded ? Color.green : Color.red;
        Gizmos.DrawLine(
            transform.position + transform.up * 0.1f,
            transform.position - transform.up * groundCheckDist);

        Gizmos.color = Color.cyan;
        Vector3 origin = transform.position + transform.up * 0.2f;
        Gizmos.DrawWireSphere(origin + transform.forward * groundSampleRadius, 0.05f);
        Gizmos.DrawWireSphere(origin - transform.forward * groundSampleRadius, 0.05f);
        Gizmos.DrawWireSphere(origin + transform.right * groundSampleRadius, 0.05f);
        Gizmos.DrawWireSphere(origin - transform.right * groundSampleRadius, 0.05f);
    }
}
