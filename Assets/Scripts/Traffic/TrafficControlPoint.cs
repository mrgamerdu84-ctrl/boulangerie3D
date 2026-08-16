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

        private bool hasRuntimeIntersection;
        private Vector3 runtimeIntersectionCenter;
        private TrafficLightAxis runtimeAxis = TrafficLightAxis.Auto;
        private int runtimeApproachSign;
        private float runtimeStopCoordinate;

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

        public void BindToNearestIntersection(
            TrafficIntersectionReservation[] intersections,
            CrosswalkPriorityZone[] crosswalks)
        {
            hasRuntimeIntersection = false;
            runtimeAxis = TrafficLightAxis.Auto;
            runtimeApproachSign = 0;
            runtimeStopCoordinate = 0f;

            if (intersections == null || intersections.Length == 0)
                return;

            Vector3 position = transform.position;
            position.y = 0f;
            float nearestSqr = 24f * 24f;
            Bounds nearestBounds = new Bounds();
            bool found = false;

            for (int i = 0; i < intersections.Length; i++)
            {
                TrafficIntersectionReservation intersection = intersections[i];
                if (intersection == null)
                    continue;

                Bounds bounds = intersection.Bounds;
                Vector3 center = bounds.center;
                center.y = 0f;
                float sqr = (center - position).sqrMagnitude;
                if (sqr >= nearestSqr)
                    continue;

                nearestSqr = sqr;
                nearestBounds = bounds;
                found = true;
            }

            if (!found)
                return;

            Vector3 nearestCenter = nearestBounds.center;
            nearestCenter.y = 0f;
            Vector3 fromCenter = position - nearestCenter;
            if (fromCenter.sqrMagnitude < 0.25f)
                return;

            hasRuntimeIntersection = true;
            runtimeIntersectionCenter = nearestCenter;

            // Prefer the nearest pedestrian crossing around this junction to determine
            // which approach this signal belongs to. This is much safer than using the
            // pole position/orientation, especially for manually placed corner signals.
            bool hasReferenceCrosswalk = false;
            Bounds referenceCrosswalkBounds = new Bounds();
            float referenceCrosswalkSqr = float.MaxValue;

            if (crosswalks != null)
            {
                float maxApproachX = nearestBounds.extents.x + 8f;
                float maxApproachZ = nearestBounds.extents.z + 8f;

                for (int i = 0; i < crosswalks.Length; i++)
                {
                    CrosswalkPriorityZone crosswalk = crosswalks[i];
                    if (crosswalk == null)
                        continue;

                    BoxCollider box = crosswalk.GetComponent<BoxCollider>();
                    if (box == null)
                        continue;

                    Bounds bounds = box.bounds;
                    Vector3 crossCenter = bounds.center;
                    crossCenter.y = 0f;
                    Vector3 relative = crossCenter - nearestCenter;

                    if (Mathf.Abs(relative.x) > maxApproachX ||
                        Mathf.Abs(relative.z) > maxApproachZ)
                        continue;

                    float sqr = (crossCenter - position).sqrMagnitude;
                    if (sqr >= referenceCrosswalkSqr)
                        continue;

                    referenceCrosswalkSqr = sqr;
                    referenceCrosswalkBounds = bounds;
                    hasReferenceCrosswalk = true;
                }
            }

            if (trafficAxis != TrafficLightAxis.Auto)
            {
                runtimeAxis = trafficAxis;
                runtimeApproachSign = runtimeAxis == TrafficLightAxis.X
                    ? (fromCenter.x >= 0f ? 1 : -1)
                    : (fromCenter.z >= 0f ? 1 : -1);
            }
            else if (hasReferenceCrosswalk)
            {
                Vector3 crossCenter = referenceCrosswalkBounds.center;
                crossCenter.y = 0f;
                Vector3 crossFromCenter = crossCenter - nearestCenter;

                runtimeAxis = Mathf.Abs(crossFromCenter.x) >= Mathf.Abs(crossFromCenter.z)
                    ? TrafficLightAxis.X
                    : TrafficLightAxis.Z;
                runtimeApproachSign = runtimeAxis == TrafficLightAxis.X
                    ? (crossFromCenter.x >= 0f ? 1 : -1)
                    : (crossFromCenter.z >= 0f ? 1 : -1);
            }
            else
            {
                // Last-resort fallback for intersections without a usable crosswalk zone.
                Vector3 signalForward = transform.forward;
                signalForward.y = 0f;
                runtimeAxis = Mathf.Abs(signalForward.x) >= Mathf.Abs(signalForward.z)
                    ? TrafficLightAxis.X
                    : TrafficLightAxis.Z;
                runtimeApproachSign = runtimeAxis == TrafficLightAxis.X
                    ? (fromCenter.x >= 0f ? 1 : -1)
                    : (fromCenter.z >= 0f ? 1 : -1);
            }

            float centerCoordinate = AxisCoordinate(nearestCenter, runtimeAxis);
            float intersectionExtent = AxisExtent(nearestBounds, runtimeAxis);

            runtimeStopCoordinate = centerCoordinate +
                runtimeApproachSign * (intersectionExtent + 1.25f);

            // If a reference crossing was found on this exact approach, stop outside its
            // outer edge so the vehicle never waits on top of the pedestrian crossing.
            if (hasReferenceCrosswalk)
            {
                Vector3 crossCenter = referenceCrosswalkBounds.center;
                crossCenter.y = 0f;
                float crossCoordinate = AxisCoordinate(crossCenter, runtimeAxis);
                float outwardDistance = runtimeApproachSign *
                    (crossCoordinate - centerCoordinate);
                float perpendicularDistance = Mathf.Abs(
                    PerpendicularCoordinate(crossCenter, runtimeAxis) -
                    PerpendicularCoordinate(nearestCenter, runtimeAxis));
                float perpendicularExtent = runtimeAxis == TrafficLightAxis.X
                    ? nearestBounds.extents.z
                    : nearestBounds.extents.x;

                if (outwardDistance >= -0.5f &&
                    outwardDistance <= intersectionExtent + 8f &&
                    perpendicularDistance <= perpendicularExtent + 5f)
                {
                    float crossExtent = AxisExtent(referenceCrosswalkBounds, runtimeAxis);
                    runtimeStopCoordinate = crossCoordinate +
                        runtimeApproachSign * (crossExtent + 0.9f);
                }
            }

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

            if (hasRuntimeIntersection)
            {
                float positionCoordinate = AxisCoordinate(position, runtimeAxis);
                return runtimeApproachSign * (positionCoordinate - runtimeStopCoordinate);
            }

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

                if (!MatchesRuntimeApproachDirection(travelDirection))
                    return false;

                float positionCoordinate = AxisCoordinate(position, runtimeAxis);
                float centerCoordinate = AxisCoordinate(runtimeIntersectionCenter, runtimeAxis);
                float approachSide = runtimeApproachSign *
                    (positionCoordinate - centerCoordinate);
                if (approachSide < -1.5f)
                    return false;

                float distanceToLine = DistanceAhead(position, travelDirection);
                if (distanceToLine < -0.9f || distanceToLine > detectionDistance)
                    return false;

                float lateral = Mathf.Abs(
                    PerpendicularCoordinate(position, runtimeAxis) -
                    PerpendicularCoordinate(runtimeIntersectionCenter, runtimeAxis));
                return lateral <= laneTolerance;
            }

            Vector3 controlForward = transform.forward;
            controlForward.y = 0f;
            if (controlForward.sqrMagnitude > 0.01f &&
                Mathf.Abs(Vector3.Dot(controlForward.normalized, travelDirection)) < 0.45f)
                return false;

            Vector3 delta = transform.position - position;
            delta.y = 0f;
            float ahead = Vector3.Dot(delta, travelDirection);
            float lateralFallback = (delta - travelDirection * ahead).magnitude;
            return ahead >= -0.75f && ahead <= detectionDistance && lateralFallback <= laneTolerance;
        }

        private bool MatchesRuntimeApproachDirection(Vector3 travelDirection)
        {
            float directionComponent = runtimeAxis == TrafficLightAxis.X
                ? travelDirection.x
                : travelDirection.z;
            return directionComponent * runtimeApproachSign <= -0.4f;
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

        private static float AxisCoordinate(Vector3 value, TrafficLightAxis axis)
        {
            return axis == TrafficLightAxis.X ? value.x : value.z;
        }

        private static float PerpendicularCoordinate(Vector3 value, TrafficLightAxis axis)
        {
            return axis == TrafficLightAxis.X ? value.z : value.x;
        }

        private static float AxisExtent(Bounds bounds, TrafficLightAxis axis)
        {
            return axis == TrafficLightAxis.X ? bounds.extents.x : bounds.extents.z;
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
