using UnityEngine;

namespace Boulangerie3D.Traffic
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed class TrafficVehicleAgent : MonoBehaviour
    {
        [SerializeField, Min(1f)] private float cruiseSpeed = 5.5f;
        [SerializeField, Min(1f)] private float acceleration = 3.5f;
        [SerializeField, Min(1f)] private float braking = 8f;
        [SerializeField, Min(2f)] private float safetyDistance = 7f;
        [SerializeField, Min(1f)] private float turnSpeed = 7f;

        private MobileTrafficController controller;
        private TrafficRoutePath route;
        private int targetIndex;
        private float speed;
        private TrafficControlPoint servedStop;
        private float stopTimer;
        private Rigidbody cachedBody;
        private readonly RaycastHit[] obstacleHits = new RaycastHit[12];
        private Vector3 previousPosition;
        private float distanceTravelled;
        private float stationaryDuration;
        private float longestStationaryDuration;

        public TrafficRoutePath Route => route;
        public float CurrentSpeed => speed;
        public float DistanceTravelled => distanceTravelled;
        public float StationaryDuration => stationaryDuration;
        public float LongestStationaryDuration => longestStationaryDuration;

        private void Awake()
        {
            cachedBody = GetComponent<Rigidbody>();
            cachedBody.isKinematic = true;
            cachedBody.useGravity = false;
            cachedBody.interpolation = RigidbodyInterpolation.None;
        }

        public void Initialize(MobileTrafficController owner, TrafficRoutePath assignedRoute, int startIndex)
        {
            controller = owner;
            route = assignedRoute;
            targetIndex = Mathf.Abs(startIndex) % Mathf.Max(1, route.Count);
            SetPosition(route.GetPoint(targetIndex));
            targetIndex = (targetIndex + 1) % route.Count;
            Vector3 direction = route.GetPoint(targetIndex) - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            speed = 0f;
            servedStop = null;
            stopTimer = 0f;
            previousPosition = transform.position;
            distanceTravelled = 0f;
            stationaryDuration = 0f;
            longestStationaryDuration = 0f;
        }

        private void Update()
        {
            if (controller == null || route == null || route.Count < 2)
                return;

            Vector3 target = route.GetPoint(targetIndex);
            Vector3 planar = target - transform.position;
            planar.y = 0f;
            if (planar.magnitude < 0.45f)
            {
                targetIndex = (targetIndex + 1) % route.Count;
                target = route.GetPoint(targetIndex);
                planar = target - transform.position;
                planar.y = 0f;
            }

            Vector3 forward = planar.sqrMagnitude > 0.001f ? planar.normalized : transform.forward;
            bool mustBrake = controller.GetLeadVehicleDistance(this, forward, safetyDistance) < safetyDistance;
            mustBrake |= HasObstacleAhead(forward);
            mustBrake |= controller.CrosswalkRequiresStop(transform.position, forward, 9f);
            mustBrake |= !controller.CanProceedIntersection(this, transform.position, forward, safetyDistance + 5f);

            TrafficControlPoint control = controller.FindBlockingControl(transform.position, forward);
            if (control != null && control.Kind == TrafficControlKind.Stop && control == servedStop)
                control = null;

            if (control != null)
            {
                mustBrake = true;
                if (control.Kind == TrafficControlKind.Stop && speed < 0.08f)
                {
                    stopTimer += Time.deltaTime;
                    if (stopTimer >= 1f)
                    {
                        servedStop = control;
                        stopTimer = 0f;
                        mustBrake = false;
                    }
                }
            }
            else
            {
                stopTimer = 0f;
                if (servedStop != null && Vector3.Distance(transform.position, servedStop.transform.position) > 10f)
                    servedStop = null;
            }

            float desiredSpeed = mustBrake ? 0f : cruiseSpeed;
            float rate = desiredSpeed < speed ? braking : acceleration;
            speed = Mathf.MoveTowards(speed, desiredSpeed, rate * Time.deltaTime);

            if (forward.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(forward, Vector3.up), turnSpeed * Time.deltaTime);

            float travel = Mathf.Min(speed * Time.deltaTime, planar.magnitude);
            Vector3 corrected = transform.position + forward * travel;
            corrected.y = target.y;
            SetPosition(corrected);
            controller.UpdateIntersectionReservation(this);

            float movement = Vector3.Distance(transform.position, previousPosition);
            distanceTravelled += movement;
            stationaryDuration = movement < 0.002f ? stationaryDuration + Time.deltaTime : 0f;
            longestStationaryDuration = Mathf.Max(longestStationaryDuration, stationaryDuration);
            previousPosition = transform.position;
        }

        private bool HasObstacleAhead(Vector3 forward)
        {
            Vector3 origin = transform.position + forward * 2.1f + Vector3.up * 0.85f;
            int count = Physics.SphereCastNonAlloc(origin, 0.45f, forward, obstacleHits,
                Mathf.Max(0.5f, safetyDistance - 2.1f), ~0, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                Collider hit = obstacleHits[i].collider;
                if (hit == null || hit.transform.IsChildOf(transform))
                    continue;

                if (hit.GetComponentInParent<CrosswalkPriorityZone>() != null)
                    continue;

                // Road and sidewalk surfaces sit below the sensor and must not stop cars.
                if (hit.bounds.max.y < origin.y - 0.25f)
                    continue;

                string objectName = hit.name.ToLowerInvariant();
                if (objectName.Contains("road") || objectName.Contains("junction") ||
                    objectName.Contains("crosswalk") || objectName.Contains("sidewalk") ||
                    objectName.Contains("trottoir") || objectName.Contains("curb"))
                    continue;

                return true;
            }

            return false;
        }

        private void SetPosition(Vector3 position)
        {
            if (cachedBody != null) cachedBody.position = position;
            transform.position = position;
        }
    }
}
