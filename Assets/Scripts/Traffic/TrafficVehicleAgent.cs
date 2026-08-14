using UnityEngine;

namespace Boulangerie3D.Traffic
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed class TrafficVehicleAgent : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField, Min(1f)] private float cruiseSpeed = 5.5f;
        [SerializeField, Min(1f)] private float acceleration = 3.5f;
        [SerializeField, Min(1f)] private float braking = 8f;
        [SerializeField, Min(2f)] private float safetyDistance = 7f;
        [SerializeField, Min(1f)] private float turnSpeed = 7f;

        [Header("Traffic controls")]
        [SerializeField, Range(0.4f, 1.5f)] private float stopLineBuffer = 0.75f;
        [SerializeField, Range(0.1f, 1f)] private float yellowSafetyMargin = 0.35f;
        [SerializeField, Min(2f)] private float intersectionLookAhead = 5f;

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
            if (controller != null)
                controller.ReleaseIntersectionReservations(this);

            controller = owner;
            route = assignedRoute;
            if (route == null || route.Count < 2)
                return;

            targetIndex = Mathf.Abs(startIndex) % route.Count;
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
            forward.y = 0f;
            if (forward.sqrMagnitude > 0.001f)
                forward.Normalize();

            float leadDistance = controller.GetLeadVehicleDistance(this, forward, safetyDistance);
            bool followingBlocked = leadDistance < safetyDistance;
            bool obstacleBlocked = HasObstacleAhead(forward);
            bool pedestrianBlocked = controller.CrosswalkRequiresStop(transform.position, forward, 9f);

            TrafficControlPoint control = controller.FindBlockingControl(transform.position, forward);
            bool controlRequiresStop = false;
            float controlDistance = float.MaxValue;

            if (control != null && control.Kind == TrafficControlKind.Stop && control == servedStop)
                control = null;

            if (control != null)
            {
                controlDistance = Mathf.Max(0f, control.DistanceAhead(transform.position, forward));

                if (control.Kind == TrafficControlKind.Stop)
                {
                    controlRequiresStop = true;
                    bool atStopLine = controlDistance <= stopLineBuffer + 0.25f;
                    if (atStopLine && speed <= 0.12f)
                    {
                        stopTimer += Time.deltaTime;
                        if (stopTimer >= control.StopHoldDuration)
                        {
                            servedStop = control;
                            stopTimer = 0f;
                            controlRequiresStop = false;
                        }
                    }
                    else
                    {
                        stopTimer = 0f;
                    }
                }
                else
                {
                    stopTimer = 0f;
                    if (control.LightState == TrafficLightState.Red)
                    {
                        controlRequiresStop = true;
                    }
                    else if (control.LightState == TrafficLightState.Yellow)
                    {
                        float stoppingDistance = speed * speed / (2f * Mathf.Max(0.1f, braking));
                        bool alreadyNearlyStopped = speed < 0.5f;
                        bool enoughRoomToStop = controlDistance > stoppingDistance + stopLineBuffer + yellowSafetyMargin;
                        controlRequiresStop = alreadyNearlyStopped || enoughRoomToStop;
                    }
                }
            }
            else
            {
                stopTimer = 0f;
            }

            if (servedStop != null && Vector3.Distance(transform.position, servedStop.transform.position) > 10f)
                servedStop = null;

            // These reasons always keep their priority. Serving a STOP must never cancel
            // a pedestrian, obstacle or lead-vehicle stop request.
            bool hardBlocked = followingBlocked || obstacleBlocked || pedestrianBlocked;
            bool intersectionBlocked = false;

            // Reserve an intersection only when every earlier rule already allows movement.
            // This avoids a car owning the junction while it is waiting at a red light or STOP.
            if (!hardBlocked && !controlRequiresStop)
            {
                intersectionBlocked = !controller.CanProceedIntersection(
                    this, transform.position, forward, intersectionLookAhead);
            }

            float desiredSpeed = cruiseSpeed;
            if (hardBlocked || intersectionBlocked)
                desiredSpeed = 0f;

            if (controlRequiresStop)
            {
                // Brake progressively so the car reaches zero close to the authored stop line
                // instead of stopping several metres too early.
                float remaining = Mathf.Max(0f, controlDistance - stopLineBuffer);
                float approachSpeed = Mathf.Sqrt(2f * Mathf.Max(0.1f, braking) * remaining);
                desiredSpeed = Mathf.Min(desiredSpeed, approachSpeed);
            }

            float rate = desiredSpeed < speed ? braking : acceleration;
            speed = Mathf.MoveTowards(speed, desiredSpeed, rate * Time.deltaTime);

            if (forward.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation,
                    Quaternion.LookRotation(forward, Vector3.up),
                    turnSpeed * Time.deltaTime);

            float travel = Mathf.Min(speed * Time.deltaTime, planar.magnitude);
            if (controlRequiresStop)
            {
                float allowedBeforeLine = Mathf.Max(0f, controlDistance - stopLineBuffer);
                travel = Mathf.Min(travel, allowedBeforeLine);
            }

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
            if (forward.sqrMagnitude < 0.001f)
                return false;

            Vector3 origin = transform.position + forward * 2.1f + Vector3.up * 0.85f;
            int count = Physics.SphereCastNonAlloc(
                origin,
                0.45f,
                forward,
                obstacleHits,
                Mathf.Max(0.5f, safetyDistance - 2.1f),
                ~0,
                QueryTriggerInteraction.Ignore);

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

        private void OnDisable()
        {
            if (controller != null)
                controller.ReleaseIntersectionReservations(this);
            speed = 0f;
            stopTimer = 0f;
        }

        private void SetPosition(Vector3 position)
        {
            if (cachedBody != null)
                cachedBody.position = position;
            transform.position = position;
        }
    }
}
