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
            vehicles = vehiclePoolRoot != null
                ? vehiclePoolRoot.GetComponentsInChildren<TrafficVehicleAgent>(true)
                : new TrafficVehicleAgent[0];
            pedestrians = pedestrianPoolRoot != null
                ? pedestrianPoolRoot.GetComponentsInChildren<PedestrianAgent>(true)
                : new PedestrianAgent[0];

            int vehicleCount = Mathf.Min(maxVisibleVehicles, vehicles.Length);
            bool useSingleDirection = vehicleCount > vehicleRoutes.Length;
            TrafficRoutePath sharedRoute = useSingleDirection && vehicleRoutes.Length > 0
                ? vehicleRoutes[0]
                : null;
            int sharedSpacing = sharedRoute != null
                ? Mathf.Max(1, sharedRoute.Count / vehicleCount)
                : 1;
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
                bool enabled = i < pedestrianCount && validPedestrianRoutes.Count > 0;
                pedestrians[i].gameObject.SetActive(enabled);
                if (enabled)
                {
                    int routeIndex = i % validPedestrianRoutes.Count;
                    TrafficRoutePath assignedRoute = validPedestrianRoutes[routeIndex];
                    int agentsOnRoute = Mathf.CeilToInt((float)pedestrianCount / validPedestrianRoutes.Count);
                    int spacing = Mathf.Max(1, assignedRoute.Count / Mathf.Max(1, agentsOnRoute));
                    int occurrence = i / pedestrianRoutes.Length;
                    pedestrians[i].Initialize(this, assignedRoute, occurrence * spacing);
                }
            }

            if (maxVisiblePedestrians > 0 && pedestrians.Length == 0)
                Debug.Log("[MobileTraffic] Aucun prefab humain léger hors o3n n'est disponible; le pool piéton reste vide.", this);
        }

        public float GetLeadVehicleDistance(TrafficVehicleAgent self, Vector3 forward, float maxDistance)
        {
            float nearest = maxDistance;
            Vector3 position = self.transform.position;

            for (int i = 0; i < vehicles.Length; i++)
            {
                TrafficVehicleAgent other = vehicles[i];
                if (other == null || other == self || !other.isActiveAndEnabled)
                    continue;

                Vector3 delta = other.transform.position - position;
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
            for (int i = 0; i < crosswalks.Length; i++)
            {
                CrosswalkPriorityZone zone = crosswalks[i];
                if (zone == null || !zone.HasPedestrian)
                    continue;

                Vector3 delta = zone.Bounds.center - position;
                float ahead = Vector3.Dot(delta, forward);
                float lateral = (delta - forward * ahead).magnitude;
                if (ahead > -1f && ahead < distance && lateral < 5f &&
                    zone.HasPedestrianNear(position + forward * Mathf.Max(2f, ahead), 4.5f))
                    return true;
            }

            return false;
        }

        public TrafficControlPoint FindBlockingControl(Vector3 position, Vector3 forward)
        {
            TrafficControlPoint nearest = null;
            float nearestDistance = float.MaxValue;

            for (int i = 0; i < controls.Length; i++)
            {
                TrafficControlPoint control = controls[i];
                if (control == null || (control.Kind == TrafficControlKind.TrafficLight && !control.IsRed))
                    continue;

                if (!control.Affects(position, forward))
                    continue;

                Vector3 delta = control.transform.position - position;
                float ahead = Vector3.Dot(delta, forward);
                if (ahead < nearestDistance)
                {
                    nearest = control;
                    nearestDistance = ahead;
                }
            }

            return nearest;
        }

        public bool CanProceedIntersection(TrafficVehicleAgent vehicle, Vector3 position, Vector3 forward, float lookAhead)
        {
            for (int i=0;i<intersections.Length;i++)
            {
                var zone=intersections[i]; if(zone==null)continue;
                Vector3 delta=zone.Bounds.center-position; delta.y=0f;
                float ahead=Vector3.Dot(delta,forward); float lateral=(delta-forward*ahead).magnitude;
                if (zone.Bounds.Contains(position) || (ahead>=0f && ahead<=lookAhead && lateral<=zone.Bounds.extents.magnitude))
                    if(!zone.TryReserve(vehicle)) return false;
            }
            return true;
        }
        public void UpdateIntersectionReservation(TrafficVehicleAgent vehicle){for(int i=0;i<intersections.Length;i++)if(intersections[i]!=null)intersections[i].UpdateOwner(vehicle);}
    }
}
