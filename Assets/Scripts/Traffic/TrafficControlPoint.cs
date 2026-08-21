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

    public class TrafficControlPoint : MonoBehaviour
    {
        [SerializeField] private TrafficControlKind kind;
        [SerializeField, Min(1f)] private float detectionDistance = 7f;
        [SerializeField, Min(1f)] private float laneTolerance = 2.75f;

        [Header("Coordinated traffic lights")]
        [SerializeField] private TrafficLightAxis trafficAxis = TrafficLightAxis.Auto;
        [SerializeField, Min(6f)] private float greenDuration = 12f;
        [SerializeField, Min(1.5f)] private float yellowDuration = 3f;
        [SerializeField, Min(8f)] private float redDuration = 16f;
        [SerializeField, Range(0.5f, 3f)] private float allRedDuration = 1f;
        [SerializeField, Range(0.5f, 3f)] private float stopHoldDuration = 1f;
        [SerializeField, Min(1.25f)] private float fallbackStopOffset = 4f;

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
        private string runtimeIntersectionName = string.Empty;
        private bool hasRuntimeCrosswalk;
        private Bounds runtimeCrosswalkBounds;
        private Vector3 runtimeCrosswalkOuterEdge;

        public TrafficControlKind Kind => kind;
        public float DetectionDistance => detectionDistance;
        public float StopHoldDuration => stopHoldDuration;
        public float LaneTolerance => laneTolerance;
        public bool HasRuntimeIntersection => hasRuntimeIntersection;
        public TrafficLightAxis DetectedAxis => runtimeAxis;
        public string DetectedDirection => runtimeApproachSign > 0
            ? (runtimeAxis == TrafficLightAxis.X ? "X vers -X" : "Z vers -Z")
            : runtimeApproachSign < 0
                ? (runtimeAxis == TrafficLightAxis.X ? "-X vers +X" : "-Z vers +Z")
                : "Non detecte";
        public string AssociatedIntersection => runtimeIntersectionName;
        public bool UsesCrosswalkStopLine => hasRuntimeCrosswalk;
        public Bounds DetectedCrosswalkBounds => runtimeCrosswalkBounds;
        public Vector3 CrosswalkOuterEdgePosition => runtimeCrosswalkOuterEdge;
        public Vector3 LogicalStopLinePosition
        {
            get
            {
                Vector3 value = runtimeIntersectionCenter;
                if (runtimeAxis == TrafficLightAxis.X)
                    value.x = runtimeStopCoordinate;
                else if (runtimeAxis == TrafficLightAxis.Z)
                    value.z = runtimeStopCoordinate;
                return value;
            }
        }

        protected virtual void Awake()
        {
            if (fallbackStopOffset <= 1.25f)
                fallbackStopOffset = 4f;

            if (greenDuration <= 8.01f)
                greenDuration = 12f;
            if (yellowDuration <= 2.01f)
                yellowDuration = 3f;
            redDuration = Mathf.Max(redDuration, greenDuration + yellowDuration + allRedDuration);

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
            if (intersections == null || intersections.Length == 0)
            {
                BindToNearestIntersection(new Bounds[0], crosswalks);
                return;
            }

            var bounds = new List<Bounds>(intersections.Length);
            for (int i = 0; i < intersections.Length; i++)
                if (intersections[i] != null)
                    bounds.Add(intersections[i].Bounds);

            BindToNearestIntersection(bounds.ToArray(), crosswalks);
        }

        public void BindToNearestIntersection(
            Bounds[] intersectionBounds,
            CrosswalkPriorityZone[] crosswalks)
        {
            hasRuntimeIntersection = false;
            runtimeAxis = TrafficLightAxis.Auto;
            runtimeApproachSign = 0;
            runtimeStopCoordinate = 0f;
            runtimeIntersectionName = string.Empty;
            hasRuntimeCrosswalk = false;
            runtimeCrosswalkBounds = new Bounds();
            runtimeCrosswalkOuterEdge = Vector3.zero;

            if (intersectionBounds == null || intersectionBounds.Length == 0)
                return;

            Vector3 position = transform.position;
            position.y = 0f;
            float nearestSqr = float.MaxValue;
            Bounds nearestBounds = new Bounds();
            bool found = false;

            for (int i = 0; i < intersectionBounds.Length; i++)
            {
                Bounds bounds = intersectionBounds[i];
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

            // When no CrosswalkPriorityZone exists on this approach, keep enough
            // clearance for the complete painted crossing. Junction bounds often end
            // near its centre, so the former 1.25 m offset stopped cars on the stripes.
            runtimeStopCoordinate = centerCoordinate +
                runtimeApproachSign *
                (intersectionExtent + Mathf.Max(1.25f, fallbackStopOffset));

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
                    float outerEdgeCoordinate = crossCoordinate + runtimeApproachSign * crossExtent;
                    hasRuntimeCrosswalk = true;
                    runtimeCrosswalkBounds = referenceCrosswalkBounds;
                    runtimeCrosswalkOuterEdge = referenceCrosswalkBounds.center;
                    if (runtimeAxis == TrafficLightAxis.X)
                        runtimeCrosswalkOuterEdge.x = outerEdgeCoordinate;
                    else
                        runtimeCrosswalkOuterEdge.z = outerEdgeCoordinate;
                    runtimeStopCoordinate = crossCoordinate +
                        runtimeApproachSign * (crossExtent + 0.15f);
                }
            }

            ApplyVisualState(true);
        }

        protected void BindToIntersectionFromOrientation(
            Bounds intersectionBounds,
            string intersectionName,
            CrosswalkPriorityZone[] crosswalks)
        {
            Vector3 forward = transform.forward;
            forward.y = 0f;
            hasRuntimeIntersection = false;
            runtimeAxis = TrafficLightAxis.Auto;
            runtimeApproachSign = 0;
            runtimeStopCoordinate = 0f;
            runtimeIntersectionName = string.Empty;
            hasRuntimeCrosswalk = false;
            runtimeCrosswalkBounds = new Bounds();
            runtimeCrosswalkOuterEdge = Vector3.zero;

            if (forward.sqrMagnitude < 0.001f)
                return;

            forward.Normalize();
            runtimeAxis = Mathf.Abs(forward.x) >= Mathf.Abs(forward.z)
                ? TrafficLightAxis.X
                : TrafficLightAxis.Z;
            float facingComponent = runtimeAxis == TrafficLightAxis.X ? forward.x : forward.z;
            runtimeApproachSign = facingComponent >= 0f ? 1 : -1;
            runtimeIntersectionCenter = intersectionBounds.center;
            runtimeIntersectionCenter.y = 0f;
            runtimeIntersectionName = intersectionName ?? string.Empty;
            hasRuntimeIntersection = true;

            float centerCoordinate = AxisCoordinate(runtimeIntersectionCenter, runtimeAxis);
            float intersectionExtent = AxisExtent(intersectionBounds, runtimeAxis);
            runtimeStopCoordinate = centerCoordinate + runtimeApproachSign *
                (intersectionExtent + Mathf.Max(1.25f, fallbackStopOffset));

            Bounds matchingCrosswalk = new Bounds();
            bool foundCrosswalk = false;
            float bestCrosswalkScore = float.MaxValue;
            if (crosswalks != null)
            {
                for (int i = 0; i < crosswalks.Length; i++)
                {
                    CrosswalkPriorityZone zone = crosswalks[i];
                    if (zone == null)
                        continue;

                    BoxCollider box = zone.GetComponent<BoxCollider>();
                    if (box == null)
                        continue;

                    Bounds bounds = box.bounds;
                    Vector3 relative = bounds.center - runtimeIntersectionCenter;
                    relative.y = 0f;
                    float axisOffset = AxisCoordinate(relative, runtimeAxis);
                    float perpendicularOffset = Mathf.Abs(PerpendicularCoordinate(relative, runtimeAxis));
                    float perpendicularExtent = runtimeAxis == TrafficLightAxis.X
                        ? intersectionBounds.extents.z
                        : intersectionBounds.extents.x;

                    if (runtimeApproachSign * axisOffset < -0.5f ||
                        Mathf.Abs(axisOffset) > intersectionExtent + 8f ||
                        perpendicularOffset > perpendicularExtent + 5f)
                        continue;

                    float axisExtent = AxisExtent(bounds, runtimeAxis);
                    float perpendicularCrosswalkExtent = runtimeAxis == TrafficLightAxis.X
                        ? bounds.extents.z
                        : bounds.extents.x;
                    // A crossing used by this approach must span across the driven axis.
                    // Longitudinal zones belong to the perpendicular road and must not
                    // become a stop line for this signal.
                    if (perpendicularCrosswalkExtent < axisExtent * 1.15f)
                        continue;

                    float expectedApproachEdge = runtimeApproachSign * intersectionExtent;
                    float shapePenalty = Mathf.Max(0f, axisExtent - perpendicularCrosswalkExtent);
                    float score = Mathf.Abs(axisOffset - expectedApproachEdge) +
                        perpendicularOffset * 0.25f + shapePenalty;
                    if (score >= bestCrosswalkScore)
                        continue;

                    bestCrosswalkScore = score;
                    matchingCrosswalk = bounds;
                    foundCrosswalk = true;
                }
            }

            if (foundCrosswalk)
            {
                float crossCoordinate = AxisCoordinate(matchingCrosswalk.center, runtimeAxis);
                float crossExtent = AxisExtent(matchingCrosswalk, runtimeAxis);
                float outerEdgeCoordinate = crossCoordinate + runtimeApproachSign * crossExtent;
                hasRuntimeCrosswalk = true;
                runtimeCrosswalkBounds = matchingCrosswalk;
                runtimeCrosswalkOuterEdge = matchingCrosswalk.center;
                if (runtimeAxis == TrafficLightAxis.X)
                    runtimeCrosswalkOuterEdge.x = outerEdgeCoordinate;
                else
                    runtimeCrosswalkOuterEdge.z = outerEdgeCoordinate;
                runtimeStopCoordinate = outerEdgeCoordinate + runtimeApproachSign * 0.15f;
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
                float activeWindow = green + yellow + clearance;
                float red = Mathf.Max(redDuration, activeWindow);
                float cycle = activeWindow + red;
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

                if (phase < activeWindow)
                    return TrafficLightState.Red;

                float secondPhase = phase - activeWindow;
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
            return TryAffect(position, travelDirection, out _);
        }

        public bool TryAffect(Vector3 position, Vector3 travelDirection, out string reason)
        {
            return TryAffect(position, travelDirection, detectionDistance, out reason);
        }

        public bool TryAffect(
            Vector3 position,
            Vector3 travelDirection,
            float requiredDetectionDistance,
            out string reason)
        {
            travelDirection.y = 0f;
            if (travelDirection.sqrMagnitude < 0.01f)
            {
                reason = "direction du véhicule trop faible";
                return false;
            }
            travelDirection.Normalize();

            if (hasRuntimeIntersection)
            {
                TrafficLightAxis travelAxis = Mathf.Abs(travelDirection.x) >= Mathf.Abs(travelDirection.z)
                    ? TrafficLightAxis.X
                    : TrafficLightAxis.Z;
                if (travelAxis != ResolveAxis())
                {
                    reason = $"axe opposé (véhicule={travelAxis}, feu={ResolveAxis()})";
                    return false;
                }

                if (!MatchesRuntimeApproachDirection(travelDirection))
                {
                    reason = "sens opposé à l'approche contrôlée";
                    return false;
                }

                float positionCoordinate = AxisCoordinate(position, runtimeAxis);
                float centerCoordinate = AxisCoordinate(runtimeIntersectionCenter, runtimeAxis);
                float approachSide = runtimeApproachSign *
                    (positionCoordinate - centerCoordinate);
                if (approachSide < -1.5f)
                {
                    reason = $"mauvais côté ou carrefour déjà franchi ({approachSide:F2} m)";
                    return false;
                }

                float distanceToLine = DistanceAhead(position, travelDirection);
                float effectiveDetectionDistance = GetEffectiveDetectionDistance(requiredDetectionDistance);
                if (distanceToLine < -0.9f || distanceToLine > effectiveDetectionDistance)
                {
                    reason = $"ligne hors portée ({distanceToLine:F2} m)";
                    return false;
                }

                float lateral = Mathf.Abs(
                    PerpendicularCoordinate(position, runtimeAxis) -
                    PerpendicularCoordinate(runtimeIntersectionCenter, runtimeAxis));
                if (lateral > laneTolerance)
                {
                    reason = $"voie différente (écart latéral {lateral:F2} m)";
                    return false;
                }

                reason = $"axe, côté et sens valides (ligne à {distanceToLine:F2} m)";
                return true;
            }

            Vector3 controlForward = transform.forward;
            controlForward.y = 0f;
            if (controlForward.sqrMagnitude > 0.01f)
            {
                float facing = Vector3.Dot(controlForward.normalized, travelDirection);
                // The lamp faces approaching traffic. A positive dot means the car is
                // travelling with the signal (away from the junction), so this is the
                // opposite approach even when the pole is very close.
                if (facing > -0.45f)
                {
                    reason = $"sens opposé au feu non raccordé (alignement {facing:F2})";
                    return false;
                }
            }

            Vector3 delta = transform.position - position;
            delta.y = 0f;
            float ahead = Vector3.Dot(delta, travelDirection);
            float lateralFallback = (delta - travelDirection * ahead).magnitude;
            if (ahead < -0.75f)
            {
                reason = "feu déjà dépassé";
                return false;
            }
            float fallbackDetectionDistance = GetEffectiveDetectionDistance(requiredDetectionDistance);
            if (ahead > fallbackDetectionDistance)
            {
                reason = $"feu trop éloigné ({ahead:F2} m)";
                return false;
            }
            if (lateralFallback > laneTolerance)
            {
                reason = $"voie différente (écart latéral {lateralFallback:F2} m)";
                return false;
            }

            reason = $"association de secours valide (ligne à {ahead:F2} m)";
            return true;
        }

        public float GetEffectiveDetectionDistance(float requiredDetectionDistance)
        {
            return Mathf.Max(detectionDistance, requiredDetectionDistance);
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
