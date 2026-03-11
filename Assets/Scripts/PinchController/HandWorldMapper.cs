using UnityEngine;
using UnityEngine.Serialization;
using Mediapipe.Unity;

public struct HandWorldPose
{
    public bool IsTracked;
    public TrackedHand Hand;
    public Vector3 Wrist;
    public Vector3 IndexTip;
    public Vector3 ThumbTip;
    public Vector3 PinchCenter;
    public float DepthFar01;
}

[DisallowMultipleComponent]
[RequireComponent(typeof(HandTrackingProvider))]
public class HandWorldMapper : MonoBehaviour
{
    [Header("World Mapping")]
    [SerializeField] private float _worldMinX = -3.5f;
    [SerializeField] private float _worldMaxX = 3.5f;
    [SerializeField] private bool _useVerticalMapping = true;
    [SerializeField] private float _worldMinY = 0.4f;
    [SerializeField] private float _worldMaxY = 6f;
    // Camera-near hand => back side (+Z), capped by this max.
    [FormerlySerializedAs("_nearHandWorldZ")]
    [SerializeField] private float _nearHandBackZMax = 8f;
    // Camera-far hand => front side (-Z), capped by this min.
    [FormerlySerializedAs("_farHandWorldZ")]
    [SerializeField] private float _farHandFrontZMin = -8f;

    [Header("Fallback Y")]
    [SerializeField] private float _planeY = 0.8f;

    [Header("Depth Input")]
    [SerializeField] private float _depthNearValue = -0.2f;
    [SerializeField] private float _depthFarValue = 0.2f;
    [SerializeField] private bool _invertDepthInput = false;
    [SerializeField] private bool _usePalmScaleDepth = true;
    [SerializeField] private int _palmScaleCalibrationFrames = 12;
    [SerializeField] private float _palmScaleMinimumSpan = 0.02f;
    [SerializeField] private float _palmScaleEdgeMargin = 0.004f;
    [SerializeField] private float _palmScaleRangeMinimumSpan = 0.07f;
    [SerializeField] private float _depthResponse = 0.16f;
    [SerializeField] private float _depthDeadZone = 0.04f;

    [Header("Input Correction")]
    [SerializeField] private bool _autoMirrorXFromImageSource = true;
    [SerializeField] private bool _mirrorX = false;
    [SerializeField] private bool _invertY = true;
    [SerializeField] private float _positionSharpness = 18f;

    private HandTrackingProvider _provider;

    private HandWorldPose _leftPose;
    private HandWorldPose _rightPose;

    private bool _leftInitialized;
    private bool _rightInitialized;
    private DepthCalibrationState _leftDepthState;
    private DepthCalibrationState _rightDepthState;

    public bool TryGetHandPose(TrackedHand hand, out HandWorldPose pose)
    {
        pose = hand == TrackedHand.Left ? _leftPose : _rightPose;
        return pose.IsTracked;
    }

    public bool TryGetPreferredTrackedHand(out TrackedHand hand, out HandWorldPose pose)
    {
        if (_rightPose.IsTracked)
        {
            hand = TrackedHand.Right;
            pose = _rightPose;
            return true;
        }

        if (_leftPose.IsTracked)
        {
            hand = TrackedHand.Left;
            pose = _leftPose;
            return true;
        }

        hand = TrackedHand.Right;
        pose = default;
        return false;
    }

    private void Awake()
    {
        NormalizeDepthRange();
        _provider = GetComponent<HandTrackingProvider>();
    }

    private void OnValidate()
    {
        NormalizeDepthRange();
    }

    private void NormalizeDepthRange()
    {
        if (_nearHandBackZMax < 0f)
        {
            _nearHandBackZMax = Mathf.Abs(_nearHandBackZMax);
        }

        if (_farHandFrontZMin > 0f)
        {
            _farHandFrontZMin = -_farHandFrontZMin;
        }
    }

    private void Update()
    {
        EnsureProvider();
        if (_provider == null)
        {
            return;
        }

        UpdateHandPose(TrackedHand.Left, ref _leftPose, ref _leftInitialized);
        UpdateHandPose(TrackedHand.Right, ref _rightPose, ref _rightInitialized);
    }

    private void EnsureProvider()
    {
        if (_provider != null)
        {
            return;
        }

        _provider = GetComponent<HandTrackingProvider>();
        if (_provider == null)
        {
            _provider = FindObjectOfType<HandTrackingProvider>();
        }
    }

    private void UpdateHandPose(TrackedHand hand, ref HandWorldPose current, ref bool initialized)
    {
        if (!_provider.TryGetHand(hand, out var sample))
        {
            current = CreateEmptyPose(hand);
            initialized = false;
            return;
        }

        var depthState = hand == TrackedHand.Left ? _leftDepthState : _rightDepthState;
        var target = MapToWorld(hand, sample, ref depthState);
        if (hand == TrackedHand.Left)
        {
            _leftDepthState = depthState;
        }
        else
        {
            _rightDepthState = depthState;
        }

        if (!initialized)
        {
            current = target;
            initialized = true;
            return;
        }

        current.IsTracked = true;
        current.Hand = hand;
        current.DepthFar01 = target.DepthFar01;
        current.Wrist = Smooth(current.Wrist, target.Wrist);
        current.IndexTip = Smooth(current.IndexTip, target.IndexTip);
        current.ThumbTip = Smooth(current.ThumbTip, target.ThumbTip);
        current.PinchCenter = (current.IndexTip + current.ThumbTip) * 0.5f;
    }

    private HandWorldPose MapToWorld(TrackedHand hand, HandTrackingSample sample, ref DepthCalibrationState depthState)
    {
        var depthFar01 = ResolveDepthFar01(sample, ref depthState);

        var wrist = MapPoint(sample.Wrist.x, sample.Wrist.y, depthFar01);
        var indexTip = MapPoint(sample.IndexTip.x, sample.IndexTip.y, depthFar01);
        var thumbTip = MapPoint(sample.ThumbTip.x, sample.ThumbTip.y, depthFar01);

        return new HandWorldPose
        {
            IsTracked = true,
            Hand = hand,
            Wrist = wrist,
            IndexTip = indexTip,
            ThumbTip = thumbTip,
            PinchCenter = (indexTip + thumbTip) * 0.5f,
            DepthFar01 = depthFar01,
        };
    }

    private Vector3 MapPoint(float normalizedX, float normalizedY, float depthFar01)
    {
        var mirrorX = ShouldMirrorX();
        var x01 = Mathf.Clamp01(mirrorX ? 1f - normalizedX : normalizedX);
        var worldX = Mathf.Lerp(_worldMinX, _worldMaxX, x01);

        var worldY = _planeY;
        if (_useVerticalMapping)
        {
            var y01 = Mathf.Clamp01(_invertY ? 1f - normalizedY : normalizedY);
            worldY = Mathf.Lerp(_worldMinY, _worldMaxY, y01);
        }

        // depthFar01: 0 = near hand (camera side), 1 = far hand.
        // near -> +Z(back), far -> -Z(front).
        var worldZ = Mathf.Lerp(_nearHandBackZMax, _farHandFrontZMin, depthFar01);
        return new Vector3(worldX, worldY, worldZ);
    }

    private float ResolveDepthFar01(HandTrackingSample sample, ref DepthCalibrationState state)
    {
        if (_usePalmScaleDepth && sample.HasPalmScale)
        {
            return ResolveDepthFromPalmScale(sample.PalmScale, ref state);
        }

        var depthInput = _invertDepthInput ? -sample.Wrist.z : sample.Wrist.z;
        return Mathf.Clamp01(Mathf.InverseLerp(_depthNearValue, _depthFarValue, depthInput));
    }

    private float ResolveDepthFromPalmScale(float palmScale, ref DepthCalibrationState state)
    {
        if (!state.RangeInitialized)
        {
            state.RangeInitialized = true;
            state.CalibrationCount = 0;
            state.MinScale = palmScale;
            state.MaxScale = palmScale;
            state.SmoothedDepthFar01 = 0.5f;
            state.HasSmoothedDepth = true;
        }

        if (state.CalibrationCount < _palmScaleCalibrationFrames)
        {
            state.MinScale = Mathf.Min(state.MinScale, palmScale);
            state.MaxScale = Mathf.Max(state.MaxScale, palmScale);
            state.CalibrationCount++;

            if (state.CalibrationCount >= _palmScaleCalibrationFrames)
            {
                var initialSpan = Mathf.Max(state.MaxScale - state.MinScale, _palmScaleMinimumSpan);
                state.MinScale -= initialSpan * 0.45f;
                state.MaxScale += initialSpan * 0.45f;
            }
        }
        else
        {
            if (palmScale < state.MinScale + _palmScaleEdgeMargin)
            {
                state.MinScale = Mathf.Lerp(state.MinScale, palmScale - _palmScaleEdgeMargin, 0.22f);
            }

            if (palmScale > state.MaxScale - _palmScaleEdgeMargin)
            {
                state.MaxScale = Mathf.Lerp(state.MaxScale, palmScale + _palmScaleEdgeMargin, 0.22f);
            }

            if (state.MaxScale - state.MinScale < _palmScaleRangeMinimumSpan)
            {
                state.MaxScale = state.MinScale + _palmScaleRangeMinimumSpan;
            }
        }

        var near01 = Mathf.InverseLerp(state.MinScale, state.MaxScale, palmScale);
        var rawFar01 = 1f - near01;

        if (state.HasSmoothedDepth)
        {
            if (Mathf.Abs(rawFar01 - state.SmoothedDepthFar01) < _depthDeadZone)
            {
                rawFar01 = state.SmoothedDepthFar01;
            }

            state.SmoothedDepthFar01 = Mathf.Lerp(state.SmoothedDepthFar01, rawFar01, _depthResponse);
        }
        else
        {
            state.SmoothedDepthFar01 = rawFar01;
            state.HasSmoothedDepth = true;
        }

        return Mathf.Clamp01(state.SmoothedDepthFar01);
    }

    private bool ShouldMirrorX()
    {
        if (!_autoMirrorXFromImageSource)
        {
            return _mirrorX;
        }

        var imageSource = ImageSourceProvider.ImageSource;
        if (imageSource == null)
        {
            return true;
        }

        return imageSource.isHorizontallyFlipped ^ imageSource.isFrontFacing;
    }

    private Vector3 Smooth(Vector3 current, Vector3 target)
    {
        if (_positionSharpness <= 0f)
        {
            return target;
        }

        var t = 1f - Mathf.Exp(-_positionSharpness * Time.deltaTime);
        return Vector3.Lerp(current, target, t);
    }

    private static HandWorldPose CreateEmptyPose(TrackedHand hand)
    {
        return new HandWorldPose
        {
            IsTracked = false,
            Hand = hand,
            Wrist = Vector3.zero,
            IndexTip = Vector3.zero,
            ThumbTip = Vector3.zero,
            PinchCenter = Vector3.zero,
            DepthFar01 = 0f,
        };
    }

    private struct DepthCalibrationState
    {
        public bool RangeInitialized;
        public int CalibrationCount;
        public float MinScale;
        public float MaxScale;
        public bool HasSmoothedDepth;
        public float SmoothedDepthFar01;
    }
}
