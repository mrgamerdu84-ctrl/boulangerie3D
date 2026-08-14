using UnityEngine;

namespace Boulangerie3D.Traffic
{
    public enum PedestrianRole
    {
        Normal,
        BakeryCustomer
    }

    public enum PedestrianTravelDirection
    {
        Forward,
        Reverse
    }

    public sealed class PedestrianAgent : MonoBehaviour
    {
        [Header("Authored route")]
        [SerializeField] private PedestrianRole role;
        [SerializeField] private TrafficRoutePath initialRoute;
        [SerializeField, Min(0)] private int initialWaypointIndex;
        [SerializeField] private PedestrianTravelDirection initialDirection;

        [Header("Movement")]
        [SerializeField, Min(0.5f)] private float walkSpeed = 1.25f;
        [SerializeField, Min(1f)] private float turnSpeed = 8f;
        [SerializeField, Min(0.1f)] private float obstacleRadius = 0.3f;
        [SerializeField, Min(0.2f)] private float obstacleLookAhead = 0.8f;
        [SerializeField, Range(0f, 0.05f)] private float walkBobHeight = 0.018f;
        [SerializeField, Range(1f, 12f)] private float walkBobFrequency = 7f;
        [SerializeField, Min(0.5f)] private float stuckRecoveryDelay = 1.5f;
        [SerializeField, Min(0.5f)] private float pedestrianAwarenessDistance = 1.8f;
        [SerializeField, Range(0.2f, 1f)] private float passingOffset = 0.65f;
        [SerializeField, Range(0.1f, 1f)] private float minimumFollowingDistance = 0.55f;
        [SerializeField, Range(0.55f, 1.2f)] private float personalSpaceRadius = 0.82f;

        private MobileTrafficController controller;
        private TrafficRoutePath route;
        private int targetIndex;
        private CrosswalkPriorityZone activeCrosswalk;
        private readonly RaycastHit[] obstacleHits = new RaycastHit[10];
        private readonly RaycastHit[] groundHits = new RaycastHit[8];
        private Vector3 previousPosition;
        private float distanceTravelled;
        private float stationaryDuration;
        private float longestStationaryDuration;
        private Rigidbody cachedBody;
        private Animator cachedAnimator;
        private Transform[] visualRoots;
        private Vector3[] visualRootPositions;
        private float walkPhase;
        private int routeStep = 1;
        private PedestrianRouteConnector targetConnector;
        private PedestrianRouteConnector activePassageReservation;
        private bool avoidingHeadOn;
        private bool waitingForDoor;
        private int headOnAvoidanceCount;
        private int completedBakeryVisits;
        private float minimumPedestrianDistance = float.MaxValue;

        public PedestrianRole Role => role;
        public TrafficRoutePath Route => route;
        public float DistanceTravelled => distanceTravelled;
        public float StationaryDuration => stationaryDuration;
        public float LongestStationaryDuration => longestStationaryDuration;
        public bool IsWaitingForDoor => waitingForDoor;
        public int HeadOnAvoidanceCount => headOnAvoidanceCount;
        public int CompletedBakeryVisits => completedBakeryVisits;
        public float MinimumPedestrianDistance => minimumPedestrianDistance;

        private void Awake()
        {
            cachedBody = GetComponent<Rigidbody>();
            cachedAnimator = GetComponentInChildren<Animator>(true);
            if (cachedBody == null) return;
            cachedBody.isKinematic = true;
            cachedBody.useGravity = false;
            cachedBody.interpolation = RigidbodyInterpolation.None;
            visualRoots = new Transform[transform.childCount];
            visualRootPositions = new Vector3[transform.childCount];
            for (int i = 0; i < transform.childCount; i++)
            {
                visualRoots[i] = transform.GetChild(i);
                visualRootPositions[i] = visualRoots[i].localPosition;
            }
        }

        public void Initialize(MobileTrafficController owner, TrafficRoutePath assignedRoute, int startIndex)
        {
            if (owner == null || assignedRoute == null || !assignedRoute.IsValid)
            {
                controller = null;
                route = null;
                return;
            }
            controller = owner;
            route = initialRoute != null && initialRoute.IsValid ? initialRoute : assignedRoute;
            int authoredStart = initialRoute != null && initialRoute.IsValid ? initialWaypointIndex : startIndex;
            targetIndex = Mathf.Abs(authoredStart) % Mathf.Max(1, route.Count);
            SetPosition(route.GetPoint(targetIndex));
            routeStep = initialDirection == PedestrianTravelDirection.Reverse ? -1 : 1;
            targetIndex = WrapIndex(targetIndex + routeStep);
            RefreshTargetConnector();
            Vector3 direction = route.GetPoint(targetIndex) - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            activeCrosswalk = null;
            previousPosition = transform.position;
            distanceTravelled = 0f;
            stationaryDuration = 0f;
            longestStationaryDuration = 0f;
            waitingForDoor = false;
            avoidingHeadOn = false;
            headOnAvoidanceCount = 0;
            completedBakeryVisits = 0;
            minimumPedestrianDistance = float.MaxValue;
            activePassageReservation = null;
        }

        private void Update()
        {
            if (controller == null || route == null || route.Count < 2)
                return;

            Vector3 target = route.GetPoint(targetIndex);
            Vector3 planar = target - transform.position;
            planar.y = 0f;
            waitingForDoor = targetConnector != null &&
                             targetConnector.AppliesTo(route, role) &&
                             targetConnector.IsPassageOccupiedByAnother(this) &&
                             planar.magnitude <= targetConnector.WaitingDistance;

            if (planar.magnitude < 0.25f)
            {
                if (!TryUseTargetConnector())
                {
                    if (waitingForDoor)
                    {
                        RecordMovement(0f, false);
                        return;
                    }

                    targetIndex = WrapIndex(targetIndex + routeStep);
                    RefreshTargetConnector();
                }

                target = route.GetPoint(targetIndex);
                planar = target - transform.position;
                planar.y = 0f;
            }

            Vector3 direction = planar.sqrMagnitude > 0.001f ? planar.normalized : transform.forward;
            float speedScale;
            bool headOn;
            direction = GetPedestrianSteering(direction, out speedScale, out headOn);
            if (headOn && !avoidingHeadOn)
                headOnAvoidanceCount++;
            avoidingHeadOn = headOn;
            bool blocked = waitingForDoor || HasObstacleAhead(direction);

            if (!blocked)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(direction, Vector3.up), turnSpeed * Time.deltaTime);
                float travel = Mathf.Min(walkSpeed * speedScale * Time.deltaTime, planar.magnitude);
                Vector3 corrected = transform.position + direction * travel;
                corrected.y = target.y;
                SnapFeetToGround(ref corrected);
                SetPosition(corrected);
            }

            float movement = Vector3.Distance(transform.position, previousPosition);
            RecordMovement(movement, blocked);
            if (blocked && !waitingForDoor && stationaryDuration >= stuckRecoveryDelay)
            {
                routeStep = -routeStep;
                targetIndex = WrapIndex(targetIndex + routeStep);
                RefreshTargetConnector();
                stationaryDuration = 0f;
            }
            UpdateCrosswalkOccupancy();
        }

        private bool TryUseTargetConnector()
        {
            if (targetConnector == null || !targetConnector.AppliesTo(route, role))
                return false;

            if (!targetConnector.TryGetDestination(this, role, route, out TrafficRoutePath nextRoute, out int nextIndex))
            {
                waitingForDoor = targetConnector.IsPassageOccupiedByAnother(this);
                return false;
            }

            if (targetConnector.AcquiresPassage)
                activePassageReservation = targetConnector.PassageReservationOwner;
            if (targetConnector.ReleasesPassage)
            {
                activePassageReservation = null;
                completedBakeryVisits++;
            }

            route = nextRoute;
            targetIndex = nextIndex;
            if (Vector3.Distance(transform.position, route.GetPoint(targetIndex)) < 0.25f)
                targetIndex = WrapIndex(targetIndex + routeStep);
            RefreshTargetConnector();
            waitingForDoor = false;
            return true;
        }

        private void RefreshTargetConnector()
        {
            Transform waypoint = route == null ? null : route.GetWaypoint(targetIndex);
            targetConnector = waypoint == null ? null : waypoint.GetComponent<PedestrianRouteConnector>();
        }

        private Vector3 GetPedestrianSteering(Vector3 routeDirection, out float speedScale, out bool headOn)
        {
            speedScale = 1f;
            headOn = false;
            if (controller == null)
                return routeDirection;

            Vector3 steering = routeDirection;
            Vector3 right = Vector3.Cross(Vector3.up, routeDirection).normalized;
            PedestrianAgent[] pedestrians = controller.Pedestrians;
            for (int i = 0; i < pedestrians.Length; i++)
            {
                PedestrianAgent other = pedestrians[i];
                if (other == null || other == this || !other.isActiveAndEnabled)
                    continue;

                Vector3 delta = other.transform.position - transform.position;
                delta.y = 0f;
                float distance = delta.magnitude;
                minimumPedestrianDistance = Mathf.Min(minimumPedestrianDistance, distance);
                if (distance < 0.001f || distance > pedestrianAwarenessDistance)
                    continue;

                if (distance < personalSpaceRadius)
                {
                    Vector3 away = -delta / distance;
                    float forwardAmount = Vector3.Dot(away, routeDirection);
                    Vector3 lateralAway = away - routeDirection * forwardAmount * 0.35f;
                    if (lateralAway.sqrMagnitude > 0.001f)
                    {
                        float pressure = 1f - distance / personalSpaceRadius;
                        steering = (steering + lateralAway.normalized * (1.2f + pressure * 2.4f)).normalized;
                        speedScale = Mathf.Min(speedScale,
                            Mathf.Lerp(0.15f, 0.45f, distance / personalSpaceRadius));
                    }
                }

                float ahead = Vector3.Dot(delta, routeDirection);
                if (ahead <= 0f)
                    continue;

                float lateral = Mathf.Abs(Vector3.Dot(delta, right));
                bool facing = Vector3.Dot(routeDirection, other.transform.forward) < -0.25f;
                if (facing && lateral < passingOffset * 1.5f)
                {
                    float urgency = 1f - Mathf.Clamp01(distance / pedestrianAwarenessDistance);
                    steering = (steering + right * (0.45f + urgency) * passingOffset).normalized;
                    speedScale = Mathf.Min(speedScale, Mathf.Lerp(0.45f, 0.8f, distance / pedestrianAwarenessDistance));
                    headOn = true;
                }
                else if (!facing && lateral < minimumFollowingDistance && distance < 1.2f)
                {
                    speedScale = Mathf.Min(speedScale,
                        Mathf.InverseLerp(minimumFollowingDistance, 1.2f, distance));
                }
            }

            return steering;
        }

        private void RecordMovement(float movement, bool blocked)
        {
            distanceTravelled += movement;
            stationaryDuration = movement < 0.001f ? stationaryDuration + Time.deltaTime : 0f;
            longestStationaryDuration = Mathf.Max(longestStationaryDuration, stationaryDuration);
            previousPosition = transform.position;
            UpdateWalkVisual(movement);
            if (cachedAnimator != null && cachedAnimator.runtimeAnimatorController != null)
                cachedAnimator.SetBool("Moving", movement > 0.001f && !blocked);
        }

        private bool HasObstacleAhead(Vector3 direction)
        {
            Vector3 origin = transform.position + Vector3.up * 0.9f;
            int count = Physics.SphereCastNonAlloc(origin, obstacleRadius, direction, obstacleHits,
                obstacleLookAhead, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider hit = obstacleHits[i].collider;
                if (hit == null || hit.transform.IsChildOf(transform) ||
                    hit.GetComponentInParent<PedestrianAgent>() != null ||
                    hit.GetComponentInParent<CrosswalkPriorityZone>() != null)
                    continue;
                if (hit.bounds.max.y < origin.y - 0.35f)
                    continue;
                return true;
            }
            return false;
        }

        private void UpdateCrosswalkOccupancy()
        {
            CrosswalkPriorityZone containing = null;
            CrosswalkPriorityZone[] zones = controller.Crosswalks;
            Vector3 position = transform.position;
            for (int i = 0; i < zones.Length; i++)
            {
                if (zones[i] != null && zones[i].Bounds.Contains(position))
                {
                    containing = zones[i];
                    break;
                }
            }

            if (containing == activeCrosswalk)
                return;

            if (activeCrosswalk != null)
                activeCrosswalk.Exit(this);
            activeCrosswalk = containing;
            if (activeCrosswalk != null)
                activeCrosswalk.Enter(this);
        }

        private void OnDisable()
        {
            if (activePassageReservation != null)
                activePassageReservation.ReleasePassage(this);
            activePassageReservation = null;
            if (activeCrosswalk != null)
                activeCrosswalk.Exit(this);
            activeCrosswalk = null;
            if (cachedAnimator != null && cachedAnimator.runtimeAnimatorController != null)
                cachedAnimator.SetBool("Moving", false);
            RestoreWalkVisual();
        }

        private void SetPosition(Vector3 position)
        {
            if (cachedBody != null) cachedBody.position = position;
            transform.position = position;
        }

        private void SnapFeetToGround(ref Vector3 position)
        {
            const float footOffset = 0.152f;
            Vector3 origin = position + Vector3.up * 3f;
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, groundHits, 6f, ~0, QueryTriggerInteraction.Ignore);
            float highestGround = float.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                Collider hit = groundHits[i].collider;
                if (hit == null || hit.transform.IsChildOf(transform) ||
                    hit.GetComponentInParent<PedestrianAgent>() != null ||
                    hit.GetComponentInParent<TrafficVehicleAgent>() != null)
                    continue;
                highestGround = Mathf.Max(highestGround, groundHits[i].point.y);
            }
            if (!float.IsNegativeInfinity(highestGround))
                position.y = highestGround + footOffset;
        }

        private int WrapIndex(int index)
        {
            return ((index % route.Count) + route.Count) % route.Count;
        }

        private void UpdateWalkVisual(float movement)
        {
            if (visualRoots == null) return;
            if (movement > 0.001f) walkPhase += Time.deltaTime * walkBobFrequency;
            else walkPhase = Mathf.MoveTowards(walkPhase, 0f, Time.deltaTime * walkBobFrequency);
            float offset = movement > 0.001f ? Mathf.Abs(Mathf.Sin(walkPhase)) * walkBobHeight : 0f;
            for (int i = 0; i < visualRoots.Length; i++)
                if (visualRoots[i] != null) visualRoots[i].localPosition = visualRootPositions[i] + Vector3.up * offset;
        }

        private void RestoreWalkVisual()
        {
            if (visualRoots == null) return;
            for (int i = 0; i < visualRoots.Length; i++)
                if (visualRoots[i] != null) visualRoots[i].localPosition = visualRootPositions[i];
        }
    }
}
