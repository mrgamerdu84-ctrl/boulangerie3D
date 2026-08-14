using UnityEngine;
using System.Collections.Generic;

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

    public enum TrafficLightAxis
    {
        Auto,
        X,
        Z
    }

    public sealed class TrafficControlPoint : MonoBehaviour
    {
        [SerializeField] private TrafficControlKind kind;
        [SerializeField, Min(1f)] private float detectionDistance = 7f;
        [SerializeField, Min(1f)] private float laneTolerance = 2.75f;

        [Header("Coordinated traffic lights")]
        [SerializeField] private TrafficLightAxis trafficAxis = TrafficLightAxis.Auto;
        [SerializeField, Min(6f)] private float greenDuration = 12f;
        [SerializeField, Min(1.5f)] private float yellowDuration = 3f;
        [SerializeField, Range(0.5f, 3f)] private float allRedDuration = 1f;
        [SerializeField, Range(0.5f, 3f)] private float stopHoldDuration = 1f;

        [Header("Visible traffic-light lamps")]
        [SerializeField] private Renderer[] redRenderers = new Renderer[0];
        [SerializeField] private Renderer[] yellowRenderers = new Renderer[0];
        [SerializeField] private Renderer[] greenRenderers = new Renderer[0];

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private MaterialPropertyBlock propertyBlock;
        private TrafficLightState lastVisualState;
        private bool visualStateInitialized;

        // Runtime information inferred from the nearest authored intersection. This makes
        // traffic controls independent from the sometimes arbitrary local rotation of a
        // decorative prefab and lets roadside signs control the correct approach lane.
        private bool hasRuntimeIntersection;
        private Vector3 runtimeIntersectionCenter;
        private TrafficLightAxis runtimeAxis = TrafficLightAxis.Auto;
        private int runtimeApproachSign;

        public TrafficControlKind Kind => kind;
        public float DetectionDistance => detectionDistance;
        public float StopHoldDuration => stopHoldDuration;

        private void Awake()
        {
            if (greenDuration <= 8.01f)
                greenDuration = 12f;
            if (yellowDuration <= 2.01f)
                yellowDuration = 3f;

            DiscoverLampRenderersIfNeeded();
            ApplyVisualState(true);
        }

        private void Update()
        {
            if (kind != TrafficControlKind.TrafficLight)
                return;

            TrafficLightState state = LightState;
            if (!visualStateInitialized || state != lastVisualState)
                ApplyVisualState(false);
        }

        public void Configure(TrafficControlKind controlKind, float detectDistance, float tolerance)
        {
            kind = controlKind;
            detectionDistance = Mathf.Max(1f, detectDistance);
            laneTolerance = Mathf.Max(1f, tolerance);

            if (kind == TrafficControlKind.TrafficLight)
            {
                greenDuration = Mathf.Max(12f, greenDuration);
                yellowDuration = Mathf.Max(3f, yellowDuration);
                allRedDuration = Mathf.Max(1f, allRedDuration);
            }

            DiscoverLampRenderersIfNeeded();
            ApplyVisualState(true);
        }

        public void BindToNearestIntersection(TrafficIntersectionReservation[] intersections)
        {
            hasRuntimeIntersection = false;
            runtimeAxis = TrafficLightAxis.Auto;
            runtimeApproachSign = 0;

            if (intersections == null || intersections.Length == 0)
                return;

            Vector3 position = transform.position;
            position.y = 0f;
            float nearestSqr = 35f * 35f;
            Vector3 nearestCenter = Vector3.zero;
            bool found = false;

            for (int i = 0; i < intersections.Length; i++)
            {
                TrafficIntersectionReservation intersection = intersections[i];
                if (intersection == null)
                    continue;

                Vector3 center = intersection.Bounds.center;
                center.y = 0f;
                float sqr = (center - position).sqrMagnitude;
                if (sqr >= nearestSqr)
                    continue;

                nearestSqr = sqr;
                nearestCenter = center;
                found = true;
            }

            if (!found)
                return;

            Vector3 fromCenter = position - nearestCenter;
            if (fromCenter.sqrMagnitude < 0.25f)
                return;

            hasRuntimeIntersection = true;
            runtimeIntersectionCenter = nearestCenter;

            if (Mathf.Abs(fromCenter.x) >= Mathf.Abs(fromCenter.z))
            {
                runtimeAxis = TrafficLightAxis.X;
                runtimeApproachSign = fromCenter.x >= 0f ? 1 : -1;
            }
            else
            {
                runtimeAxis = TrafficLightAxis.Z;
                runtimeApproachSign = fromCenter.z >= 0f ? 1 : -1;
            }

            // The runtime axis can change the current visible phase, so refresh the lamps.
            ApplyVisualState(true);
        }

        public TrafficLightState LightState
        {
            get
            {
                if (kind != TrafficControlKind.TrafficLight)
                    return TrafficLightState.Green;

                float green = Mathf.Max(6f, greenDuration);
                float yellow = Mathf.Max(1.5f, yellowDuration);
                float clearance = Mathf.Clamp(allRedDuration, 0.5f, 3f);
                float halfCycle = green + yellow + clearance;
                float cycle = halfCycle * 2f;
                float phase = Mathf.Repeat(Time.time, cycle);
                bool firstAxis = ResolveAxis() == TrafficLightAxis.X;

                if (firstAxis)
                {
                    if (phase < green)
                        return TrafficLightState.Green;
                    if (phase < green + yellow)
                        return TrafficLightState.Yellow;
                    return TrafficLightState.Red;
                }

                if (phase < halfCycle)
                    return TrafficLightState.Red;

                float secondPhase = phase - halfCycle;
                if (secondPhase < green)
                    return TrafficLightState.Green;
                if (secondPhase < green + yellow)
                    return TrafficLightState.Yellow;
                return TrafficLightState.Red;
            }
        }

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
            travelDirection.y = 0f;
            if (travelDirection.sqrMagnitude < 0.01f)
                return false;
            travelDirection.Normalize();

            if (hasRuntimeIntersection)
            {
                TrafficLightAxis travelAxis = Mathf.Abs(travelDirection.x) >= Mathf.Abs(travelDirection.z)
                    ? TrafficLightAxis.X
                    : TrafficLightAxis.Z;

                if (travelAxis != ResolveAxis())
                    return false;

                Vector3 outward = ResolveAxis() == TrafficLightAxis.X
                    ? new Vector3(runtimeApproachSign, 0f, 0f)
                    : new Vector3(0f, 0f, runtimeApproachSign);

                // Only the approach travelling toward this intersection is controlled.
                // This rejects the opposite-direction sign/light on the far side.
                if (Vector3.Dot(travelDirection, outward) > -0.25f)
                    return false;

                Vector3 fromIntersection = position - runtimeIntersectionCenter;
                fromIntersection.y = 0f;
                if (Vector3.Dot(fromIntersection, outward) < -1.5f)
                    return false;
            }
            else
            {
                // Fallback for isolated controls that are not close to a reservation zone.
                Vector3 controlForward = transform.forward;
                controlForward.y = 0f;
                if (controlForward.sqrMagnitude > 0.01f &&
                    Mathf.Abs(Vector3.Dot(controlForward.normalized, travelDirection)) < 0.45f)
                    return false;
            }

            Vector3 delta = transform.position - position;
            delta.y = 0f;
            float ahead = Vector3.Dot(delta, travelDirection);
            float lateral = (delta - travelDirection * ahead).magnitude;
            return ahead >= -0.75f && ahead <= detectionDistance && lateral <= laneTolerance;
        }

        private TrafficLightAxis ResolveAxis()
        {
            if (runtimeAxis != TrafficLightAxis.Auto)
                return runtimeAxis;
            if (trafficAxis != TrafficLightAxis.Auto)
                return trafficAxis;

            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                return TrafficLightAxis.Z;

            return Mathf.Abs(forward.x) >= Mathf.Abs(forward.z)
                ? TrafficLightAxis.X
                : TrafficLightAxis.Z;
        }

        private void DiscoverLampRenderersIfNeeded()
        {
            if (kind != TrafficControlKind.TrafficLight)
                return;

            bool needRed = redRenderers == null || redRenderers.Length == 0;
            bool needYellow = yellowRenderers == null || yellowRenderers.Length == 0;
            bool needGreen = greenRenderers == null || greenRenderers.Length == 0;
            if (!needRed && !needYellow && !needGreen)
                return;

            Renderer[] allRenderers = GetComponentsInChildren<Renderer>(true);
            var reds = new List<Renderer>();
            var yellows = new List<Renderer>();
            var greens = new List<Renderer>();

            for (int i = 0; i < allRenderers.Length; i++)
            {
                Renderer renderer = allRenderers[i];
                if (renderer == null)
                    continue;

                string key = renderer.name.ToLowerInvariant();
                Material[] materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    if (materials[m] != null)
                        key += " " + materials[m].name.ToLowerInvariant();
                }

                if (ContainsAny(key, "red", "rouge"))
                    reds.Add(renderer);
                else if (ContainsAny(key, "yellow", "amber", "orange", "jaune"))
                    yellows.Add(renderer);
                else if (ContainsAny(key, "green", "vert"))
                    greens.Add(renderer);
            }

            if (needRed && reds.Count > 0)
                redRenderers = reds.ToArray();
            if (needYellow && yellows.Count > 0)
                yellowRenderers = yellows.ToArray();
            if (needGreen && greens.Count > 0)
                greenRenderers = greens.ToArray();
        }

        private void ApplyVisualState(bool force)
        {
            if (kind != TrafficControlKind.TrafficLight)
                return;

            TrafficLightState state = LightState;
            if (!force && visualStateInitialized && state == lastVisualState)
                return;

            if (propertyBlock == null)
                propertyBlock = new MaterialPropertyBlock();

            SetLampGroup(redRenderers, state == TrafficLightState.Red, new Color(1f, 0.04f, 0.02f, 1f));
            SetLampGroup(yellowRenderers, state == TrafficLightState.Yellow, new Color(1f, 0.55f, 0.02f, 1f));
            SetLampGroup(greenRenderers, state == TrafficLightState.Green, new Color(0.04f, 1f, 0.12f, 1f));

            lastVisualState = state;
            visualStateInitialized = true;
        }

        private void SetLampGroup(Renderer[] renderers, bool active, Color activeColor)
        {
            if (renderers == null)
                return;

            Color visibleColor = active
                ? activeColor
                : new Color(0.025f, 0.025f, 0.025f, 1f);
            Color emissionColor = active ? activeColor * 2f : Color.black;
            emissionColor.a = 1f;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                propertyBlock.Clear();
                renderer.GetPropertyBlock(propertyBlock);
                propertyBlock.SetColor(BaseColorId, visibleColor);
                propertyBlock.SetColor(ColorId, visibleColor);
                propertyBlock.SetColor(EmissionColorId, emissionColor);
                renderer.SetPropertyBlock(propertyBlock);
            }
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            for (int i = 0; i < tokens.Length; i++)
                if (value.Contains(tokens[i]))
                    return true;
            return false;
        }
    }
}
