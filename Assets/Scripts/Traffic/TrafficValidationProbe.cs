using System.Linq;
using UnityEngine;

namespace Boulangerie3D.Traffic
{
    [DisallowMultipleComponent]
    public sealed class TrafficValidationProbe : MonoBehaviour
    {
        [SerializeField] private float duration = 25f;

        private TrafficVehicleAgent[] vehicles;
        private PedestrianAgent[] pedestrians;
        private float elapsed;
        private float minimumVehicleDistance = float.MaxValue;
        private float maximumVehicleRouteDeviation;
        private float maximumPedestrianRouteDeviation;
        private bool finished;

        private void Start()
        {
            vehicles = FindObjectsByType<TrafficVehicleAgent>(FindObjectsSortMode.None)
                .Where(agent => agent.isActiveAndEnabled).ToArray();
            pedestrians = FindObjectsByType<PedestrianAgent>(FindObjectsSortMode.None)
                .Where(agent => agent.isActiveAndEnabled).ToArray();
        }

        private void Update()
        {
            if (finished)
                return;

            elapsed += Time.deltaTime;
            for (int i = 0; i < vehicles.Length; i++)
            {
                TrafficVehicleAgent vehicle = vehicles[i];
                if (vehicle != null && vehicle.Route != null)
                    maximumVehicleRouteDeviation = Mathf.Max(maximumVehicleRouteDeviation,
                        vehicle.Route.DistanceToPath(vehicle.transform.position));
            }

            for (int i = 0; i < pedestrians.Length; i++)
            {
                PedestrianAgent pedestrian = pedestrians[i];
                if (pedestrian != null && pedestrian.Route != null)
                    maximumPedestrianRouteDeviation = Mathf.Max(maximumPedestrianRouteDeviation,
                        pedestrian.Route.DistanceToPath(pedestrian.transform.position));
            }

            for (int i = 0; i < vehicles.Length; i++)
                for (int j = i + 1; j < vehicles.Length; j++)
                    minimumVehicleDistance = Mathf.Min(minimumVehicleDistance,
                        Vector3.Distance(vehicles[i].transform.position, vehicles[j].transform.position));

            if (elapsed < duration)
                return;

            finished = true;
            int movingVehicles = vehicles.Count(agent => agent != null && agent.DistanceTravelled >= 8f);
            int movingPedestrians = pedestrians.Count(agent => agent != null && agent.DistanceTravelled >= 4f);
            float longestVehicleStop = vehicles.Length == 0 ? 0f : vehicles.Max(agent => agent.LongestStationaryDuration);
            float longestPedestrianStop = pedestrians.Length == 0 ? 0f : pedestrians.Max(agent => agent.LongestStationaryDuration);
            bool passed = vehicles.Length >= 4 && vehicles.Length <= 6 &&
                          pedestrians.Length >= 6 && pedestrians.Length <= 10 &&
                          movingVehicles == vehicles.Length && movingPedestrians == pedestrians.Length &&
                          minimumVehicleDistance >= 2.5f && maximumVehicleRouteDeviation <= 1.25f &&
                          maximumPedestrianRouteDeviation <= 0.75f && longestVehicleStop < 8f &&
                          longestPedestrianStop < 8f;

            string vehicleDetails = string.Join("; ", vehicles.Select(agent => agent == null
                ? "<missing>"
                : $"{agent.name}[pos={agent.transform.position:F1},travel={agent.DistanceTravelled:F1}," +
                  $"speed={agent.CurrentSpeed:F1},dev={(agent.Route == null ? -1f : agent.Route.DistanceToPath(agent.transform.position)):F1}," +
                  $"stop={agent.LongestStationaryDuration:F1}]"));
            string pedestrianDetails = string.Join("; ", pedestrians.Select(agent => agent == null
                ? "<missing>"
                : $"{agent.name}[pos={agent.transform.position:F1},travel={agent.DistanceTravelled:F1}," +
                  $"dev={(agent.Route == null ? -1f : agent.Route.DistanceToPath(agent.transform.position)):F1}," +
                  $"stop={agent.LongestStationaryDuration:F1}]"));

            Debug.Log($"TRAFFIC_VALIDATION_{(passed ? "PASS" : "FAIL")}: " +
                      $"cars={vehicles.Length}, movingCars={movingVehicles}, pedestrians={pedestrians.Length}, " +
                      $"movingPedestrians={movingPedestrians}, minCarDistance={minimumVehicleDistance:F2}m, " +
                      $"maxCarRouteDeviation={maximumVehicleRouteDeviation:F2}m, " +
                      $"maxPedRouteDeviation={maximumPedestrianRouteDeviation:F2}m, " +
                      $"longestCarStop={longestVehicleStop:F2}s, longestPedStop={longestPedestrianStop:F2}s, " +
                      $"duration={elapsed:F1}s\nVehicles: {vehicleDetails}\nPedestrians: {pedestrianDetails}", this);
        }
    }
}
