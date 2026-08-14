using UnityEngine;

namespace Boulangerie3D.Traffic
{
    public enum TrafficControlKind
    {
        Stop,
        TrafficLight
    }

    public enum TrafficLightState
    {
        Green,
        Yellow,
        Red
    }

    public sealed class TrafficControlPoint : MonoBehaviour
    {
        [SerializeField] private TrafficControlKind kind;
        [SerializeField, Min(1f)] private float detectionDistance = 7f;
        [SerializeField, Min(1f)] private float laneTolerance = 2.75f;
        [SerializeField, Min(2f)] private float greenDuration = 8f;
        [SerializeField, Min(0.5f)] private float yellowDuration = 2f;
        [SerializeField, Min(2f)] private float redDuration = 8f;
        [SerializeField, Range(0.5f, 3f)] private float stopHoldDuration = 1f;
        [SerializeField] private float phaseOffset;

        public TrafficControlKind Kind => kind;
        public float DetectionDistance => detectionDistance;
        public float StopHoldDuration => stopHoldDuration;

        public TrafficLightState LightState
        {
            get
            {
                if (kind != TrafficControlKind.TrafficLight)
                    return TrafficLightState.Green;

                // Keep the old green+red total cycle unchanged so existing phaseOffset
                // values in the Unity scene remain synchronised. Yellow occupies the end
                // of the previously-authored green phase instead of lengthening the cycle.
                float effectiveYellow = Mathf.Min(yellowDuration, Mathf.Max(0.5f, greenDuration - 0.5f));
                float fullGreenEnd = Mathf.Max(0f, greenDuration - effectiveYellow);
                float cycle = greenDuration + redDuration;
                float phase = Mathf.Repeat(Time.time + phaseOffset, cycle);

                if (phase < fullGreenEnd)
                    return TrafficLightState.Green;
                if (phase < greenDuration)
                    return TrafficLightState.Yellow;
                return TrafficLightState.Red;
            }
        }

        // Kept for compatibility with existing scene/scripts.
        public bool IsRed => kind == TrafficControlKind.TrafficLight && LightState == TrafficLightState.Red;
        public bool IsYellow => kind == TrafficControlKind.TrafficLight && LightState == TrafficLightState.Yellow;
        public bool IsGreen => kind != TrafficControlKind.TrafficLight || LightState == TrafficLightState.Green;

        public float DistanceAhead(Vector3 position, Vector3 travelDirection)
        {
            travelDirection.y = 0f;
            if (travelDirection.sqrMagnitude < 0.01f)
                return float.MaxValue;

            travelDirection.Normalize();
            Vector3 delta = transform.position - position;
            delta.y = 0f;
            return Vector3.Dot(delta, travelDirection);
        }

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
            delta.y = 0f;
            float ahead = Vector3.Dot(delta, travelDirection);
            float lateral = (delta - travelDirection * ahead).magnitude;
            return ahead >= -0.5f && ahead <= detectionDistance && lateral < laneTolerance;
        }
    }
}
