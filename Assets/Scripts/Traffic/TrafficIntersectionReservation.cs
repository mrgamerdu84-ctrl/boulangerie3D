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
        private float ownerBestApproachDistance;
        private float ownerLastProgressAt;
        private TrafficVehicleAgent timedOutOwner;
        private float timedOutOwnerUntil;
        private BoxCollider box;

        private const float ProgressEpsilon = 0.1f;
        private const float TakeoverMargin = 0.5f;
        private const float RetryCooldown = 0.75f;

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
                ClearOwner(false);
                return;
            }

            if (ownerEntered)
                return;

            float distance = DistanceToIntersection(owner);
            if (distance + ProgressEpsilon < ownerBestApproachDistance)
            {
                ownerBestApproachDistance = distance;
                ownerLastProgressAt = Time.time;
                return;
            }

            // Expire only a reservation whose owner has stopped making progress.
            // The timed-out owner gets a short cooldown so it cannot immediately
            // reacquire the lock and starve every other approach forever.
            if (Time.time - ownerLastProgressAt > approachReservationTimeout)
                ClearOwner(true);
        }

        public bool TryReserve(TrafficVehicleAgent vehicle)
        {
            if (vehicle == null)
                return false;

            if (owner == vehicle)
                return true;

            if (owner != null)
            {
                // Until a vehicle enters, priority belongs to the closest approach.
                // A distant/off-screen vehicle must not hold a green junction hostage.
                if (ownerEntered ||
                    DistanceToIntersection(vehicle) + TakeoverMargin >= DistanceToIntersection(owner))
                    return false;

                AssignOwner(vehicle);
                return true;
            }

            if (vehicle == timedOutOwner && Time.time < timedOutOwnerUntil)
                return false;

            Bounds bounds = Bounds;
            if (!bounds.Contains(vehicle.transform.position) &&
                bounds.SqrDistance(vehicle.transform.position) > maximumApproachDistance * maximumApproachDistance)
                return false;

            AssignOwner(vehicle);
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
                ClearOwner(false);
        }

        public void Release(TrafficVehicleAgent vehicle)
        {
            if (owner == vehicle)
                ClearOwner(false);
        }

        private float DistanceToIntersection(TrafficVehicleAgent vehicle)
        {
            return vehicle == null
                ? float.MaxValue
                : Mathf.Sqrt(Bounds.SqrDistance(vehicle.transform.position));
        }

        private void AssignOwner(TrafficVehicleAgent vehicle)
        {
            owner = vehicle;
            ownerEntered = Bounds.Contains(vehicle.transform.position);
            ownerBestApproachDistance = DistanceToIntersection(vehicle);
            ownerLastProgressAt = Time.time;
        }

        private void EnsureCollider()
        {
            if (box == null)
                box = GetComponent<BoxCollider>();
        }

        private void ClearOwner(bool timedOut)
        {
            if (timedOut)
            {
                timedOutOwner = owner;
                timedOutOwnerUntil = Time.time + RetryCooldown;
            }

            owner = null;
            ownerEntered = false;
            ownerBestApproachDistance = float.MaxValue;
            ownerLastProgressAt = 0f;
        }
    }
}
