using UnityEngine;

namespace Boulangerie3D.Traffic
{
    [DisallowMultipleComponent]
    public sealed class TrafficRoutePath : MonoBehaviour
    {
        [SerializeField] private bool pedestrianRoute;
        [SerializeField] private Transform[] waypoints = new Transform[0];

        public bool IsPedestrianRoute => pedestrianRoute;
        public int Count => waypoints == null ? 0 : waypoints.Length;
        public bool IsValid
        {
            get
            {
                if (!pedestrianRoute || waypoints == null || waypoints.Length < 2) return false;
                for (int i = 0; i < waypoints.Length; i++) if (waypoints[i] == null) return false;
                return true;
            }
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
