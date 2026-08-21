using System;
using System.Collections.Generic;
using UnityEngine;

namespace Boulangerie3D.Traffic
{
    public enum TrafficTurnDirection
    {
        None,
        Straight,
        Left,
        Right,
        UTurn
    }

    [Serializable]
    public sealed class TrafficRoadExit
    {
        [SerializeField] private TrafficRoadSegment segment;
        [SerializeField, Min(0f)] private float weight = 1f;
        [SerializeField] private bool allowUTurn;

        public TrafficRoadSegment Segment => segment;
        public float Weight => Mathf.Max(0f, weight);
        public bool AllowUTurn => allowUTurn;
    }

    [DisallowMultipleComponent]
    public sealed class TrafficRoadNode : MonoBehaviour
    {
        [SerializeField] private TrafficRoadExit[] exits = Array.Empty<TrafficRoadExit>();
        [Header("Turn weights")]
        [SerializeField, Min(0f)] private float straightWeight = 1f;
        [SerializeField, Min(0f)] private float leftWeight = 0.8f;
        [SerializeField, Min(0f)] private float rightWeight = 0.9f;
        [SerializeField, Min(0f)] private float uTurnWeight = 0.1f;
        [SerializeField, Range(0f, 1f)] private float repeatedExitMultiplier = 0.3f;
        [SerializeField, Min(0.25f)] private float maxConnectionDistance = 2f;
        [SerializeField] private bool showGizmos = true;

        private readonly List<TrafficRoadExit> validExitBuffer = new List<TrafficRoadExit>(8);

        public int ExitCount => exits == null ? 0 : exits.Length;

        public int CollectValidExits(
            TrafficRoadSegment incoming,
            List<TrafficRoadSegment> results)
        {
            results.Clear();
            validExitBuffer.Clear();
            if (exits == null)
                return 0;

            for (int i = 0; i < exits.Length; i++)
            {
                TrafficRoadExit candidate = exits[i];
                TrafficRoadSegment segment = candidate != null ? candidate.Segment : null;
                if (segment == null || !segment.IsValid || segment.StartNode != this)
                    continue;

                TrafficTurnDirection turn = ClassifyTurn(incoming, segment);
                bool immediateReturn = incoming != null && segment.EndNode == incoming.StartNode;
                if ((turn == TrafficTurnDirection.UTurn || immediateReturn) && !candidate.AllowUTurn)
                    continue;

                if (incoming != null)
                {
                    Vector3 incomingEnd = incoming.GetPointAtStep(incoming.PointCount - 1);
                    Vector3 outgoingStart = segment.GetPointAtStep(0);
                    incomingEnd.y = outgoingStart.y;
                    if (Vector3.Distance(incomingEnd, outgoingStart) > maxConnectionDistance)
                        continue;
                }

                validExitBuffer.Add(candidate);
                results.Add(segment);
            }

            return results.Count;
        }

        public TrafficRoadSegment ChooseNextSegment(
            TrafficRoadSegment incoming,
            TrafficRoadSegment previousChoice,
            System.Random random,
            List<TrafficRoadSegment> available,
            out TrafficTurnDirection direction)
        {
            direction = TrafficTurnDirection.None;
            CollectValidExits(incoming, available);
            if (available.Count == 0)
                return null;

            float total = 0f;
            for (int i = 0; i < validExitBuffer.Count; i++)
                total += GetEffectiveWeight(validExitBuffer[i], incoming, previousChoice);

            if (total <= 0.0001f)
                return null;

            float sample = (float)(random != null ? random.NextDouble() : UnityEngine.Random.value) * total;
            TrafficRoadSegment selected = available[available.Count - 1];
            for (int i = 0; i < validExitBuffer.Count; i++)
            {
                sample -= GetEffectiveWeight(validExitBuffer[i], incoming, previousChoice);
                if (sample > 0f)
                    continue;
                selected = validExitBuffer[i].Segment;
                break;
            }

            direction = ClassifyTurn(incoming, selected);
            return selected;
        }

        public TrafficTurnDirection ClassifyTurn(
            TrafficRoadSegment incoming,
            TrafficRoadSegment outgoing)
        {
            if (incoming == null || outgoing == null)
                return TrafficTurnDirection.None;

            float angle = Vector3.SignedAngle(
                incoming.GetEndDirection(),
                outgoing.GetStartDirection(),
                Vector3.up);
            float absolute = Mathf.Abs(angle);
            if (absolute >= 135f)
                return TrafficTurnDirection.UTurn;
            if (absolute <= 35f)
                return TrafficTurnDirection.Straight;
            return angle > 0f ? TrafficTurnDirection.Right : TrafficTurnDirection.Left;
        }

        private float GetEffectiveWeight(
            TrafficRoadExit exit,
            TrafficRoadSegment incoming,
            TrafficRoadSegment previousChoice)
        {
            TrafficTurnDirection turn = ClassifyTurn(incoming, exit.Segment);
            float turnWeight = turn == TrafficTurnDirection.Left
                ? leftWeight
                : turn == TrafficTurnDirection.Right
                    ? rightWeight
                    : turn == TrafficTurnDirection.UTurn
                        ? uTurnWeight
                        : straightWeight;
            float repeatWeight = exit.Segment == previousChoice ? repeatedExitMultiplier : 1f;
            return exit.Weight * exit.Segment.SelectionWeight * turnWeight * repeatWeight;
        }

        private void OnDrawGizmosSelected()
        {
            if (!showGizmos || exits == null)
                return;

            Gizmos.color = new Color(1f, 0.65f, 0.05f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, 0.45f);
            for (int i = 0; i < exits.Length; i++)
            {
                TrafficRoadSegment segment = exits[i] != null ? exits[i].Segment : null;
                if (segment == null || segment.Route == null)
                    continue;
                Gizmos.DrawLine(
                    transform.position + Vector3.up * 0.2f,
                    segment.GetPointAtStep(0) + Vector3.up * 0.2f);
            }
        }
    }
}
