using UnityEngine;

namespace Boulangerie3D.Traffic
{
    public enum TrafficControlKind
    {
        Stop,
        TrafficLight
    }

    public sealed class TrafficControlPoint : MonoBehaviour
    {
        [SerializeField] private TrafficControlKind kind;
        [SerializeField, Min(1f)] private float detectionDistance = 7f;
        [SerializeField, Min(1f)] private float laneTolerance = 2.75f;
        [SerializeField, Min(2f)] private float greenDuration = 8f;
        [SerializeField, Min(2f)] private float redDuration = 8f;
        [SerializeField] private float phaseOffset;

        public TrafficControlKind Kind => kind;
        public float DetectionDistance => detectionDistance;

        public bool Affects(Vector3 position, Vector3 travelDirection)
        {
            Vector3 controlForward = transform.forward;
            controlForward.y = 0f;
            travelDirection.y = 0f;
            if (travelDirection.sqrMagnitude < 0.01f)
                return false;
            travelDirection.Normalize();

            if (controlForward.sqrMagnitude > 0.01f &&
                Vector3.Dot(controlForward.normalized, travelDirection) < 0.55f)
                return false;

            Vector3 delta = transform.position - position;
            float ahead = Vector3.Dot(delta, travelDirection);
            float lateral = (delta - travelDirection * ahead).magnitude;
            return ahead >= -0.5f && ahead <= detectionDistance && lateral < laneTolerance;
        }

        public bool IsRed
        {
            get
            {
                if (kind != TrafficControlKind.TrafficLight)
                    return false;

                float cycle = greenDuration + redDuration;
                return Mathf.Repeat(Time.time + phaseOffset, cycle) >= greenDuration;
            }
        }
    }
}
