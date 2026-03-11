using System;
using System.Collections.Generic;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.HandTracking;
using UnityEngine;

public enum TrackedHand
{
    Left,
    Right,
}

public struct HandTrackingSample
{
    public bool IsTracked;
    public TrackedHand Hand;
    public Vector3 Wrist;
    public Vector3 IndexTip;
    public Vector3 ThumbTip;
    public float PalmScale;
    public bool HasPalmScale;
    public float HandednessScore;

    public float PinchDistance => Vector3.Distance(IndexTip, ThumbTip);
}

[DisallowMultipleComponent]
public class HandTrackingProvider : MonoBehaviour
{
    private const int WristLandmarkIndex = 0;
    private const int ThumbTipLandmarkIndex = 4;
    private const int IndexMcpLandmarkIndex = 5;
    private const int IndexTipLandmarkIndex = 8;
    private const int MiddleMcpLandmarkIndex = 9;
    private const int PinkyMcpLandmarkIndex = 17;

    [SerializeField] private bool _preferRightWhenUnknown = true;
    [SerializeField] private float _subscribeRetryIntervalSeconds = 0.25f;

    private readonly object _syncRoot = new object();
    private readonly List<LandmarkSnapshot> _latestLandmarks = new List<LandmarkSnapshot>(2);
    private readonly List<HandednessSnapshot> _latestHandedness = new List<HandednessSnapshot>(2);

    private HandTrackingGraph _graph;
    private bool _isSubscribed;
    private float _nextSubscribeRetryTime;

    private HandTrackingSample _leftHand;
    private HandTrackingSample _rightHand;

    public bool TryGetHand(TrackedHand hand, out HandTrackingSample sample)
    {
        lock (_syncRoot)
        {
            sample = hand == TrackedHand.Left ? _leftHand : _rightHand;
            return sample.IsTracked;
        }
    }

    public bool TryGetPreferredTrackedHand(out TrackedHand hand, out HandTrackingSample sample)
    {
        lock (_syncRoot)
        {
            if (_rightHand.IsTracked)
            {
                hand = TrackedHand.Right;
                sample = _rightHand;
                return true;
            }

            if (_leftHand.IsTracked)
            {
                hand = TrackedHand.Left;
                sample = _leftHand;
                return true;
            }

            hand = TrackedHand.Right;
            sample = default;
            return false;
        }
    }

    private void OnEnable()
    {
        EnsureSubscribed();
    }

    private void Update()
    {
        EnsureSubscribed();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    private void EnsureSubscribed()
    {
        if (_isSubscribed && _graph != null)
        {
            return;
        }

        if (_isSubscribed && _graph == null)
        {
            _isSubscribed = false;
        }

        if (Time.unscaledTime < _nextSubscribeRetryTime)
        {
            return;
        }

        _graph = _graph != null ? _graph : FindObjectOfType<HandTrackingGraph>();
        if (_graph == null)
        {
            _nextSubscribeRetryTime = Time.unscaledTime + _subscribeRetryIntervalSeconds;
            return;
        }

        if (!TrySubscribeToGraph(_graph))
        {
            _nextSubscribeRetryTime = Time.unscaledTime + _subscribeRetryIntervalSeconds;
            return;
        }

        _nextSubscribeRetryTime = 0f;
    }

    private void Unsubscribe()
    {
        if (!_isSubscribed)
        {
            return;
        }

        if (_graph != null)
        {
            try
            {
                _graph.OnHandLandmarksOutput -= HandleHandLandmarksOutput;
                _graph.OnHandednessOutput -= HandleHandednessOutput;
            }
            catch (NullReferenceException)
            {
                // Graph output streams may already be disposed during shutdown.
            }
        }

        _isSubscribed = false;
        _graph = null;
    }

    private bool TrySubscribeToGraph(HandTrackingGraph graph)
    {
        var landmarksSubscribed = false;
        var handednessSubscribed = false;

        try
        {
            graph.OnHandLandmarksOutput += HandleHandLandmarksOutput;
            landmarksSubscribed = true;

            graph.OnHandednessOutput += HandleHandednessOutput;
            handednessSubscribed = true;

            _isSubscribed = true;
            return true;
        }
        catch (NullReferenceException)
        {
            if (handednessSubscribed)
            {
                graph.OnHandednessOutput -= HandleHandednessOutput;
            }

            if (landmarksSubscribed)
            {
                graph.OnHandLandmarksOutput -= HandleHandLandmarksOutput;
            }

            _isSubscribed = false;
            return false;
        }
    }

    private void HandleHandLandmarksOutput(object stream, OutputEventArgs<List<NormalizedLandmarkList>> eventArgs)
    {
        if (eventArgs == null || eventArgs.value == null)
        {
            return;
        }

        lock (_syncRoot)
        {
            _latestLandmarks.Clear();

            var handLandmarks = eventArgs.value;
            if (handLandmarks != null)
            {
                for (var i = 0; i < handLandmarks.Count; i++)
                {
                    if (!TryCreateSnapshot(handLandmarks[i], out var snapshot))
                    {
                        continue;
                    }

                    _latestLandmarks.Add(snapshot);
                }
            }

            RebuildSamplesLocked();
        }
    }

    private void HandleHandednessOutput(object stream, OutputEventArgs<List<ClassificationList>> eventArgs)
    {
        if (eventArgs == null || eventArgs.value == null)
        {
            return;
        }

        lock (_syncRoot)
        {
            _latestHandedness.Clear();

            var handednessList = eventArgs.value;
            if (handednessList != null)
            {
                for (var i = 0; i < handednessList.Count; i++)
                {
                    var classificationList = handednessList[i];
                    if (classificationList == null || classificationList.Classification.Count == 0)
                    {
                        _latestHandedness.Add(default);
                        continue;
                    }

                    var primary = classificationList.Classification[0];
                    _latestHandedness.Add(new HandednessSnapshot
                    {
                        Label = primary.Label,
                        Score = primary.HasScore ? primary.Score : 0f,
                    });
                }
            }

            RebuildSamplesLocked();
        }
    }

    private void RebuildSamplesLocked()
    {
        var left = EmptySample(TrackedHand.Left);
        var right = EmptySample(TrackedHand.Right);

        var unknown0 = default(HandTrackingSample);
        var unknown1 = default(HandTrackingSample);
        var unknownCount = 0;

        for (var i = 0; i < _latestLandmarks.Count; i++)
        {
            var snapshot = _latestLandmarks[i];
            var score = i < _latestHandedness.Count ? _latestHandedness[i].Score : 0f;
            var sample = CreateTrackedSample(snapshot, score);
            var slot = ResolveHandSlot(i);

            if (slot == HandSlot.Left)
            {
                sample.Hand = TrackedHand.Left;
                KeepHigherConfidence(ref left, sample);
            }
            else if (slot == HandSlot.Right)
            {
                sample.Hand = TrackedHand.Right;
                KeepHigherConfidence(ref right, sample);
            }
            else if (unknownCount == 0)
            {
                unknown0 = sample;
                unknownCount = 1;
            }
            else if (unknownCount == 1)
            {
                unknown1 = sample;
                unknownCount = 2;
            }
        }

        if (unknownCount > 0)
        {
            AssignUnknownHand(ref left, ref right, unknown0);
        }

        if (unknownCount > 1)
        {
            AssignUnknownHand(ref left, ref right, unknown1);
        }

        _leftHand = left;
        _rightHand = right;
    }

    private void AssignUnknownHand(ref HandTrackingSample left, ref HandTrackingSample right, HandTrackingSample unknown)
    {
        var hasLeft = left.IsTracked;
        var hasRight = right.IsTracked;

        if (!hasLeft && !hasRight)
        {
            var shouldBeRight = _preferRightWhenUnknown ? unknown.Wrist.x >= 0.5f : unknown.Wrist.x < 0.5f;
            unknown.Hand = shouldBeRight ? TrackedHand.Right : TrackedHand.Left;

            if (unknown.Hand == TrackedHand.Right)
            {
                right = unknown;
            }
            else
            {
                left = unknown;
            }

            return;
        }

        if (!hasRight)
        {
            unknown.Hand = TrackedHand.Right;
            right = unknown;
            return;
        }

        if (!hasLeft)
        {
            unknown.Hand = TrackedHand.Left;
            left = unknown;
            return;
        }

        var rightDistance = Mathf.Abs(unknown.Wrist.x - 0.75f);
        var leftDistance = Mathf.Abs(unknown.Wrist.x - 0.25f);
        unknown.Hand = rightDistance < leftDistance ? TrackedHand.Right : TrackedHand.Left;

        if (unknown.Hand == TrackedHand.Right)
        {
            KeepHigherConfidence(ref right, unknown);
        }
        else
        {
            KeepHigherConfidence(ref left, unknown);
        }
    }

    private static void KeepHigherConfidence(ref HandTrackingSample current, HandTrackingSample candidate)
    {
        if (!current.IsTracked || candidate.HandednessScore >= current.HandednessScore)
        {
            current = candidate;
        }
    }

    private HandSlot ResolveHandSlot(int index)
    {
        if (index >= _latestHandedness.Count)
        {
            return HandSlot.Unknown;
        }

        var label = _latestHandedness[index].Label;
        if (string.IsNullOrWhiteSpace(label))
        {
            return HandSlot.Unknown;
        }

        if (label.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return HandSlot.Left;
        }

        if (label.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return HandSlot.Right;
        }

        return HandSlot.Unknown;
    }

    private static bool TryCreateSnapshot(NormalizedLandmarkList landmarkList, out LandmarkSnapshot snapshot)
    {
        snapshot = default;
        if (landmarkList == null || landmarkList.Landmark == null || landmarkList.Landmark.Count <= PinkyMcpLandmarkIndex)
        {
            return false;
        }

        snapshot = new LandmarkSnapshot
        {
            Wrist = ToVector3(landmarkList.Landmark[WristLandmarkIndex]),
            ThumbTip = ToVector3(landmarkList.Landmark[ThumbTipLandmarkIndex]),
            IndexTip = ToVector3(landmarkList.Landmark[IndexTipLandmarkIndex]),
            PalmScale = EstimatePalmScale(landmarkList),
        };

        return true;
    }

    private static HandTrackingSample EmptySample(TrackedHand hand)
    {
        return new HandTrackingSample
        {
            IsTracked = false,
            Hand = hand,
            Wrist = Vector3.zero,
            IndexTip = Vector3.zero,
            ThumbTip = Vector3.zero,
            PalmScale = 0f,
            HasPalmScale = false,
            HandednessScore = 0f,
        };
    }

    private static HandTrackingSample CreateTrackedSample(LandmarkSnapshot snapshot, float handednessScore)
    {
        return new HandTrackingSample
        {
            IsTracked = true,
            Hand = TrackedHand.Right,
            Wrist = snapshot.Wrist,
            IndexTip = snapshot.IndexTip,
            ThumbTip = snapshot.ThumbTip,
            PalmScale = snapshot.PalmScale,
            HasPalmScale = snapshot.PalmScale > 0f,
            HandednessScore = handednessScore,
        };
    }

    private static Vector3 ToVector3(NormalizedLandmark landmark)
    {
        return new Vector3(landmark.X, landmark.Y, landmark.Z);
    }

    private static float EstimatePalmScale(NormalizedLandmarkList landmarkList)
    {
        var wrist = landmarkList.Landmark[WristLandmarkIndex];
        var indexMcp = landmarkList.Landmark[IndexMcpLandmarkIndex];
        var middleMcp = landmarkList.Landmark[MiddleMcpLandmarkIndex];
        var pinkyMcp = landmarkList.Landmark[PinkyMcpLandmarkIndex];

        var tri = (Distance2D(wrist, indexMcp) + Distance2D(wrist, pinkyMcp) + Distance2D(indexMcp, pinkyMcp)) / 3f;
        var spine = Distance2D(wrist, middleMcp);
        return tri * 0.7f + spine * 0.3f;
    }

    private static float Distance2D(NormalizedLandmark a, NormalizedLandmark b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private enum HandSlot
    {
        Unknown,
        Left,
        Right,
    }

    private struct LandmarkSnapshot
    {
        public Vector3 Wrist;
        public Vector3 ThumbTip;
        public Vector3 IndexTip;
        public float PalmScale;
    }

    private struct HandednessSnapshot
    {
        public string Label;
        public float Score;
    }
}
