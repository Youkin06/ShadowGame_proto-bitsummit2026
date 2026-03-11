using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.HandTracking;
using UnityEngine;

[DisallowMultipleComponent]
public class HandPinchGrabController : MonoBehaviour
{
  private const int HandLandmarkCount = 21;
  private const int WristIndex = 0;
  private const int ThumbTipIndex = 4;
  private const int IndexTipIndex = 8;
  private const int IndexMcpIndex = 5;
  private const int MiddleMcpIndex = 9;
  private const int PinkyMcpIndex = 17;

  [System.Serializable]
  private struct HandState
  {
    public bool detected;
    public float x;
    public float y;
    public float depth;
    public bool pinch;
  }

  [Header("MediaPipe")]
  [SerializeField] private HandTrackingGraph handTrackingGraph;
  [SerializeField] private Camera worldCamera;

  [Header("Cursor (Sphere)")]
  [SerializeField] private Transform cursorSphere;
  [SerializeField] private float cursorSphereDiameter = 0.22f;
  [SerializeField] private UnityEngine.Color cursorIdleColor = new UnityEngine.Color(0f, 0.898f, 1f, 1f);
  [SerializeField] private UnityEngine.Color cursorPinchColor = new UnityEngine.Color(1f, 0.922f, 0.231f, 1f);
  [SerializeField] private float cursorPlaneY = 0f;

  [Header("Grabbing")]
  [SerializeField] private LayerMask grabbableLayerMask = 1 << 3;
  [SerializeField] private float reach = 2.3f;
  [SerializeField] private Vector2 xRange = new Vector2(-4.5f, 4.5f);
  [SerializeField] private Vector2 yRange = new Vector2(0f, 9f);
  [SerializeField] private Vector2 zRange = new Vector2(-4.5f, 4.5f);
  [SerializeField] private float followLerpX = 0.3f;
  [SerializeField] private float followLerpY = 0.3f;
  [SerializeField] private float followLerpZ = 0.2f;
  [SerializeField] private bool restoreIsKinematicOnRelease = false;
  [SerializeField] private bool restoreUseGravityOnRelease = false;
  [SerializeField] private float cursorLerpX = 0.35f;
  [SerializeField] private float cursorLerpZ = 0.22f;
  [SerializeField] private float xSensitivity = 0.65f;
  [SerializeField] private float zSensitivity = 0.65f;

  [Header("Fail Safe")]
  [SerializeField] private float handLostTimeoutSeconds = 0.25f;

  private readonly object _frameLock = new object();
  private readonly Vector3[] _latestLandmarks = new Vector3[HandLandmarkCount];
  private readonly Vector3[] _frameLandmarks = new Vector3[HandLandmarkCount];
  private readonly Collider[] _overlapColliders = new Collider[64];

  private HandState _handState;
  private bool _prevPinch;
  private bool _hasFrame;
  private bool _hasHandOnLatestFrame;
  private long _latestFrameTicks;
  private bool _isSubscribed;

  private bool _handDepthCalibrated;
  private int _handDepthCalibFrames;
  private float _handDepthCalibMin = float.PositiveInfinity;
  private float _handDepthCalibMax = float.NegativeInfinity;
  private float _handDepthRangeMin = 0.05f;
  private float _handDepthRangeMax = 0.14f;
  private bool _invertDepthDirection;
  private bool _depthDirectionResolved;
  private float _lastPalmScale = float.NaN;
  private float _lastDepthHint = float.NaN;
  private float _depthDirectionScore;
  private int _depthDirectionSamples;
  private bool _xMirrorResolved;
  private bool _xShouldMirror;

  private Rigidbody _grabbedBody;
  private Vector3 _grabOffset;
  private bool _grabbedOriginalIsKinematic;
  private bool _grabbedOriginalUseGravity;

  private Renderer _cursorRenderer;
  private Material _cursorMaterial;
  private bool _createdCursorAtRuntime;
  private Coroutine _subscribeCoroutine;
  private Vector3 _smoothedCursorPoint;
  private bool _hasSmoothedCursorPoint;
  private int _cachedCursorFrame = -1;
  private Vector3 _cachedCursorPoint;

  private void Reset()
  {
    handTrackingGraph = GetComponent<HandTrackingGraph>();
    worldCamera = Camera.main;
    if (grabbableLayerMask.value == 0)
    {
      var grabbableLayer = LayerMask.NameToLayer("Grabbable");
      if (grabbableLayer >= 0)
      {
        grabbableLayerMask = 1 << grabbableLayer;
      }
    }
  }

  private void Awake()
  {
    InitializeHandState();
    EnsureReferences();
    EnsureCursorSphere();
  }

  private void OnEnable()
  {
    StartSubscriptionLoop();
  }

  private void OnDisable()
  {
    StopSubscriptionLoop();
    UnsubscribeFromGraph();
    ReleaseGrabbedObject();
    SetCursorVisible(false);
  }

  private void OnDestroy()
  {
    StopSubscriptionLoop();
    UnsubscribeFromGraph();
    if (_cursorMaterial != null)
    {
      Destroy(_cursorMaterial);
      _cursorMaterial = null;
    }
    if (_createdCursorAtRuntime && cursorSphere != null)
    {
      Destroy(cursorSphere.gameObject);
      cursorSphere = null;
    }
  }

  private void OnValidate()
  {
    NormalizeRange(ref xRange);
    NormalizeRange(ref yRange);
    NormalizeRange(ref zRange);
    cursorSphereDiameter = Mathf.Max(0.01f, cursorSphereDiameter);
    reach = Mathf.Max(0f, reach);
    handLostTimeoutSeconds = Mathf.Max(0.01f, handLostTimeoutSeconds);
    followLerpX = Mathf.Clamp01(followLerpX);
    followLerpY = Mathf.Clamp01(followLerpY);
    followLerpZ = Mathf.Clamp01(followLerpZ);
    cursorLerpX = Mathf.Clamp01(cursorLerpX);
    cursorLerpZ = Mathf.Clamp01(cursorLerpZ);
    xSensitivity = Mathf.Clamp(xSensitivity, 0.1f, 2f);
    zSensitivity = Mathf.Clamp(zSensitivity, 0.1f, 2f);
  }

  private void Update()
  {
    ProcessLatestHandFrame();
    UpdateHandInteraction();
  }

  private void EnsureReferences()
  {
    if (handTrackingGraph == null)
    {
      handTrackingGraph = GetComponent<HandTrackingGraph>();
    }
    if (worldCamera == null)
    {
      worldCamera = Camera.main;
    }
  }

  private void EnsureCursorSphere()
  {
    if (cursorSphere == null)
    {
      var cursorObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
      cursorObject.name = "HandCursorSphere";
      cursorObject.transform.SetParent(transform, false);
      var collider = cursorObject.GetComponent<Collider>();
      if (collider != null)
      {
        Destroy(collider);
      }
      cursorSphere = cursorObject.transform;
      _createdCursorAtRuntime = true;
    }

    cursorSphere.localScale = Vector3.one * cursorSphereDiameter;
    if (worldCamera != null)
    {
      cursorSphere.position = GetCursorPointOnPlane();
    }
    _cursorRenderer = cursorSphere.GetComponent<Renderer>();
    if (_cursorRenderer != null)
    {
      _cursorMaterial = _cursorRenderer.material;
      ApplyCursorColor(false);
    }
    cursorSphere.gameObject.hideFlags = HideFlags.None;
    SetCursorVisible(false);
  }

  private void StartSubscriptionLoop()
  {
    if (_subscribeCoroutine != null)
    {
      return;
    }

    _subscribeCoroutine = StartCoroutine(SubscribeWhenGraphIsReady());
  }

  private void StopSubscriptionLoop()
  {
    if (_subscribeCoroutine == null)
    {
      return;
    }

    StopCoroutine(_subscribeCoroutine);
    _subscribeCoroutine = null;
  }

  private IEnumerator SubscribeWhenGraphIsReady()
  {
    while (!_isSubscribed && isActiveAndEnabled)
    {
      SubscribeToGraph();
      if (_isSubscribed)
      {
        yield break;
      }

      yield return null;
    }
  }

  private void SubscribeToGraph()
  {
    if (_isSubscribed || handTrackingGraph == null)
    {
      return;
    }

    try
    {
      handTrackingGraph.OnHandLandmarksOutput += OnHandLandmarksOutput;
      _isSubscribed = true;
    }
    catch (System.NullReferenceException)
    {
      // Graph streams are not initialized yet; retry next frame.
      _isSubscribed = false;
    }
  }

  private void UnsubscribeFromGraph()
  {
    if (!_isSubscribed || handTrackingGraph == null)
    {
      return;
    }

    try
    {
      handTrackingGraph.OnHandLandmarksOutput -= OnHandLandmarksOutput;
    }
    catch (System.NullReferenceException)
    {
      // Graph is already disposed/stopped.
    }
    _isSubscribed = false;
  }

  private void OnHandLandmarksOutput(object stream, OutputEventArgs<List<NormalizedLandmarkList>> eventArgs)
  {
    var hasHand = TryGetFirstHand(eventArgs == null ? null : eventArgs.value, out var handLandmarks);

    lock (_frameLock)
    {
      _hasFrame = true;
      _latestFrameTicks = Stopwatch.GetTimestamp();
      _hasHandOnLatestFrame = hasHand;

      if (!hasHand)
      {
        return;
      }

      for (var i = 0; i < HandLandmarkCount; i++)
      {
        var landmark = handLandmarks.Landmark[i];
        _latestLandmarks[i] = new Vector3(landmark.X, landmark.Y, landmark.Z);
      }
    }
  }

  private static bool TryGetFirstHand(IList<NormalizedLandmarkList> handLandmarks, out NormalizedLandmarkList firstHand)
  {
    firstHand = null;
    if (handLandmarks == null || handLandmarks.Count == 0)
    {
      return false;
    }

    firstHand = handLandmarks[0];
    return firstHand != null && firstHand.Landmark != null && firstHand.Landmark.Count >= HandLandmarkCount;
  }

  private void ProcessLatestHandFrame()
  {
    bool hasFrame;
    bool hasHand;
    long frameTicks;

    lock (_frameLock)
    {
      hasFrame = _hasFrame;
      hasHand = _hasHandOnLatestFrame;
      frameTicks = _latestFrameTicks;

      if (hasFrame && hasHand)
      {
        System.Array.Copy(_latestLandmarks, _frameLandmarks, HandLandmarkCount);
      }
    }

    if (!hasFrame)
    {
      SetHandUndetected();
      return;
    }

    var elapsedSeconds = (Stopwatch.GetTimestamp() - frameTicks) / (double)Stopwatch.Frequency;
    if (elapsedSeconds > handLostTimeoutSeconds || !hasHand)
    {
      SetHandUndetected();
      return;
    }

    UpdateHandState(_frameLandmarks);
  }

  private void UpdateHandState(Vector3[] landmarks)
  {
    _handState.detected = true;

    var indexTip = landmarks[IndexTipIndex];
    var thumbTip = landmarks[ThumbTipIndex];

    _handState.x = Mathf.Lerp(_handState.x, ToSceneNormalizedX(indexTip.x), 0.55f);
    _handState.y = Mathf.Lerp(_handState.y, indexTip.y, 0.55f);

    var palmScale = GetPalmScale(landmarks);
    UpdateHandDepthFromPalmScale(palmScale);
    UpdateDepthDirection(palmScale, indexTip.z);

    var pinchDistance = Vector3.Distance(indexTip, thumbTip);
    const float pinchOnThreshold = 0.05f;
    const float pinchOffThreshold = 0.075f;
    _handState.pinch = _handState.pinch
      ? pinchDistance < pinchOffThreshold
      : pinchDistance < pinchOnThreshold;
  }

  private float GetPalmScale(Vector3[] landmarks)
  {
    var wrist = landmarks[WristIndex];
    var indexMcp = landmarks[IndexMcpIndex];
    var middleMcp = landmarks[MiddleMcpIndex];
    var pinkyMcp = landmarks[PinkyMcpIndex];

    var tri = (Distance2D(wrist, indexMcp) + Distance2D(wrist, pinkyMcp) + Distance2D(indexMcp, pinkyMcp)) / 3f;
    var spine = Distance2D(wrist, middleMcp);
    return tri * 0.7f + spine * 0.3f;
  }

  private static float Distance2D(Vector3 a, Vector3 b)
  {
    var dx = a.x - b.x;
    var dy = a.y - b.y;
    return Mathf.Sqrt(dx * dx + dy * dy);
  }

  private void UpdateHandDepthFromPalmScale(float palmScale)
  {
    if (!_handDepthCalibrated)
    {
      _handDepthCalibMin = Mathf.Min(_handDepthCalibMin, palmScale);
      _handDepthCalibMax = Mathf.Max(_handDepthCalibMax, palmScale);
      _handDepthCalibFrames++;

      if (_handDepthCalibFrames >= 12)
      {
        var initialSpan = Mathf.Max(_handDepthCalibMax - _handDepthCalibMin, 0.02f);
        _handDepthRangeMin = _handDepthCalibMin - initialSpan * 0.45f;
        _handDepthRangeMax = _handDepthCalibMax + initialSpan * 0.45f;
        _handDepthCalibrated = true;
      }
      else
      {
        return;
      }
    }
    else
    {
      const float edgeMargin = 0.004f;
      if (palmScale < _handDepthRangeMin + edgeMargin)
      {
        _handDepthRangeMin = Mathf.Lerp(_handDepthRangeMin, palmScale - edgeMargin, 0.22f);
      }
      if (palmScale > _handDepthRangeMax - edgeMargin)
      {
        _handDepthRangeMax = Mathf.Lerp(_handDepthRangeMax, palmScale + edgeMargin, 0.22f);
      }
      if (_handDepthRangeMax - _handDepthRangeMin < 0.07f)
      {
        _handDepthRangeMax = _handDepthRangeMin + 0.07f;
      }
    }

    var normalizedDepth = Mathf.Clamp01((palmScale - _handDepthRangeMin) / (_handDepthRangeMax - _handDepthRangeMin));
    normalizedDepth = Mathf.Clamp01((normalizedDepth - 0.5f) * 0.95f + 0.5f);

    const float deadZone = 0.04f;
    if (Mathf.Abs(normalizedDepth - _handState.depth) < deadZone)
    {
      normalizedDepth = _handState.depth;
    }

    _handState.depth = Mathf.Lerp(_handState.depth, normalizedDepth, 0.16f);
  }

  private void UpdateHandInteraction()
  {
    if (worldCamera == null)
    {
      SetCursorVisible(false);
      ReleaseGrabbedObject();
      _prevPinch = false;
      return;
    }

    if (!_handState.detected)
    {
      SetCursorVisible(false);
      ReleaseGrabbedObject();
      _prevPinch = false;
      return;
    }

    var cursorPoint = GetCursorPointOnPlane();
    SetCursorVisible(true);
    UpdateCursor(cursorPoint, _handState.pinch);

    var pinchStarted = _handState.pinch && !_prevPinch;
    var pinchEnded = !_handState.pinch && _prevPinch;
    _prevPinch = _handState.pinch;

    if (pinchStarted && _grabbedBody == null)
    {
      TryBeginGrab();
    }

    if (_grabbedBody != null && _handState.pinch)
    {
      UpdateGrabbedObject();
    }

    if (pinchEnded)
    {
      ReleaseGrabbedObject();
    }
  }

  private Vector3 GetCursorPointOnPlane()
  {
    if (_cachedCursorFrame == Time.frameCount)
    {
      return _cachedCursorPoint;
    }

    var rawPoint = ComputeRawCursorPointOnPlane();
    if (!_hasSmoothedCursorPoint)
    {
      _smoothedCursorPoint = rawPoint;
      _hasSmoothedCursorPoint = true;
    }
    else
    {
      _smoothedCursorPoint.x = Mathf.Lerp(_smoothedCursorPoint.x, rawPoint.x, cursorLerpX);
      _smoothedCursorPoint.z = Mathf.Lerp(_smoothedCursorPoint.z, rawPoint.z, cursorLerpZ);
      _smoothedCursorPoint.y = cursorPlaneY;
    }

    _cachedCursorPoint = _smoothedCursorPoint;
    _cachedCursorFrame = Time.frameCount;
    return _cachedCursorPoint;
  }

  private Vector3 ComputeRawCursorPointOnPlane()
  {
    var depthZ = Mathf.Clamp(GetHandDepthWorldZ(), zRange.x, zRange.y);
    var xNorm = ApplySensitivity(Mathf.Clamp01(_handState.x), xSensitivity);
    var point = new Vector3(Mathf.Lerp(xRange.x, xRange.y, xNorm), cursorPlaneY, depthZ);

    var viewportX = xNorm;
    if (TryProjectToFloorPlane(viewportX, 0.5f, out var floorPoint)
        || TryProjectToFloorPlane(viewportX, 0.45f, out floorPoint)
        || TryProjectToFloorPlane(viewportX, 0.55f, out floorPoint))
    {
      point.x = Mathf.Clamp(floorPoint.x, xRange.x, xRange.y);
    }

    // Keep floor-projection behavior but compress movement range by sensitivity.
    var xCenter = (xRange.x + xRange.y) * 0.5f;
    point.x = Mathf.Lerp(xCenter, point.x, Mathf.Clamp01(xSensitivity));

    return point;
  }

  private Vector3 GetHandTargetPoint()
  {
    var cursorPoint = GetCursorPointOnPlane();
    var handY = Mathf.Lerp(yRange.x, yRange.y, 1f - Mathf.Clamp01(_handState.y));
    return new Vector3(cursorPoint.x, Mathf.Clamp(handY, yRange.x, yRange.y), cursorPoint.z);
  }

  private float GetHandDepthWorldZ()
  {
    var normalizedDepth = Mathf.Clamp01(_handState.depth);
    if (_invertDepthDirection)
    {
      normalizedDepth = 1f - normalizedDepth;
    }
    normalizedDepth = ApplySensitivity(normalizedDepth, zSensitivity);
    // Requirement: hand closer to camera => +Z side.
    return Mathf.Lerp(zRange.x, zRange.y, 1f - normalizedDepth);
  }

  private void TryBeginGrab()
  {
    var handTarget = GetHandTargetPoint();
    var hitCount = Physics.OverlapSphereNonAlloc(handTarget, reach, _overlapColliders, grabbableLayerMask, QueryTriggerInteraction.Ignore);

    Rigidbody nearestBody = null;
    var nearestDistanceSq = float.MaxValue;

    for (var i = 0; i < hitCount; i++)
    {
      var collider = _overlapColliders[i];
      if (collider == null)
      {
        continue;
      }

      var body = collider.attachedRigidbody;
      if (body == null)
      {
        continue;
      }
      if ((grabbableLayerMask.value & (1 << body.gameObject.layer)) == 0)
      {
        continue;
      }

      var distanceSq = (body.worldCenterOfMass - handTarget).sqrMagnitude;
      if (distanceSq >= nearestDistanceSq)
      {
        continue;
      }

      nearestDistanceSq = distanceSq;
      nearestBody = body;
    }

    if (nearestBody == null)
    {
      return;
    }

    _grabbedBody = nearestBody;
    _grabOffset = _grabbedBody.position - handTarget;
    _grabbedOriginalIsKinematic = _grabbedBody.isKinematic;
    _grabbedOriginalUseGravity = _grabbedBody.useGravity;

    _grabbedBody.linearVelocity = Vector3.zero;
    _grabbedBody.angularVelocity = Vector3.zero;
    _grabbedBody.isKinematic = true;
    _grabbedBody.useGravity = false;
  }

  private void UpdateGrabbedObject()
  {
    if (_grabbedBody == null)
    {
      return;
    }

    var handTarget = GetHandTargetPoint();
    var tx = Mathf.Clamp(handTarget.x + _grabOffset.x, xRange.x, xRange.y);
    var ty = Mathf.Clamp(handTarget.y + _grabOffset.y, yRange.x, yRange.y);
    var tz = Mathf.Clamp(handTarget.z + _grabOffset.z, zRange.x, zRange.y);

    var current = _grabbedBody.position;
    var next = new Vector3(
      Mathf.Lerp(current.x, tx, followLerpX),
      Mathf.Lerp(current.y, ty, followLerpY),
      Mathf.Lerp(current.z, tz, followLerpZ));

    _grabbedBody.position = next;
    _grabbedBody.linearVelocity = Vector3.zero;
    _grabbedBody.angularVelocity = Vector3.zero;
  }

  private void ReleaseGrabbedObject()
  {
    if (_grabbedBody == null)
    {
      return;
    }

    _grabbedBody.linearVelocity = Vector3.zero;
    _grabbedBody.angularVelocity = Vector3.zero;

    if (restoreIsKinematicOnRelease)
    {
      _grabbedBody.isKinematic = _grabbedOriginalIsKinematic;
    }
    if (restoreUseGravityOnRelease)
    {
      _grabbedBody.useGravity = _grabbedOriginalUseGravity;
    }

    _grabbedBody = null;
  }

  private void InitializeHandState()
  {
    _handState.detected = false;
    _handState.x = 0.5f;
    _handState.y = 0.5f;
    _handState.depth = 0.5f;
    _handState.pinch = false;
  }

  private void SetHandUndetected()
  {
    _handState.detected = false;
    _handState.pinch = false;
    _lastPalmScale = float.NaN;
    _lastDepthHint = float.NaN;
    _cachedCursorFrame = -1;
  }

  private void UpdateCursor(Vector3 position, bool isPinching)
  {
    if (cursorSphere == null)
    {
      return;
    }

    cursorSphere.position = position;
    cursorSphere.localScale = Vector3.one * cursorSphereDiameter;
    ApplyCursorColor(isPinching);
  }

  private void SetCursorVisible(bool visible)
  {
    if (cursorSphere != null && cursorSphere.gameObject.activeSelf != visible)
    {
      cursorSphere.gameObject.SetActive(visible);
    }
  }

  private void ApplyCursorColor(bool isPinching)
  {
    if (_cursorMaterial == null)
    {
      return;
    }

    var color = isPinching ? cursorPinchColor : cursorIdleColor;
    _cursorMaterial.color = color;
    if (_cursorMaterial.HasProperty("_EmissionColor"))
    {
      _cursorMaterial.SetColor("_EmissionColor", color * 0.2f);
    }
  }

  private static void NormalizeRange(ref Vector2 range)
  {
    if (range.x > range.y)
    {
      var temp = range.x;
      range.x = range.y;
      range.y = temp;
    }
  }

  private static float ApplySensitivity(float normalizedValue, float sensitivity)
  {
    return Mathf.Clamp01(0.5f + (normalizedValue - 0.5f) * sensitivity);
  }

  private bool TryProjectToFloorPlane(float viewportX, float viewportY, out Vector3 hitPoint)
  {
    hitPoint = Vector3.zero;
    var ray = worldCamera.ViewportPointToRay(new Vector3(viewportX, viewportY, 0f));
    var floorPlane = new Plane(Vector3.up, new Vector3(0f, cursorPlaneY, 0f));
    if (!floorPlane.Raycast(ray, out var distance))
    {
      return false;
    }

    hitPoint = ray.GetPoint(distance);
    return true;
  }

  private float ToSceneNormalizedX(float rawX)
  {
    var normalizedX = Mathf.Clamp01(rawX);
    var imageSource = ImageSourceProvider.ImageSource;
    if (imageSource == null)
    {
      return normalizedX;
    }

    if (!_xMirrorResolved && imageSource.isPrepared)
    {
      // Match the same mirroring rule used by annotation rendering.
      _xShouldMirror = true ^ imageSource.isHorizontallyFlipped ^ imageSource.isFrontFacing;
      _xMirrorResolved = true;
    }

    return _xMirrorResolved && _xShouldMirror ? 1f - normalizedX : normalizedX;
  }

  private void UpdateDepthDirection(float palmScale, float indexTipZ)
  {
    if (_depthDirectionResolved)
    {
      return;
    }

    var depthHint = -indexTipZ;
    if (float.IsNaN(_lastPalmScale) || float.IsNaN(_lastDepthHint))
    {
      _lastPalmScale = palmScale;
      _lastDepthHint = depthHint;
      return;
    }

    var deltaPalm = palmScale - _lastPalmScale;
    var deltaHint = depthHint - _lastDepthHint;
    _lastPalmScale = palmScale;
    _lastDepthHint = depthHint;

    if (Mathf.Abs(deltaPalm) < 0.00001f || Mathf.Abs(deltaHint) < 0.00001f)
    {
      return;
    }

    _depthDirectionScore += deltaPalm * deltaHint;
    _depthDirectionSamples++;
    if (_depthDirectionSamples >= 24)
    {
      _invertDepthDirection = _depthDirectionScore < 0f;
      _depthDirectionResolved = true;
    }
  }
}
