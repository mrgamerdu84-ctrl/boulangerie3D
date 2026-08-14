using UnityEngine;

namespace Boulangerie3D.Traffic
{
    [DisallowMultipleComponent]
    public sealed class PedestrianRouteConnector : MonoBehaviour
    {
        [Header("Explicit route references")]
        [SerializeField] private TrafficRoutePath sourceRoute;
        [SerializeField] private TrafficRoutePath destinationRoute;
        [SerializeField] private Transform destinationWaypoint;
        [SerializeField] private bool bakeryCustomersOnly;

        [Header("Optional narrow passage")]
        [SerializeField] private PedestrianRouteConnector passageReservationOwner;
        [SerializeField] private bool acquirePassage;
        [SerializeField] private bool releasePassage;
        [SerializeField, Min(0.25f)] private float waitingDistance = 0.9f;

        private PedestrianAgent passageOccupant;

        public float WaitingDistance => waitingDistance;
        public bool AcquiresPassage => acquirePassage;
        public bool ReleasesPassage => releasePassage;
        public PedestrianRouteConnector PassageReservationOwner =>
            passageReservationOwner != null ? passageReservationOwner : this;

        public bool AppliesTo(TrafficRoutePath currentRoute, PedestrianRole role)
        {
            return sourceRoute == currentRoute &&
                   destinationRoute != null &&
                   destinationWaypoint != null &&
                   (!bakeryCustomersOnly || role == PedestrianRole.BakeryCustomer);
        }

        public bool IsPassageOccupiedByAnother(PedestrianAgent pedestrian)
        {
            if (!acquirePassage)
                return false;

            PedestrianRouteConnector owner = PassageReservationOwner;
            return owner.passageOccupant != null && owner.passageOccupant != pedestrian;
        }

        public bool TryGetDestination(
            PedestrianAgent pedestrian,
            PedestrianRole role,
            TrafficRoutePath currentRoute,
            out TrafficRoutePath nextRoute,
            out int destinationIndex)
        {
            nextRoute = null;
            destinationIndex = -1;
            if (pedestrian == null || !AppliesTo(currentRoute, role))
                return false;

            destinationIndex = destinationRoute.IndexOfWaypoint(destinationWaypoint);
            if (destinationIndex < 0)
                return false;

            PedestrianRouteConnector owner = PassageReservationOwner;
            if (acquirePassage)
            {
                if (owner.passageOccupant != null && owner.passageOccupant != pedestrian)
                    return false;
                owner.passageOccupant = pedestrian;
            }

            if (releasePassage)
                owner.ReleasePassage(pedestrian);

            nextRoute = destinationRoute;
            return true;
        }

        public void ReleasePassage(PedestrianAgent pedestrian)
        {
            PedestrianRouteConnector owner = PassageReservationOwner;
            if (owner.passageOccupant == pedestrian)
                owner.passageOccupant = null;
        }
    }
}
