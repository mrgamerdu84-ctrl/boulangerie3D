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
        [SerializeField] private TrafficRoutePath[] pedestrianRoutes = new TrafficRoutePath[0];
        [SerializeField] private CrosswalkPriorityZone[] crosswalks = new CrosswalkPriorityZone[0];
        [SerializeField] private TrafficControlPoint[] controls = new TrafficControlPoint[0];
        [SerializeField] private TrafficIntersectionReservation[] intersections = new TrafficIntersectionReservation[0];

        private TrafficVehicleAgent[] vehicles = new TrafficVehicleAgent[0];
        private PedestrianAgent[] pedestrians = new PedestrianAgent[0];

        public CrosswalkPriorityZone[] Crosswalks => crosswalks;
        public TrafficVehicleAgent[] Vehicles => vehicles;
        public PedestrianAgent[] Pedestrians => pedestrians;

        private void Awake()
        {
            TrafficControlAutoBinder.BindSceneControls();

            TrafficControlPoint[] discoveredControls = FindObjectsByType<TrafficControlPoint>(FindObjectsSortMode.None);
            if (discoveredControls.Length > 0)
                controls = discoveredControls;

            TrafficIntersectionReservation[] discoveredIntersections = FindObjectsByType<TrafficIntersectionReservation>(FindObjectsSortMode.None);
            if (discoveredIntersections.Length > 0)
                intersections = discoveredIntersections;

            CrosswalkPriorityZone[] discoveredCrosswalks = FindObjectsByType<CrosswalkPriorityZone>(FindObjectsSortMode.None);
            if (discoveredCrosswalks.Length > 0)
                crosswalks = discoveredCrosswalks;

            for (int i = 0; i < controls.Length; i++)
                if (controls[i] != null)
                    controls[i].BindToNearestIntersection(intersections, crosswalks);

            vehicles = vehiclePoolRoot != null ? vehiclePoolRoot.GetComponentsInChildren<TrafficVehicleAgent>(true) : new TrafficVehicleAgent[0];

            PreparePlacedPedestrians();
            pedestrians = pedestrianPoolRoot != null
                ? pedestrianPoolRoot.GetComponentsInChildren<PedestrianAgent>(true)
                : FindObjectsByType<PedestrianAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            int vehicleCount = Mathf.Min(maxVisibleVehicles, vehicles.Length);
            bool useSingleDirection = vehicleCount > vehicleRoutes.Length;
            TrafficRoutePath sharedRoute = useSingleDirection && vehicleRoutes.Length > 0 ? vehicleRoutes[0] : null;
            int sharedSpacing = sharedRoute != null ? Mathf.Max(1, sharedRoute.Count / vehicleCount) : 1;
            for (int i = 0; i < vehicles.Length; i++)
            {
                bool enabled = i < vehicleCount && vehicleRoutes.Length > 0;
                vehicles[i].gameObject.SetActive(enabled);
                if (enabled)
                {
                    TrafficRoutePath route = sharedRoute ?? vehicleRoutes[i % vehicleRoutes.Length];
                    int startIndex = sharedRoute != null ? i * sharedSpacing : i * 3;
                    vehicles[i].Initialize(this, route, startIndex);
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
                if (pedestrian == null) continue;
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
        }

        private void PreparePlacedPedestrians()
        {
            if (pedestrianPoolRoot == null) return;
            for (int i = 0; i < pedestrianPoolRoot.childCount; i++)
            {
                Transform child = pedestrianPoolRoot.GetChild(i);
                if (child == null || child.GetComponentInChildren<PedestrianAgent>(true) != null) continue;
                if (child.GetComponentInChildren<Renderer>(true) == null && child.GetComponentInChildren<Animator>(true) == null) continue;
                Rigidbody body = child.GetComponent<Rigidbody>();
                if (body == null) body = child.gameObject.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.useGravity = false;
                body.interpolation = RigidbodyInterpolation.None;
                child.gameObject.AddComponent<PedestrianAgent>();
            }
        }

        public float GetLeadVehicleDistance(TrafficVehicleAgent self, Vector3 forward, float maxDistance)
        {
            float nearest = maxDistance;
            Vector3 position = self.transform.position;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) return nearest;
            forward.Normalize();
            for (int i = 0; i < vehicles.Length; i++)
            {
                TrafficVehicleAgent other = vehicles[i];
                if (other == null || other == self || !other.isActiveAndEnabled) continue;
                Vector3 delta = other.transform.position - position;
                delta.y = 0f;
                float forwardDistance = Vector3.Dot(delta, forward);
                if (forwardDistance <= 0f || forwardDistance >= nearest) continue;
                float lateralDistance = (delta - forward * forwardDistance).magnitude;
                if (lateralDistance < 2.1f) nearest = forwardDistance;
            }
            return nearest;
        }

        public bool CrosswalkRequiresStop(Vector3 position, Vector3 forward, float distance) { return false; }

        public TrafficControlPoint FindBlockingControl(Vector3 position, Vector3 forward)
        {
            TrafficControlPoint nearest = null;
            float nearestDistance = float.MaxValue;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) return null;
            forward.Normalize();

            for (int i = 0; i < controls.Length; i++)
            {
                TrafficControlPoint control = controls[i];
                if (control == null || control.Kind != TrafficControlKind.TrafficLight || control.IsGreen) continue;

                // Important : on ne fait plus aucun secours basé sur la proximité physique
                // du poteau. Un véhicule ne doit réagir qu'au feu qui correspond à son axe,
                // son sens et son côté d'approche.
                if (!control.Affects(position, forward)) continue;

                float ahead = control.DistanceAhead(position, forward);
                if (ahead >= -2.25f && ahead < nearestDistance)
                {
                    nearest = control;
                    nearestDistance = ahead;
                }
            }

            return nearest;
        }

        public bool CanProceedIntersection(TrafficVehicleAgent vehicle, Vector3 position, Vector3 forward, float lookAhead)
        {
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) return true;
            forward.Normalize();
            TrafficIntersectionReservation nearest = null;
            float nearestAhead = float.MaxValue;
            for (int i = 0; i < intersections.Length; i++)
            {
                TrafficIntersectionReservation zone = intersections[i];
                if (zone == null) continue;
                Bounds bounds = zone.Bounds;
                if (bounds.Contains(position)) { nearest = zone; break; }
                Vector3 delta = bounds.center - position;
                delta.y = 0f;
                float ahead = Vector3.Dot(delta, forward);
                if (ahead < 0f || ahead > lookAhead) continue;
                float lateral = (delta - forward * ahead).magnitude;
                float radius = Mathf.Sqrt(bounds.extents.x * bounds.extents.x + bounds.extents.z * bounds.extents.z) + 1f;
                if (lateral > radius || ahead >= nearestAhead) continue;
                nearest = zone;
                nearestAhead = ahead;
            }
            return nearest == null || nearest.TryReserve(vehicle);
        }

        public void UpdateIntersectionReservation(TrafficVehicleAgent vehicle)
        {
            for (int i = 0; i < intersections.Length; i++) if (intersections[i] != null) intersections[i].UpdateOwner(vehicle);
        }

        public void ReleaseIntersectionReservations(TrafficVehicleAgent vehicle)
        {
            for (int i = 0; i < intersections.Length; i++) if (intersections[i] != null) intersections[i].Release(vehicle);
        }
    }
}
