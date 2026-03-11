using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(HandTrackingProvider))]
[RequireComponent(typeof(HandWorldMapper))]
public class PinchController : MonoBehaviour
{
    [Header("Pinch")]
    [SerializeField] private float _pinchStartDistance = 0.045f;
    [SerializeField] private float _pinchReleaseDistance = 0.06f;
    [SerializeField] private bool _prioritizeRightHand = true;

    [Header("Grabbing")]
    [SerializeField] private float _grabSearchRadius = 0.4f;
    [SerializeField] private float _followSharpness = 20f;
    [SerializeField] private bool _requireRigidbody = true;
    [SerializeField] private string _grabbableLayerName = "Grabbable";
    [SerializeField] private bool _allowTagFallback = false;
    [SerializeField] private string _grabbableTag = "Grabbable";

    private readonly Collider[] _overlapBuffer = new Collider[24];

    private HandTrackingProvider _provider;
    private HandWorldMapper _mapper;

    private bool _leftPinching;
    private bool _rightPinching;

    private Transform _grabbedTransform;
    private Rigidbody _grabbedRigidbody;
    private TrackedHand _grabbedByHand;
    private Vector3 _grabOffset;

    private bool _previousKinematic;
    private bool _previousUseGravity;

    private int _grabbableLayer = -1;
    private bool _warnedMissingLayer;

    public bool IsHoldingObject => _grabbedTransform != null;

    public bool IsPinching(TrackedHand hand)
    {
        return hand == TrackedHand.Left ? _leftPinching : _rightPinching;
    }

    private void Awake()
    {
        _provider = GetComponent<HandTrackingProvider>();
        _mapper = GetComponent<HandWorldMapper>();
        ResolveGrabbableLayer();
    }

    private void OnValidate()
    {
        if (_pinchReleaseDistance < _pinchStartDistance)
        {
            _pinchReleaseDistance = _pinchStartDistance;
        }

        _grabSearchRadius = Mathf.Max(0.01f, _grabSearchRadius);
    }

    private void OnDisable()
    {
        ReleaseObject();
    }

    private void Update()
    {
        EnsureDependencies();
        if (_provider == null || _mapper == null)
        {
            return;
        }

        var hadLeftPinching = _leftPinching;
        var hadRightPinching = _rightPinching;

        _leftPinching = EvaluatePinch(_leftPinching, TrackedHand.Left);
        _rightPinching = EvaluatePinch(_rightPinching, TrackedHand.Right);

        if (_grabbedTransform != null)
        {
            UpdateGrabbedObject();
            return;
        }

        if (_prioritizeRightHand)
        {
            TryStartGrab(TrackedHand.Right, hadRightPinching, _rightPinching);
            if (_grabbedTransform == null)
            {
                TryStartGrab(TrackedHand.Left, hadLeftPinching, _leftPinching);
            }
        }
        else
        {
            TryStartGrab(TrackedHand.Left, hadLeftPinching, _leftPinching);
            if (_grabbedTransform == null)
            {
                TryStartGrab(TrackedHand.Right, hadRightPinching, _rightPinching);
            }
        }
    }

    private void EnsureDependencies()
    {
        if (_provider == null)
        {
            _provider = GetComponent<HandTrackingProvider>();
            if (_provider == null)
            {
                _provider = FindObjectOfType<HandTrackingProvider>();
            }
        }

        if (_mapper == null)
        {
            _mapper = GetComponent<HandWorldMapper>();
            if (_mapper == null)
            {
                _mapper = FindObjectOfType<HandWorldMapper>();
            }
        }

        if (_grabbableLayer < 0)
        {
            ResolveGrabbableLayer();
        }
    }

    private bool EvaluatePinch(bool previousState, TrackedHand hand)
    {
        if (!_provider.TryGetHand(hand, out var sample))
        {
            return false;
        }

        var distance = sample.PinchDistance;
        if (!previousState)
        {
            return distance <= _pinchStartDistance;
        }

        return distance < _pinchReleaseDistance;
    }

    private void TryStartGrab(TrackedHand hand, bool wasPinching, bool isPinching)
    {
        if (wasPinching || !isPinching)
        {
            return;
        }

        if (!_mapper.TryGetHandPose(hand, out var pose))
        {
            return;
        }

        if (!TryFindClosestGrabbable(pose.PinchCenter, out var targetTransform, out var targetRigidbody))
        {
            return;
        }

        _grabbedByHand = hand;
        _grabbedTransform = targetTransform;
        _grabbedRigidbody = targetRigidbody;
        _grabOffset = _grabbedTransform.position - pose.PinchCenter;

        if (_grabbedRigidbody != null)
        {
            _previousKinematic = _grabbedRigidbody.isKinematic;
            _previousUseGravity = _grabbedRigidbody.useGravity;

            _grabbedRigidbody.isKinematic = true;
            _grabbedRigidbody.useGravity = false;
        }
    }

    private void UpdateGrabbedObject()
    {
        if (!_mapper.TryGetHandPose(_grabbedByHand, out var pose) || !IsPinching(_grabbedByHand))
        {
            ReleaseObject();
            return;
        }

        var targetPosition = pose.PinchCenter + _grabOffset;
        var nextPosition = Smooth(_grabbedTransform.position, targetPosition);

        if (_grabbedRigidbody != null)
        {
            _grabbedRigidbody.MovePosition(nextPosition);
        }
        else
        {
            _grabbedTransform.position = nextPosition;
        }
    }

    private void ReleaseObject()
    {
        if (_grabbedRigidbody != null)
        {
            _grabbedRigidbody.isKinematic = _previousKinematic;
            _grabbedRigidbody.useGravity = _previousUseGravity;
        }

        _grabbedTransform = null;
        _grabbedRigidbody = null;
        _grabOffset = Vector3.zero;
    }

    private bool TryFindClosestGrabbable(Vector3 center, out Transform targetTransform, out Rigidbody targetRigidbody)
    {
        targetTransform = null;
        targetRigidbody = null;

        var hitCount = Physics.OverlapSphereNonAlloc(
            center,
            _grabSearchRadius,
            _overlapBuffer,
            ~0,
            QueryTriggerInteraction.Ignore);

        var minDistance = float.MaxValue;
        for (var i = 0; i < hitCount; i++)
        {
            var collider = _overlapBuffer[i];
            if (collider == null)
            {
                continue;
            }

            if (!IsGrabbable(collider, out var candidateTransform, out var candidateRigidbody))
            {
                continue;
            }

            var distance = (candidateTransform.position - center).sqrMagnitude;
            if (distance >= minDistance)
            {
                continue;
            }

            minDistance = distance;
            targetTransform = candidateTransform;
            targetRigidbody = candidateRigidbody;
        }

        return targetTransform != null;
    }

    private bool IsGrabbable(Collider collider, out Transform targetTransform, out Rigidbody targetRigidbody)
    {
        targetRigidbody = collider.attachedRigidbody;
        targetTransform = targetRigidbody != null ? targetRigidbody.transform : collider.transform;

        if (_requireRigidbody && targetRigidbody == null)
        {
            return false;
        }

        var targetObject = targetRigidbody != null ? targetRigidbody.gameObject : collider.gameObject;

        if (_grabbableLayer >= 0)
        {
            return targetObject.layer == _grabbableLayer;
        }

        if (_allowTagFallback && !string.IsNullOrWhiteSpace(_grabbableTag))
        {
            return targetObject.CompareTag(_grabbableTag);
        }

        // If no layer/tag filter is available, allow any object that passed _requireRigidbody.
        return true;
    }

    private void ResolveGrabbableLayer()
    {
        if (string.IsNullOrWhiteSpace(_grabbableLayerName))
        {
            _grabbableLayer = -1;
            return;
        }

        _grabbableLayer = LayerMask.NameToLayer(_grabbableLayerName);
        if (_grabbableLayer < 0 && !_warnedMissingLayer)
        {
            Debug.LogWarning($"[PinchController] Layer '{_grabbableLayerName}' was not found. " +
                             "Create it, enable tag fallback, or rely on Rigidbody-only fallback.", this);
            _warnedMissingLayer = true;
        }
    }

    private Vector3 Smooth(Vector3 current, Vector3 target)
    {
        if (_followSharpness <= 0f)
        {
            return target;
        }

        var t = 1f - Mathf.Exp(-_followSharpness * Time.deltaTime);
        return Vector3.Lerp(current, target, t);
    }
}
