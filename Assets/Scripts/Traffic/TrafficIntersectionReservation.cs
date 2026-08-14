using UnityEngine;

namespace Boulangerie3D.Traffic
{
    [RequireComponent(typeof(BoxCollider))]
    public sealed class TrafficIntersectionReservation : MonoBehaviour
    {
        [SerializeField, Min(1f)] private float approachReservationTimeout = 4f;
        [SerializeField, Min(2f)] private float maximumApproachDistance = 15f;

        private TrafficVehicleAgent owner;
        private bool ownerEntered;
        private float reservedAt;
        private BoxCollider box;

        public Bounds Bounds
        {
            get
            {
                EnsureCollider();
                return box.bounds;
            }
        }

        private void Awake()
        {
            EnsureCollider();
            box.isTrigger = true;
        }

        private void Update()
        {
            if (owner == null)
                return;

            if (!owner.isActiveAndEnabled)
            {
                ClearOwner();
                return;
            }

            // A reservation made on approach must not survive forever if the car
            // never reaches the intersection (route change, obstacle, disabled car, etc.).
            if (!ownerEntered && Time.time - reservedAt > approachReservationTimeout)
                ClearOwner();
        }

        public bool TryReserve(TrafficVehicleAgent vehicle)
        {
            if (vehicle == null)
                return false;

            if (owner == vehicle)
                return true;

            if (owner != null)
                return false;

            Bounds bounds = Bounds;
            if (!bounds.Contains(vehicle.transform.position) &&
                bounds.SqrDistance(vehicle.transform.position) > maximumApproachDistance * maximumApproachDistance)
                return false;

            owner = vehicle;
            ownerEntered = bounds.Contains(vehicle.transform.position);
            reservedAt = Time.time;
            return true;
        }

        public void UpdateOwner(TrafficVehicleAgent vehicle)
        {
            if (owner != vehicle)
                return;

            bool inside = Bounds.Contains(vehicle.transform.position);
            if (inside)
            {
                ownerEntered = true;
                return;
            }

            if (ownerEntered)
                ClearOwner();
        }

        public void Release(TrafficVehicleAgent vehicle)
        {
            if (owner == vehicle)
                ClearOwner();
        }

        private void EnsureCollider()
        {
            if (box == null)
                box = GetComponent<BoxCollider>();
        }

        private void ClearOwner()
        {
            owner = null;
            ownerEntered = false;
            reservedAt = 0f;
        }
    }
}
