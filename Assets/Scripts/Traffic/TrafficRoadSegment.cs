using UnityEngine;

namespace Boulangerie3D.Traffic
{
    [DisallowMultipleComponent]
    public sealed class TrafficRoadSegment : MonoBehaviour
    {
        [SerializeField] private TrafficRoutePath route;
        [SerializeField, Min(0)] private int startWaypointIndex;
        [SerializeField, Min(0)] private int endWaypointIndex = 1;
        [SerializeField] private TrafficRoadNode startNode;
        [SerializeField] private TrafficRoadNode endNode;
        [SerializeField, Min(0.01f)] private float selectionWeight = 1f;
        [SerializeField] private bool showGizmos = true;
        [SerializeField] private Color segmentColor = new Color(0.1f, 0.8f, 1f, 0.8f);

        public TrafficRoutePath Route => route;
        public TrafficRoadNode StartNode => startNode;
        public TrafficRoadNode EndNode => endNode;
        public float SelectionWeight => Mathf.Max(0.01f, selectionWeight);
        public int StartWaypointIndex => NormalizeIndex(startWaypointIndex);
        public int EndWaypointIndex => NormalizeIndex(endWaypointIndex);
        public bool IsValid => route != null && route.IsValid && route.Count >= 2 &&
            startNode != null && endNode != null && StartWaypointIndex != EndWaypointIndex;

        public int PointCount
        {
            get
            {
                if (route == null || route.Count == 0)
                    return 0;
                int distance = EndWaypointIndex - StartWaypointIndex;
                if (distance < 0)
                    distance += route.Count;
                return distance + 1;
            }
        }

        public int GetWaypointIndexAtStep(int step)
        {
            if (route == null || route.Count == 0)
                return 0;
            return NormalizeIndex(StartWaypointIndex + Mathf.Clamp(step, 0, Mathf.Max(0, PointCount - 1)));
        }

        public Vector3 GetPointAtStep(int step)
        {
            return route != null ? route.GetPoint(GetWaypointIndexAtStep(step)) : transform.position;
        }

        public Vector3 GetStartDirection()
        {
            if (PointCount < 2)
                return transform.forward;
            return PlanarDirection(GetPointAtStep(1) - GetPointAtStep(0), transform.forward);
        }

        public Vector3 GetEndDirection()
        {
            if (PointCount < 2)
                return transform.forward;
            int last = PointCount - 1;
            return PlanarDirection(GetPointAtStep(last) - GetPointAtStep(last - 1), transform.forward);
        }

        private int NormalizeIndex(int index)
        {
            if (route == null || route.Count == 0)
                return 0;
            return ((index % route.Count) + route.Count) % route.Count;
        }

        private static Vector3 PlanarDirection(Vector3 value, Vector3 fallback)
        {
            value.y = 0f;
            if (value.sqrMagnitude > 0.0001f)
                return value.normalized;
            fallback.y = 0f;
            return fallback.sqrMagnitude > 0.0001f ? fallback.normalized : Vector3.forward;
        }

        private void OnDrawGizmosSelected()
        {
            if (!showGizmos || route == null || PointCount < 2)
                return;

            Gizmos.color = segmentColor;
            for (int i = 0; i < PointCount - 1; i++)
            {
                Vector3 a = GetPointAtStep(i) + Vector3.up * 0.15f;
                Vector3 b = GetPointAtStep(i + 1) + Vector3.up * 0.15f;
                Gizmos.DrawLine(a, b);
                Vector3 direction = (b - a).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, direction) * 0.25f;
                Vector3 arrow = Vector3.Lerp(a, b, 0.7f);
                Gizmos.DrawLine(arrow, arrow - direction * 0.45f + side);
                Gizmos.DrawLine(arrow, arrow - direction * 0.45f - side);
            }
        }
    }
}
