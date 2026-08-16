using UnityEngine;

namespace Boulangerie3D.Traffic
{
    [DisallowMultipleComponent]
    public sealed class TrafficRoutePath : MonoBehaviour
    {
        private const float MinimumWaypointDistance = 0.35f;
        private const float ReverseSegmentDotThreshold = -0.35f;

        [SerializeField] private bool pedestrianRoute;
        [SerializeField] private Transform[] waypoints = new Transform[0];

        [System.NonSerialized] private string lastValidationError;

        public bool IsPedestrianRoute => pedestrianRoute;
        public int Count => waypoints == null ? 0 : waypoints.Length;
        public bool HasCoherentGeometry => TryValidateGeometry(out _, out _);
        public bool IsValid
        {
            get
            {
                // A route can be valid whether it is used by vehicles or pedestrians.
                // The previous implementation rejected every vehicle route because
                // pedestrianRoute had to be true, which made route validation misleading.
                if (waypoints == null || waypoints.Length < 2) return false;
                for (int i = 0; i < waypoints.Length; i++)
                    if (waypoints[i] == null) return false;
                return true;
            }
        }

        private void Awake()
        {
            ValidateAndLog();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            ValidateAndLog();
        }
#endif

        public bool TryValidateGeometry(out int faultyWaypointIndex, out string reason)
        {
            faultyWaypointIndex = -1;
            reason = string.Empty;

            if (waypoints == null || waypoints.Length < 2)
            {
                reason = "la route doit contenir au moins deux waypoints";
                return false;
            }

            for (int i = 0; i < waypoints.Length; i++)
            {
                if (waypoints[i] == null)
                {
                    faultyWaypointIndex = i;
                    reason = "la référence du waypoint est manquante";
                    return false;
                }

                Vector3 segment = GetPoint(i + 1) - GetPoint(i);
                segment.y = 0f;
                if (segment.magnitude < MinimumWaypointDistance)
                {
                    faultyWaypointIndex = (i + 1) % Count;
                    reason = $"waypoints superposés ou trop proches ({segment.magnitude:F2} m, minimum {MinimumWaypointDistance:F2} m)";
                    return false;
                }
            }

            for (int i = 0; i < waypoints.Length; i++)
            {
                Vector3 incoming = GetPoint(i) - GetPoint(i - 1);
                Vector3 outgoing = GetPoint(i + 1) - GetPoint(i);
                incoming.y = 0f;
                outgoing.y = 0f;

                float alignment = Vector3.Dot(incoming.normalized, outgoing.normalized);
                if (alignment <= ReverseSegmentDotThreshold)
                {
                    faultyWaypointIndex = i;
                    reason = $"le segment repart en arrière (alignement {alignment:F2})";
                    return false;
                }
            }

            return true;
        }

        public bool IsNextDirectionCoherent(
            Vector3 vehiclePosition,
            Vector3 vehicleForward,
            int waypointIndex,
            out float alignment)
        {
            alignment = 1f;
            if (Count == 0 || GetWaypoint(waypointIndex) == null)
                return false;

            Vector3 toWaypoint = GetPoint(waypointIndex) - vehiclePosition;
            toWaypoint.y = 0f;
            vehicleForward.y = 0f;
            if (toWaypoint.sqrMagnitude < 0.01f || vehicleForward.sqrMagnitude < 0.01f)
                return true;

            alignment = Vector3.Dot(vehicleForward.normalized, toWaypoint.normalized);
            return alignment >= ReverseSegmentDotThreshold;
        }

        public string DescribeWaypoint(int index)
        {
            Transform waypoint = GetWaypoint(index);
            return waypoint == null
                ? $"waypoint[{index}] <null>"
                : $"waypoint[{((index % Count) + Count) % Count}] '{waypoint.name}' à {waypoint.position}";
        }

        private void ValidateAndLog()
        {
            if (TryValidateGeometry(out int faultyIndex, out string reason))
            {
                lastValidationError = string.Empty;
                return;
            }

            string message = $"[TrafficRouteValidation] Route '{name}' incohérente : " +
                $"{DescribeWaypoint(faultyIndex)} — {reason}.";
            if (message == lastValidationError)
                return;

            lastValidationError = message;
            Debug.LogError(message, this);
        }

        public Vector3 GetPoint(int index)
        {
            if (Count == 0)
                return transform.position;

            int wrapped = ((index % Count) + Count) % Count;
            return waypoints[wrapped].position;
        }

        public Transform GetWaypoint(int index)
        {
            if (Count == 0)
                return null;

            int wrapped = ((index % Count) + Count) % Count;
            return waypoints[wrapped];
        }

        public int IndexOfWaypoint(Transform waypoint)
        {
            if (waypoint == null || waypoints == null)
                return -1;

            for (int i = 0; i < waypoints.Length; i++)
                if (waypoints[i] == waypoint)
                    return i;

            return -1;
        }

        public Vector3 GetDirection(int index)
        {
            if (Count < 2)
                return transform.forward;

            Vector3 direction = GetPoint(index + 1) - GetPoint(index);
            direction.y = 0f;
            return direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;
        }

        public float DistanceToPath(Vector3 position)
        {
            if (Count == 0)
                return Vector3.Distance(position, transform.position);

            float nearest = float.MaxValue;
            for (int i = 0; i < Count; i++)
            {
                Vector3 a = GetPoint(i);
                Vector3 b = GetPoint(i + 1);
                a.y = position.y;
                b.y = position.y;
                nearest = Mathf.Min(nearest, Vector3.Distance(position, ClosestPointOnSegment(position, a, b)));
            }
            return nearest;
        }

        public bool TryGetNearestSegment(Vector3 position, out Vector3 point, out Vector3 direction, out float distance)
        {
            point = transform.position;
            direction = transform.forward;
            distance = float.MaxValue;
            if (Count < 2)
                return false;

            for (int i = 0; i < Count; i++)
            {
                Vector3 a = GetPoint(i);
                Vector3 b = GetPoint(i + 1);
                Vector3 candidate = ClosestPointOnSegment(position, a, b);
                float candidateDistance = Vector3.Distance(position, candidate);
                if (candidateDistance >= distance)
                    continue;

                point = candidate;
                Vector3 segment = b - a;
                segment.y = 0f;
                direction = segment.sqrMagnitude > 0.0001f ? segment.normalized : transform.forward;
                distance = candidateDistance;
            }
            return distance < float.MaxValue;
        }

        private static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 segment = b - a;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared < 0.0001f)
                return a;
            float t = Mathf.Clamp01(Vector3.Dot(point - a, segment) / lengthSquared);
            return a + segment * t;
        }
    }
}
