using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(HandWorldMapper))]
public class HandGizmoVisualizer : MonoBehaviour
{
    [SerializeField] private float _jointRadius = 0.09f;
    [SerializeField] private float _pinchRadius = 0.11f;
    [SerializeField] private bool _drawHeightGuide = true;
    [SerializeField] private float _guideGroundY = 0f;
    [SerializeField] private Color _leftColor = new Color(0.2f, 0.85f, 1f, 1f);
    [SerializeField] private Color _rightColor = new Color(1f, 0.45f, 0.25f, 1f);
    [SerializeField] private Color _pinchColor = Color.yellow;

    private HandWorldMapper _mapper;
    private PinchController _pinchController;

    private void Awake()
    {
        CacheReferences();
    }

    private void OnDrawGizmos()
    {
        CacheReferences();
        if (_mapper == null)
        {
            return;
        }

        DrawHand(TrackedHand.Left, _leftColor);
        DrawHand(TrackedHand.Right, _rightColor);
    }

    private void DrawHand(TrackedHand hand, Color baseColor)
    {
        if (!_mapper.TryGetHandPose(hand, out var pose))
        {
            return;
        }

        var pinching = _pinchController != null && _pinchController.IsPinching(hand);
        var drawColor = pinching ? _pinchColor : baseColor;

        Gizmos.color = drawColor;
        Gizmos.DrawSphere(pose.Wrist, _jointRadius);
        Gizmos.DrawSphere(pose.IndexTip, _jointRadius);
        Gizmos.DrawSphere(pose.ThumbTip, _jointRadius);
        Gizmos.DrawLine(pose.IndexTip, pose.ThumbTip);

        Gizmos.color = pinching ? _pinchColor : new Color(drawColor.r, drawColor.g, drawColor.b, 0.8f);
        Gizmos.DrawSphere(pose.PinchCenter, _pinchRadius);

        if (_drawHeightGuide)
        {
            var projection = new Vector3(pose.PinchCenter.x, _guideGroundY, pose.PinchCenter.z);
            Gizmos.DrawLine(pose.PinchCenter, projection);
            Gizmos.DrawWireSphere(projection, _pinchRadius * 0.8f);
        }
    }

    private void CacheReferences()
    {
        if (_mapper == null)
        {
            _mapper = GetComponent<HandWorldMapper>();
            if (_mapper == null)
            {
                _mapper = FindObjectOfType<HandWorldMapper>();
            }
        }

        if (_pinchController == null)
        {
            _pinchController = GetComponent<PinchController>();
            if (_pinchController == null)
            {
                _pinchController = FindObjectOfType<PinchController>();
            }
        }
    }
}
