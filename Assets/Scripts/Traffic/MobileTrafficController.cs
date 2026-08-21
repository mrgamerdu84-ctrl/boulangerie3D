using UnityEngine;
using System.Collections.Generic;

namespace Boulangerie3D.Traffic
{
    [DefaultExecutionOrder(-100)]
    public sealed class MobileTrafficController : MonoBehaviour
    {
        [Header("Fixed mobile pools")]
        [SerializeField, Range(1, 6)] private int maxVisibleVehicles = 4;
        [SerializeField, Range(0, 10)] private int maxVisiblePedestrians = 8;
        [SerializeField] private Transform vehiclePoolRoot;
        [SerializeField] private Transform pedestrianPoolRoot;

        [Header("Authored network")]
        [SerializeField] private TrafficRoutePath[] vehicleRoutes = new TrafficRoutePath[0];
        [SerializeField] private TrafficRoadSegment[] roadSegments = new TrafficRoadSegment[0];
        [SerializeField] private TrafficRoutePath[] pedestrianRoutes = new TrafficRoutePath[0];
        [SerializeField] private CrosswalkPriorityZone[] crosswalks = new CrosswalkPriorityZone[0];
        [SerializeField] private TrafficControlPoint[] controls = new TrafficControlPoint[0];
        [SerializeField] private TrafficIntersectionReservation[] intersections = new TrafficIntersectionReservation[0];

        [Header("Runtime road graph debug")]
        [SerializeField] private bool showRuntimeConnectorGizmos = true;

        private TrafficVehicleAgent[] vehicles = new TrafficVehicleAgent[0];
        private PedestrianAgent[] pedestrians = new PedestrianAgent[0];
        private TrafficRuntimeRoadGraph runtimeRoadGraph;
        private readonly Dictionary<(TrafficVehicleAgent vehicle, TrafficControlPoint control), string>
            lastControlDecisions =
                new Dictionary<(TrafficVehicleAgent vehicle, TrafficControlPoint control), string>();

        public CrosswalkPriorityZone[] Crosswalks => crosswalks;
        public TrafficVehicleAgent[] Vehicles => vehicles;
        public PedestrianAgent[] Pedestrians => pedestrians;
        public TrafficRoadSegment[] RoadSegments => roadSegments;
        public TrafficRuntimeRoadGraph RuntimeRoadGraph => runtimeRoadGraph;

        private void Awake()
        {
            TrafficControlAutoBinder.BindSceneControls();

            TrafficControlPoint[] discoveredControls = FindObjectsByType<TrafficControlPoint>(FindObjectsSortMode.None);
            if (discoveredControls.Length > 0)
                controls = discoveredControls;

            TrafficIntersectionReservation[] discoveredIntersections =
                FindObjectsByType<TrafficIntersectionReservation>(FindObjectsSortMode.None);
            if (discoveredIntersections.Length > 0)
                intersections = discoveredIntersections;

            CrosswalkPriorityZone[] discoveredCrosswalks =
                FindObjectsByType<CrosswalkPriorityZone>(FindObjectsSortMode.None);
            if (discoveredCrosswalks.Length > 0)
                crosswalks = discoveredCrosswalks;

            Bounds[] controlIntersectionBounds = DiscoverControlIntersectionBounds();

            TrafficRoutePath[] discoveredVehicleRoutes =
                FindObjectsByType<TrafficRoutePath>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var validDiscoveredRoutes = new List<TrafficRoutePath>();
            for (int i = 0; i < discoveredVehicleRoutes.Length; i++)
                if (discoveredVehicleRoutes[i] != null && !discoveredVehicleRoutes[i].IsPedestrianRoute &&
                    discoveredVehicleRoutes[i].IsValid)
                    validDiscoveredRoutes.Add(discoveredVehicleRoutes[i]);
            if (validDiscoveredRoutes.Count > 0)
                vehicleRoutes = validDiscoveredRoutes.ToArray();

            BoxCollider[] runtimeJunctions = FindObjectsByType<BoxCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var validRuntimeJunctions = new List<BoxCollider>();
            for (int i = 0; i < runtimeJunctions.Length; i++)
                if (runtimeJunctions[i] != null && runtimeJunctions[i].name.StartsWith(
                    "JunctionCollider_", System.StringComparison.Ordinal))
                    validRuntimeJunctions.Add(runtimeJunctions[i]);
            runtimeRoadGraph = TrafficRuntimeRoadGraph.Build(vehicleRoutes, validRuntimeJunctions.ToArray());

            for (int i = 0; i < controls.Length; i++)
                if (controls[i] != null)
                    controls[i].BindToNearestIntersection(controlIntersectionBounds, crosswalks);

            vehicles = vehiclePoolRoot != null
                ? vehiclePoolRoot.GetComponentsInChildren<TrafficVehicleAgent>(true)
                : new TrafficVehicleAgent[0];

            TrafficRoadSegment[] discoveredRoadSegments =
                FindObjectsByType<TrafficRoadSegment>(FindObjectsSortMode.None);
            if (discoveredRoadSegments.Length > 0)
                roadSegments = discoveredRoadSegments;

            PreparePlacedPedestrians();
            pedestrians = pedestrianPoolRoot != null
                ? pedestrianPoolRoot.GetComponentsInChildren<PedestrianAgent>(true)
                : FindObjectsByType<PedestrianAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            int vehicleCount = Mathf.Min(maxVisibleVehicles, vehicles.Length);
            var validRoadSegments = new List<TrafficRoadSegment>();
            for (int i = 0; i < roadSegments.Length; i++)
                if (roadSegments[i] != null && roadSegments[i].IsValid)
                    validRoadSegments.Add(roadSegments[i]);

            bool useRuntimeRoadGraph = runtimeRoadGraph != null && runtimeRoadGraph.IsUsable;
            bool useRoadGraph = !useRuntimeRoadGraph && validRoadSegments.Count > 0;
            bool useSingleDirection = !useRoadGraph && vehicleCount > vehicleRoutes.Length;
            TrafficRoutePath sharedRoute = useSingleDirection && vehicleRoutes.Length > 0
                ? vehicleRoutes[0]
                : null;
            int sharedSpacing = sharedRoute != null
                ? Mathf.Max(1, sharedRoute.Count / vehicleCount)
                : 1;
            for (int i = 0; i < vehicles.Length; i++)
            {
                bool enabled = i < vehicleCount &&
                    (useRuntimeRoadGraph || useRoadGraph || vehicleRoutes.Length > 0);
                vehicles[i].gameObject.SetActive(enabled);
                if (enabled)
                {
                    if (useRuntimeRoadGraph)
                    {
                        int distributedIndex = Mathf.FloorToInt(
                            (float)i * runtimeRoadGraph.Segments.Count / Mathf.Max(1, vehicleCount));
                        TrafficRuntimeRoadSegment segment = runtimeRoadGraph.Segments[
                            distributedIndex % runtimeRoadGraph.Segments.Count];
                        int occurrence = i / runtimeRoadGraph.Segments.Count;
                        int availableStartSteps = Mathf.Max(1, segment.PointCount - 1);
                        vehicles[i].InitializeRuntimeGraph(this, runtimeRoadGraph, segment,
                            occurrence % availableStartSteps);
                    }
                    else if (useRoadGraph)
                    {
                        TrafficRoadSegment segment = validRoadSegments[i % validRoadSegments.Count];
                        int occurrence = i / validRoadSegments.Count;
                        int availableStartSteps = Mathf.Max(1, segment.PointCount - 1);
                        vehicles[i].InitializeGraph(
                            this,
                            segment,
                            occurrence % availableStartSteps);
                    }
                    else
                    {
                        TrafficRoutePath route = sharedRoute ?? vehicleRoutes[i % vehicleRoutes.Length];
                        int startIndex = sharedRoute != null ? i * sharedSpacing : i * 3;
                        vehicles[i].Initialize(this, route, startIndex);
                    }
                }
            }

            int pedestrianCount = Mathf.Min(maxVisiblePedestrians, pedestrians.Length);
            var validPedestrianRoutes = new List<TrafficRoutePath>();
            for (int i = 0; i < pedestrianRoutes.Length; i++)
                if (pedestrianRoutes[i] != null && pedestrianRoutes[i].IsValid)
                    validPedestrianRoutes.Add(pedestrianRoutes[i]);

            for (int i = 0; i < pedestrians.Length; i++)
            {
                PedestrianAgent pedestrian = pedestrians[i];
                if (pedestrian == null)
                    continue;

                bool enabled = i < pedestrianCount && validPedestrianRoutes.Count > 0;
                pedestrian.gameObject.SetActive(enabled);
                if (enabled)
                {
                    int routeIndex = i % validPedestrianRoutes.Count;
                    TrafficRoutePath assignedRoute = validPedestrianRoutes[routeIndex];
                    int agentsOnRoute = Mathf.CeilToInt((float)pedestrianCount / validPedestrianRoutes.Count);
                    int spacing = Mathf.Max(1, assignedRoute.Count / Mathf.Max(1, agentsOnRoute));
                    int occurrence = i / validPedestrianRoutes.Count;
                    pedestrian.Initialize(this, assignedRoute, occurrence * spacing);
                }
            }

            if (maxVisiblePedestrians > 0 && pedestrians.Length == 0)
                Debug.LogWarning("[MobileTraffic] Aucun piéton disponible. Place les personnages sous le groupe piétons ou ajoute PedestrianAgent.", this);
            else if (pedestrians.Length > 0)
                Debug.Log($"[MobileTraffic] {Mathf.Min(maxVisiblePedestrians, pedestrians.Length)} piéton(s) activé(s) sur {validPedestrianRoutes.Count} itinéraire(s).", this);
        }

        private Bounds[] DiscoverControlIntersectionBounds()
        {
            var bounds = new List<Bounds>();

            for (int i = 0; i < intersections.Length; i++)
            {
                TrafficIntersectionReservation intersection = intersections[i];
                if (intersection != null)
                    AddUniqueBounds(bounds, intersection.Bounds);
            }

            BoxCollider[] authoredJunctions =
                FindObjectsByType<BoxCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < authoredJunctions.Length; i++)
            {
                BoxCollider candidate = authoredJunctions[i];
                if (candidate == null ||
                    !candidate.name.StartsWith("JunctionCollider_", System.StringComparison.Ordinal))
                    continue;

                AddUniqueBounds(bounds, candidate.bounds);
            }

            return bounds.ToArray();
        }

        private static void AddUniqueBounds(List<Bounds> bounds, Bounds candidate)
        {
            const float sameCenterToleranceSqr = 0.25f * 0.25f;
            for (int i = 0; i < bounds.Count; i++)
            {
                Vector3 delta = bounds[i].center - candidate.center;
                delta.y = 0f;
                if (delta.sqrMagnitude <= sameCenterToleranceSqr)
                    return;
            }

            bounds.Add(candidate);
        }

        private void PreparePlacedPedestrians()
        {
            if (pedestrianPoolRoot == null)
            {
                Transform[] allTransforms = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    Transform candidate = allTransforms[i];
                    if (candidate == null)
                        continue;

                    string key = NormalizeName(candidate.name);
                    if (key == "pedestrians" || key == "pedestrianpool" || key == "pietons" ||
                        key == "pietonpool" || key == "trafficpedestrians" || key == "passants")
                    {
                        pedestrianPoolRoot = candidate;
                        break;
                    }
                }
            }

            if (pedestrianPoolRoot == null)
                return;

            int added = 0;
            for (int i = 0; i < pedestrianPoolRoot.childCount; i++)
            {
                Transform child = pedestrianPoolRoot.GetChild(i);
                if (child == null)
                    continue;

                if (child.GetComponentInChildren<PedestrianAgent>(true) != null)
                    continue;

                // Only direct children of the dedicated pedestrian group are auto-prepared.
                // This prevents bakery staff or customers elsewhere in the scene from being
                // accidentally converted into street pedestrians.
                if (child.GetComponentInChildren<Renderer>(true) == null &&
                    child.GetComponentInChildren<Animator>(true) == null)
                    continue;

                Rigidbody body = child.GetComponent<Rigidbody>();
                if (body == null)
                    body = child.gameObject.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.useGravity = false;
                body.interpolation = RigidbodyInterpolation.None;

                child.gameObject.AddComponent<PedestrianAgent>();
                added++;
            }

            if (added > 0)
                Debug.Log($"[MobileTraffic] {added} nouveau(x) bonhomme(s) préparé(s) automatiquement comme piétons.", this);
        }

        private static string NormalizeName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.ToLowerInvariant()
                .Replace(" ", string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(".", string.Empty)
                .Replace("é", "e")
                .Replace("è", "e")
                .Replace("ê", "e");
        }

        public float GetLeadVehicleDistance(TrafficVehicleAgent self, Vector3 forward, float maxDistance)
        {
            float nearest = maxDistance;
            Vector3 position = self.transform.position;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                return nearest;
            forward.Normalize();

            for (int i = 0; i < vehicles.Length; i++)
            {
                TrafficVehicleAgent other = vehicles[i];
                if (other == null || other == self || !other.isActiveAndEnabled)
                    continue;

                Vector3 delta = other.transform.position - position;
                delta.y = 0f;
                float forwardDistance = Vector3.Dot(delta, forward);
                if (forwardDistance <= 0f || forwardDistance >= nearest)
                    continue;

                float lateralDistance = (delta - forward * forwardDistance).magnitude;
                if (lateralDistance < 2.1f)
                    nearest = forwardDistance;
            }

            return nearest;
        }

        public bool CrosswalkRequiresStop(Vector3 position, Vector3 forward, float distance)
        {
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                return false;
            forward.Normalize();

            for (int i = 0; i < crosswalks.Length; i++)
            {
                CrosswalkPriorityZone zone = crosswalks[i];
                if (zone == null || !zone.HasPedestrian)
                    continue;

                Vector3 delta = zone.Bounds.center - position;
                delta.y = 0f;
                float ahead = Vector3.Dot(delta, forward);
                float lateral = (delta - forward * ahead).magnitude;
                if (ahead > -1f && ahead < distance && lateral < 5f &&
                    zone.HasPedestrianNear(position + forward * Mathf.Max(2f, ahead), 4.5f))
                    return true;
            }

            return false;
        }

        public TrafficControlPoint FindBlockingControl(
            TrafficVehicleAgent vehicle,
            Vector3 position,
            Vector3 forward,
            float requiredDetectionDistance)
        {
            TrafficControlPoint nearest = null;
            float nearestDistance = float.MaxValue;

            for (int i = 0; i < controls.Length; i++)
            {
                TrafficControlPoint control = controls[i];
                if (control == null)
                    continue;

                bool accepted = control.TryAffect(
                    position,
                    forward,
                    requiredDetectionDistance,
                    out string reason);
                LogControlDecision(vehicle, control, accepted, reason);
                if (!accepted)
                    continue;

                if (control.Kind == TrafficControlKind.TrafficLight && control.IsGreen)
                    continue;

                float ahead = control.DistanceAhead(position, forward);
                if (ahead >= -0.9f && ahead < nearestDistance)
                {
                    nearest = control;
                    nearestDistance = ahead;
                }
            }

            return nearest;
        }

        private void LogControlDecision(
            TrafficVehicleAgent vehicle,
            TrafficControlPoint control,
            bool accepted,
            string reason)
        {
            if (vehicle == null || control == null)
                return;

            var key = (vehicle, control);
            int detailStart = reason.IndexOf(" (", System.StringComparison.Ordinal);
            string stableReason = detailStart >= 0 ? reason.Substring(0, detailStart) : reason;
            string decision = (accepted ? "ACCEPTED:" : "REJECTED:") + stableReason;
            if (lastControlDecisions.TryGetValue(key, out string previous) && previous == decision)
                return;

            lastControlDecisions[key] = decision;
            Debug.Log(
                $"[TrafficControlDebug] vehicle='{vehicle.name}', feu='{control.name}', " +
                $"résultat={(accepted ? "ACCEPTÉ" : "REJETÉ")}, raison={reason}",
                vehicle);
        }

        public bool CanProceedIntersection(TrafficVehicleAgent vehicle, Vector3 position, Vector3 forward, float lookAhead)
        {
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                return true;
            forward.Normalize();

            TrafficIntersectionReservation nearest = null;
            float nearestAhead = float.MaxValue;

            for (int i = 0; i < intersections.Length; i++)
            {
                TrafficIntersectionReservation zone = intersections[i];
                if (zone == null)
                    continue;

                Bounds bounds = zone.Bounds;
                if (bounds.Contains(position))
                {
                    nearest = zone;
                    nearestAhead = -0.01f;
                    break;
                }

                Vector3 delta = bounds.center - position;
                delta.y = 0f;
                float ahead = Vector3.Dot(delta, forward);
                if (ahead < 0f || ahead > lookAhead)
                    continue;

                float lateral = (delta - forward * ahead).magnitude;
                float horizontalRadius = Mathf.Sqrt(bounds.extents.x * bounds.extents.x +
                                                    bounds.extents.z * bounds.extents.z) + 1f;
                if (lateral > horizontalRadius || ahead >= nearestAhead)
                    continue;

                nearest = zone;
                nearestAhead = ahead;
            }

            return nearest == null || nearest.TryReserve(vehicle);
        }

        public void UpdateIntersectionReservation(TrafficVehicleAgent vehicle)
        {
            for (int i = 0; i < intersections.Length; i++)
                if (intersections[i] != null)
                    intersections[i].UpdateOwner(vehicle);
        }

        public void ReleaseIntersectionReservations(TrafficVehicleAgent vehicle)
        {
            for (int i = 0; i < intersections.Length; i++)
                if (intersections[i] != null)
                    intersections[i].Release(vehicle);
        }

        private void OnDrawGizmosSelected()
        {
            if (!showRuntimeConnectorGizmos || runtimeRoadGraph == null)
                return;

            for (int i = 0; i < runtimeRoadGraph.Connectors.Count; i++)
            {
                TrafficRuntimeTurnConnector connector = runtimeRoadGraph.Connectors[i];
                Gizmos.color = connector.Turn == TrafficTurnDirection.Left
                    ? new Color(1f, 0.55f, 0.05f, 0.9f)
                    : connector.Turn == TrafficTurnDirection.Right
                        ? new Color(0.2f, 0.8f, 1f, 0.9f)
                        : new Color(0.2f, 1f, 0.3f, 0.9f);
                for (int p = 0; p < connector.PointCount - 1; p++)
                    Gizmos.DrawLine(connector.GetPoint(p) + Vector3.up * 0.2f,
                        connector.GetPoint(p + 1) + Vector3.up * 0.2f);
            }
        }
    }
}
