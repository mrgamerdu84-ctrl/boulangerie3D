using System.Collections.Generic;
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
        [SerializeField, Range(0.4f, 3f)] private float stopLineBuffer = 0.75f;
        [SerializeField, Min(0f)] private float stopSafetyMargin = 0.5f;
        [SerializeField, Range(0.1f, 1f)] private float yellowSafetyMargin = 0.35f;
        [SerializeField, Min(2f)] private float intersectionLookAhead = 5f;

        [Header("Traffic debug")]
        [SerializeField] private bool showTrafficDebugGizmos = true;
        [SerializeField] private Color brakingDistanceColor = new Color(1f, 0.55f, 0.05f, 0.9f);
        [SerializeField] private Color acceptedLightColor = new Color(0.1f, 0.9f, 1f, 0.9f);

        [Header("Road graph routing")]
        [SerializeField, Min(1f)] private float routeDecisionDistance = 12f;

        private MobileTrafficController controller;
        private TrafficRoutePath route;
        private int targetIndex;
        private float speed;
        private TrafficControlPoint servedStop;
        private TrafficControlPoint heldTrafficLight;
        private TrafficControlPoint committedTrafficLight;
        private float stopTimer;
        private Rigidbody cachedBody;
        private Renderer[] vehicleRenderers;
        private readonly RaycastHit[] obstacleHits = new RaycastHit[12];
        private Vector3 previousPosition;
        private float distanceTravelled;
        private float stationaryDuration;
        private float longestStationaryDuration;
        private int reportedDirectionErrorIndex = -1;
        private float debugBrakingDistance;
        private float debugRequiredDetectionDistance;
        private TrafficControlPoint debugAcceptedTrafficLight;
        private TrafficRoadSegment currentRoadSegment;
        private TrafficRoadSegment nextRoadSegment;
        private TrafficRoadSegment lastSelectedRoadSegment;
        private TrafficTurnDirection nextTurnDirection;
        private readonly List<TrafficRoadSegment> availableRoadExits =
            new List<TrafficRoadSegment>(8);
        private System.Random routingRandom;
        private TrafficRuntimeRoadGraph runtimeRoadGraph;
        private TrafficRuntimeRoadSegment currentRuntimeSegment;
        private TrafficRuntimeRoadSegment nextRuntimeSegment;
        private TrafficRuntimeTurnConnector preparedRuntimeConnector;
        private TrafficRuntimeTurnConnector activeRuntimeConnector;
        private int runtimeConnectorPointIndex;
        private readonly List<TrafficRuntimeTurnConnector> availableRuntimeConnectors =
            new List<TrafficRuntimeTurnConnector>(8);
        private readonly Queue<TrafficRuntimeRoadSegment> recentRuntimeSegments =
            new Queue<TrafficRuntimeRoadSegment>(6);

        public TrafficRoutePath Route => route;
        public float CurrentSpeed => speed;
        public float DistanceTravelled => distanceTravelled;
        public float StationaryDuration => stationaryDuration;
        public float LongestStationaryDuration => longestStationaryDuration;
        public float BrakingDistance => debugBrakingDistance;
        public float RequiredTrafficDetectionDistance => debugRequiredDetectionDistance;
        public TrafficControlPoint AcceptedTrafficLight => debugAcceptedTrafficLight;
        public TrafficRoadSegment CurrentRoadSegment => currentRoadSegment;
        public TrafficRoadSegment NextRoadSegment => nextRoadSegment;
        public TrafficTurnDirection NextTurnDirection => nextTurnDirection;
        public IReadOnlyList<TrafficRoadSegment> AvailableRoadExits => availableRoadExits;
        public bool UsesRoadGraph => currentRuntimeSegment != null || currentRoadSegment != null;
        public string CurrentRuntimeSegment => currentRuntimeSegment != null ? currentRuntimeSegment.Id : string.Empty;
        public string NextRuntimeSegment => nextRuntimeSegment != null ? nextRuntimeSegment.Id : string.Empty;
        public IReadOnlyList<TrafficRuntimeTurnConnector> AvailableRuntimeConnectors =>
            availableRuntimeConnectors;
        public string ActiveRuntimeConnector => activeRuntimeConnector != null
            ? activeRuntimeConnector.Id : string.Empty;

        private void Awake()
        {
            cachedBody = GetComponent<Rigidbody>();
            cachedBody.isKinematic = true;
            cachedBody.useGravity = false;
            cachedBody.interpolation = RigidbodyInterpolation.None;
            vehicleRenderers = GetComponentsInChildren<Renderer>(true);
            routingRandom = new System.Random(GetStableRoutingSeed());

            if (braking <= 8.01f)
                braking = 10f;
            if (safetyDistance <= 7.01f)
                safetyDistance = 8f;

            // Old scenes used 0.75 m, which can leave the nose of a car on the stop line.
            // Keep a larger centre-to-line margin so vehicles visibly respect the light.
            if (stopLineBuffer < 1.6f)
                stopLineBuffer = 1.6f;
        }

        public void Initialize(MobileTrafficController owner, TrafficRoutePath assignedRoute, int startIndex)
        {
            if (controller != null)
                controller.ReleaseIntersectionReservations(this);

            controller = owner;
            ResetRoadGraphState();
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
            heldTrafficLight = null;
            committedTrafficLight = null;
            stopTimer = 0f;
            previousPosition = transform.position;
            distanceTravelled = 0f;
            stationaryDuration = 0f;
            longestStationaryDuration = 0f;
            reportedDirectionErrorIndex = -1;
        }

        public void InitializeGraph(
            MobileTrafficController owner,
            TrafficRoadSegment initialSegment,
            int startStep)
        {
            if (initialSegment == null || !initialSegment.IsValid)
            {
                Initialize(owner, initialSegment != null ? initialSegment.Route : null, 0);
                return;
            }

            if (controller != null)
                controller.ReleaseIntersectionReservations(this);

            controller = owner;
            ResetRoadGraphState();
            currentRoadSegment = initialSegment;
            route = initialSegment.Route;

            int lastTravelStep = Mathf.Max(0, initialSegment.PointCount - 2);
            int clampedStep = Mathf.Clamp(startStep, 0, lastTravelStep);
            SetPosition(initialSegment.GetPointAtStep(clampedStep));
            targetIndex = initialSegment.GetWaypointIndexAtStep(clampedStep + 1);

            Vector3 direction = route.GetPoint(targetIndex) - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);

            speed = 0f;
            servedStop = null;
            heldTrafficLight = null;
            committedTrafficLight = null;
            stopTimer = 0f;
            previousPosition = transform.position;
            distanceTravelled = 0f;
            stationaryDuration = 0f;
            longestStationaryDuration = 0f;
            reportedDirectionErrorIndex = -1;
        }

        public void InitializeRuntimeGraph(MobileTrafficController owner, TrafficRuntimeRoadGraph graph,
            TrafficRuntimeRoadSegment initialSegment, int startStep)
        {
            if (graph == null || initialSegment == null || initialSegment.PointCount < 2)
            {
                Initialize(owner, initialSegment != null ? initialSegment.Route : null, 0);
                return;
            }

            if (controller != null) controller.ReleaseIntersectionReservations(this);
            controller = owner;
            ResetRoadGraphState();
            runtimeRoadGraph = graph;
            currentRuntimeSegment = initialSegment;
            currentRuntimeSegment.UsageCount++;
            route = initialSegment.Route;
            int step = Mathf.Clamp(startStep, 0, Mathf.Max(0, initialSegment.PointCount - 2));
            SetPosition(initialSegment.GetPointAtStep(step));
            targetIndex = initialSegment.GetWaypointIndexAtStep(step + 1);
            Vector3 direction = route.GetPoint(targetIndex) - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            speed = 0f;
            servedStop = null;
            heldTrafficLight = null;
            committedTrafficLight = null;
            stopTimer = 0f;
            previousPosition = transform.position;
            distanceTravelled = 0f;
            stationaryDuration = 0f;
            longestStationaryDuration = 0f;
            reportedDirectionErrorIndex = -1;
        }

        private void Update()
        {
            if (controller == null || route == null || route.Count < 2)
                return;

            Vector3 target = activeRuntimeConnector != null
                ? activeRuntimeConnector.GetPoint(runtimeConnectorPointIndex)
                : route.GetPoint(targetIndex);
            Vector3 planar = target - transform.position;
            planar.y = 0f;
            if (activeRuntimeConnector == null && currentRuntimeSegment != null &&
                targetIndex == currentRuntimeSegment.EndWaypointIndex &&
                planar.magnitude <= routeDecisionDistance)
                PrepareNextRuntimeSegment();
            else if (currentRoadSegment != null &&
                targetIndex == currentRoadSegment.EndWaypointIndex &&
                planar.magnitude <= routeDecisionDistance)
                PrepareNextRoadSegment();

            if (planar.magnitude < 0.45f)
            {
                if (activeRuntimeConnector != null)
                {
                    runtimeConnectorPointIndex++;
                    if (runtimeConnectorPointIndex >= activeRuntimeConnector.PointCount)
                        FinishRuntimeConnector();
                }
                else if (currentRuntimeSegment != null &&
                    targetIndex == currentRuntimeSegment.EndWaypointIndex)
                {
                    if (!EnterPreparedRuntimeSegment())
                    {
                        speed = 0f;
                        stationaryDuration += Time.deltaTime;
                        longestStationaryDuration = Mathf.Max(longestStationaryDuration, stationaryDuration);
                        return;
                    }
                }
                else if (currentRoadSegment != null &&
                    targetIndex == currentRoadSegment.EndWaypointIndex)
                {
                    if (!EnterPreparedRoadSegment())
                    {
                        speed = 0f;
                        stationaryDuration += Time.deltaTime;
                        longestStationaryDuration = Mathf.Max(
                            longestStationaryDuration,
                            stationaryDuration);
                        return;
                    }
                }
                else
                {
                    targetIndex = (targetIndex + 1) % route.Count;
                }

                target = activeRuntimeConnector != null
                    ? activeRuntimeConnector.GetPoint(runtimeConnectorPointIndex)
                    : route.GetPoint(targetIndex);
                planar = target - transform.position;
                planar.y = 0f;

                if (currentRoadSegment != null && planar.magnitude < 0.45f &&
                    targetIndex == currentRoadSegment.StartWaypointIndex)
                {
                    targetIndex = (targetIndex + 1) % route.Count;
                    target = route.GetPoint(targetIndex);
                    planar = target - transform.position;
                    planar.y = 0f;
                }
            }

            Vector3 forward = planar.sqrMagnitude > 0.001f ? planar.normalized : transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 0.001f)
                forward.Normalize();

            float frontClearance = GetFrontClearance(forward);
            float stoppingDistance = CalculateBrakingDistance(speed, braking);
            float reactionDistance = Mathf.Max(1f, speed * 0.5f);
            float requiredDetectionDistance = stoppingDistance + frontClearance +
                stopSafetyMargin + reactionDistance;
            debugBrakingDistance = stoppingDistance;
            debugRequiredDetectionDistance = requiredDetectionDistance;

            Vector3 actualTravelDirection = transform.position - previousPosition;
            actualTravelDirection.y = 0f;
            if (activeRuntimeConnector == null && actualTravelDirection.sqrMagnitude > 0.0025f &&
                !route.IsNextDirectionCoherent(
                    transform.position,
                    actualTravelDirection,
                    targetIndex,
                    out float waypointAlignment))
            {
                if (reportedDirectionErrorIndex != targetIndex)
                {
                    Debug.LogWarning(
                        $"[TrafficRouteValidation] Véhicule '{name}' : direction incohérente vers " +
                        $"{route.DescribeWaypoint(targetIndex)} sur la route '{route.name}' " +
                        $"(alignement {waypointAlignment:F2}).",
                        route);
                    reportedDirectionErrorIndex = targetIndex;
                }
            }
            else
            {
                reportedDirectionErrorIndex = -1;
            }

            float leadDistance = controller.GetLeadVehicleDistance(this, forward, safetyDistance);
            bool followingBlocked = leadDistance < safetyDistance;
            bool obstacleBlocked = HasObstacleAhead(forward);
            bool pedestrianBlocked = controller.CrosswalkRequiresStop(transform.position, forward, 9f);

            TrafficControlPoint detectedControl = controller.FindBlockingControl(
                this,
                transform.position,
                forward,
                requiredDetectionDistance);

            // Keep tracking the non-green signal for this approach. This avoids one-frame
            // losses when waypoint steering slightly changes the vehicle direction.
            if (detectedControl != null && detectedControl.Kind == TrafficControlKind.TrafficLight)
            {
                float detectedAhead = detectedControl.DistanceAhead(transform.position, forward);
                if (detectedAhead >= -0.2f && !detectedControl.IsGreen)
                    heldTrafficLight = detectedControl;
            }

            if (heldTrafficLight != null)
            {
                float heldAhead = heldTrafficLight.DistanceAhead(transform.position, forward);
                bool stillAffectsLane = heldTrafficLight.TryAffect(
                    transform.position,
                    forward,
                    requiredDetectionDistance,
                    out _);
                // A light is associated with a single approach.  As soon as the route
                // changes direction through the junction it cannot keep braking this car.
                if (heldTrafficLight.IsGreen || heldAhead < -0.9f || !stillAffectsLane)
                {
                    if (committedTrafficLight == heldTrafficLight && heldAhead < -0.9f)
                        committedTrafficLight = null;
                    heldTrafficLight = null;
                }
                else
                {
                    detectedControl = heldTrafficLight;
                }
            }

            TrafficControlPoint control = detectedControl;
            debugAcceptedTrafficLight = control;
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
                    bool atStopLine = controlDistance <= frontClearance + 0.25f;
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

                    // Once the car has legally committed at yellow because there was no safe
                    // stopping distance, let it clear the line even if the phase becomes red.
                    // Without this latch a car can brake abruptly halfway through its decision.
                    bool committedToThisLight = committedTrafficLight == control;
                    if (!committedToThisLight)
                    {
                        if (control.LightState == TrafficLightState.Red)
                        {
                            controlRequiresStop = true;
                        }
                        else if (control.LightState == TrafficLightState.Yellow)
                        {
                            bool alreadyNearlyStopped = speed < 0.5f;
                            float availableToFront = controlDistance - frontClearance - stopSafetyMargin;
                            bool enoughRoomToStop = availableToFront >=
                                stoppingDistance + yellowSafetyMargin;

                            controlRequiresStop = alreadyNearlyStopped || enoughRoomToStop;
                            if (!controlRequiresStop)
                                committedTrafficLight = control;
                        }
                    }
                }
            }
            else
            {
                stopTimer = 0f;
            }

            if (servedStop != null && Vector3.Distance(transform.position, servedStop.transform.position) > 10f)
                servedStop = null;

            bool hardBlocked = followingBlocked || obstacleBlocked || pedestrianBlocked;

            // If a committed vehicle is forced to stop before the line (pedestrian, obstacle,
            // or queue), cancel the yellow commitment. On the next frame a red light will be
            // obeyed normally instead of allowing a delayed entry into the junction.
            if (hardBlocked && committedTrafficLight != null)
            {
                float committedAhead = committedTrafficLight.DistanceAhead(transform.position, forward);
                if (committedAhead > frontClearance)
                    committedTrafficLight = null;
            }

            bool intersectionBlocked = false;
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
                // Brake to the actual stop line, with enough centre-to-line clearance that
                // the front of the vehicle stays visibly behind the crossing.
                float remaining = Mathf.Max(
                    0f,
                    controlDistance - frontClearance - stopSafetyMargin);
                float approachSpeed = Mathf.Sqrt(2f * Mathf.Max(0.1f, braking) * remaining);
                desiredSpeed = Mathf.Min(desiredSpeed, approachSpeed);

                if (remaining <= 0.08f)
                    desiredSpeed = 0f;
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
                float allowedBeforeLine = Mathf.Max(
                    0f,
                    controlDistance - frontClearance - stopSafetyMargin);
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

        private float GetFrontClearance(Vector3 travelDirection)
        {
            if (vehicleRenderers == null || vehicleRenderers.Length == 0 ||
                travelDirection.sqrMagnitude < 0.001f)
                return stopLineBuffer;

            travelDirection.Normalize();
            Bounds combined = new Bounds();
            bool found = false;
            for (int i = 0; i < vehicleRenderers.Length; i++)
            {
                Renderer renderer = vehicleRenderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                if (!found)
                {
                    combined = renderer.bounds;
                    found = true;
                }
                else
                {
                    combined.Encapsulate(renderer.bounds);
                }
            }

            if (!found)
                return stopLineBuffer;

            Vector3 extents = combined.extents;
            float projectedExtent = Mathf.Abs(travelDirection.x) * extents.x +
                                    Mathf.Abs(travelDirection.z) * extents.z;
            float pivotOffset = Vector3.Dot(combined.center - transform.position, travelDirection);

            // Keep the complete front bumper behind the stop line, plus a small
            // visual safety gap. The serialized buffer remains the minimum fallback.
            return Mathf.Max(stopLineBuffer, pivotOffset + projectedExtent + 0.35f);
        }

        public static float CalculateBrakingDistance(float currentSpeed, float brakingRate)
        {
            float safeBraking = Mathf.Max(0.1f, brakingRate);
            float safeSpeed = Mathf.Max(0f, currentSpeed);
            return safeSpeed * safeSpeed / (2f * safeBraking);
        }

        private void PrepareNextRoadSegment()
        {
            if (currentRoadSegment == null || nextRoadSegment != null ||
                currentRoadSegment.EndNode == null)
                return;

            nextRoadSegment = currentRoadSegment.EndNode.ChooseNextSegment(
                currentRoadSegment,
                lastSelectedRoadSegment,
                routingRandom,
                availableRoadExits,
                out nextTurnDirection);
        }

        private void PrepareNextRuntimeSegment()
        {
            if (currentRuntimeSegment == null || preparedRuntimeConnector != null ||
                runtimeRoadGraph == null)
                return;
            preparedRuntimeConnector = runtimeRoadGraph.ChooseNext(currentRuntimeSegment,
                recentRuntimeSegments, routingRandom, availableRuntimeConnectors, out nextTurnDirection);
            nextRuntimeSegment = preparedRuntimeConnector != null
                ? preparedRuntimeConnector.Outgoing : null;
        }

        private bool EnterPreparedRuntimeSegment()
        {
            PrepareNextRuntimeSegment();
            if (preparedRuntimeConnector == null || nextRuntimeSegment == null ||
                nextRuntimeSegment.PointCount < 2) return false;
            recentRuntimeSegments.Enqueue(currentRuntimeSegment);
            while (recentRuntimeSegments.Count > 6) recentRuntimeSegments.Dequeue();
            activeRuntimeConnector = preparedRuntimeConnector;
            preparedRuntimeConnector = null;
            runtimeConnectorPointIndex = 1;
            return true;
        }

        private void FinishRuntimeConnector()
        {
            currentRuntimeSegment = nextRuntimeSegment;
            nextRuntimeSegment = null;
            activeRuntimeConnector = null;
            runtimeConnectorPointIndex = 0;
            availableRuntimeConnectors.Clear();
            route = currentRuntimeSegment.Route;
            targetIndex = currentRuntimeSegment.GetWaypointIndexAtStep(1);
            nextTurnDirection = TrafficTurnDirection.None;
        }

        private bool EnterPreparedRoadSegment()
        {
            PrepareNextRoadSegment();
            if (nextRoadSegment == null || !nextRoadSegment.IsValid)
                return false;

            currentRoadSegment = nextRoadSegment;
            lastSelectedRoadSegment = nextRoadSegment;
            nextRoadSegment = null;
            nextTurnDirection = TrafficTurnDirection.None;
            availableRoadExits.Clear();
            route = currentRoadSegment.Route;
            targetIndex = currentRoadSegment.StartWaypointIndex;
            return true;
        }

        private void ResetRoadGraphState()
        {
            runtimeRoadGraph = null;
            currentRuntimeSegment = null;
            nextRuntimeSegment = null;
            preparedRuntimeConnector = null;
            activeRuntimeConnector = null;
            runtimeConnectorPointIndex = 0;
            availableRuntimeConnectors.Clear();
            recentRuntimeSegments.Clear();
            currentRoadSegment = null;
            nextRoadSegment = null;
            lastSelectedRoadSegment = null;
            nextTurnDirection = TrafficTurnDirection.None;
            availableRoadExits.Clear();
        }

        private int GetStableRoutingSeed()
        {
            unchecked
            {
                int hash = 17;
                string value = name ?? string.Empty;
                for (int i = 0; i < value.Length; i++)
                    hash = hash * 31 + value[i];
                return hash;
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!showTrafficDebugGizmos)
                return;

            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                return;
            forward.Normalize();

            Gizmos.color = brakingDistanceColor;
            Vector3 brakingEnd = transform.position + forward * debugBrakingDistance;
            Gizmos.DrawLine(transform.position, brakingEnd);
            Gizmos.DrawWireSphere(brakingEnd, 0.2f);

            if (debugAcceptedTrafficLight != null)
            {
                Gizmos.color = acceptedLightColor;
                Vector3 stop = debugAcceptedTrafficLight.LogicalStopLinePosition;
                Gizmos.DrawLine(transform.position + Vector3.up * 0.25f, stop + Vector3.up * 0.25f);
                Gizmos.DrawWireSphere(stop, 0.28f);
            }
        }

        private void OnDisable()
        {
            if (controller != null)
                controller.ReleaseIntersectionReservations(this);
            speed = 0f;
            stopTimer = 0f;
            heldTrafficLight = null;
            committedTrafficLight = null;
            reportedDirectionErrorIndex = -1;
            debugAcceptedTrafficLight = null;
            debugBrakingDistance = 0f;
            debugRequiredDetectionDistance = 0f;
            ResetRoadGraphState();
        }

        private void SetPosition(Vector3 position)
        {
            if (cachedBody != null)
                cachedBody.position = position;
            transform.position = position;
        }
    }
}
