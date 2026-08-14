using UnityEngine;

namespace Boulangerie3D.Traffic
{
    /// <summary>
    /// Installs pose repair on every pedestrian, including pedestrians prepared at runtime.
    /// The scanner is intentionally tiny and runs only a few times per second.
    /// </summary>
    internal static class PedestrianPoseRepairBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Object.FindFirstObjectByType<PedestrianPoseRepairSystem>() != null)
                return;

            GameObject host = new GameObject("Pedestrian Pose Repair System");
            host.hideFlags = HideFlags.HideInHierarchy;
            Object.DontDestroyOnLoad(host);
            host.AddComponent<PedestrianPoseRepairSystem>();
        }
    }

    [DefaultExecutionOrder(900)]
    internal sealed class PedestrianPoseRepairSystem : MonoBehaviour
    {
        private const float ScanInterval = 0.35f;
        private float nextScanTime;

        private void OnEnable()
        {
            Scan();
        }

        private void Update()
        {
            if (Time.unscaledTime < nextScanTime)
                return;

            Scan();
        }

        private void Scan()
        {
            nextScanTime = Time.unscaledTime + ScanInterval;
            PedestrianAgent[] agents = FindObjectsByType<PedestrianAgent>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            for (int i = 0; i < agents.Length; i++)
            {
                PedestrianAgent agent = agents[i];
                if (agent == null || agent.GetComponent<PedestrianPoseRepair>() != null)
                    continue;

                agent.gameObject.AddComponent<PedestrianPoseRepair>();
            }
        }
    }

    /// <summary>
    /// Keeps the traffic-character root upright and adapts the root height to the
    /// actual rendered feet instead of relying on one hard-coded offset for every model.
    /// Runs after PedestrianAgent so the final visible pose is stable.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class PedestrianPoseRepair : MonoBehaviour
    {
        [Header("Ground alignment")]
        [SerializeField, Range(0f, 0.05f)] private float soleClearance = 0.008f;
        [SerializeField, Range(0.2f, 1.2f)] private float maximumGroundCorrection = 0.5f;
        [SerializeField, Range(0.3f, 1.5f)] private float groundProbeTolerance = 0.7f;
        [SerializeField, Range(0.2f, 1f)] private float minimumWalkableNormalY = 0.55f;

        private readonly RaycastHit[] groundHits = new RaycastHit[12];
        private Rigidbody body;
        private Animator animator;
        private Transform[] directChildren;
        private Vector3[] authoredChildPositions;
        private float rootToFeetOffset = 0.152f;
        private bool hasReliableFeetBounds;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            animator = GetComponentInChildren<Animator>(true);

            if (body != null)
            {
                body.isKinematic = true;
                body.useGravity = false;
                body.constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            }

            CaptureAuthoredChildPositions();
            RecalculateFeetOffset();
            ForceUpright();
        }

        private void OnEnable()
        {
            RecalculateFeetOffset();
            ForceUpright();
        }

        private void LateUpdate()
        {
            ForceUpright();
            StabilizeFeetOnGround();

            // PedestrianAgent contains a tiny fallback bob for static characters.
            // A real Animator already supplies locomotion, so restoring its authored
            // root position avoids stacking that fake bob on top of the animation.
            if (animator != null && animator.enabled && animator.runtimeAnimatorController != null)
                RestoreAnimatedChildRoots();
        }

        private void CaptureAuthoredChildPositions()
        {
            int count = transform.childCount;
            directChildren = new Transform[count];
            authoredChildPositions = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                directChildren[i] = transform.GetChild(i);
                authoredChildPositions[i] = directChildren[i].localPosition;
            }
        }

        private void RecalculateFeetOffset()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
            {
                hasReliableFeetBounds = false;
                rootToFeetOffset = 0.152f;
                return;
            }

            float lowest = float.PositiveInfinity;
            bool foundSkinned = false;

            // Prefer skinned meshes: UI meshes, speech bubbles and helper renderers
            // must not influence where the character's feet are considered to be.
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !(renderer is SkinnedMeshRenderer))
                    continue;

                lowest = Mathf.Min(lowest, renderer.bounds.min.y);
                foundSkinned = true;
            }

            if (!foundSkinned)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null || !renderer.enabled)
                        continue;

                    lowest = Mathf.Min(lowest, renderer.bounds.min.y);
                }
            }

            if (float.IsPositiveInfinity(lowest))
            {
                hasReliableFeetBounds = false;
                rootToFeetOffset = 0.152f;
                return;
            }

            float measured = transform.position.y - lowest;
            if (float.IsNaN(measured) || float.IsInfinity(measured) || Mathf.Abs(measured) > 2.5f)
            {
                hasReliableFeetBounds = false;
                rootToFeetOffset = 0.152f;
                return;
            }

            rootToFeetOffset = Mathf.Clamp(measured, -0.5f, 2.5f);
            hasReliableFeetBounds = true;
        }

        private void ForceUpright()
        {
            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;

            Quaternion upright = Quaternion.LookRotation(forward.normalized, Vector3.up);
            if (Quaternion.Angle(transform.rotation, upright) > 0.01f)
                transform.rotation = upright;

            if (body != null && body.isKinematic)
                body.rotation = upright;
        }

        private void StabilizeFeetOnGround()
        {
            if (!hasReliableFeetBounds)
                return;

            Vector3 current = transform.position;
            float expectedGroundY = current.y - rootToFeetOffset;
            Vector3 origin = new Vector3(current.x, expectedGroundY + 1.25f, current.z);
            int hitCount = Physics.RaycastNonAlloc(
                origin,
                Vector3.down,
                groundHits,
                2.5f,
                ~0,
                QueryTriggerInteraction.Ignore);

            bool found = false;
            float bestGroundY = 0f;
            float bestDelta = float.PositiveInfinity;

            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hitInfo = groundHits[i];
                Collider hit = hitInfo.collider;
                if (hit == null || hit.transform.IsChildOf(transform))
                    continue;
                if (hit.GetComponentInParent<PedestrianAgent>() != null ||
                    hit.GetComponentInParent<TrafficVehicleAgent>() != null)
                    continue;
                if (hitInfo.normal.y < minimumWalkableNormalY)
                    continue;

                float delta = Mathf.Abs(hitInfo.point.y - expectedGroundY);
                if (delta > groundProbeTolerance || delta >= bestDelta)
                    continue;

                bestDelta = delta;
                bestGroundY = hitInfo.point.y;
                found = true;
            }

            if (!found)
                return;

            float desiredRootY = bestGroundY + rootToFeetOffset + soleClearance;
            float correction = desiredRootY - current.y;
            if (Mathf.Abs(correction) > maximumGroundCorrection)
                return;

            // Ignore microscopic corrections so the model does not shimmer vertically.
            if (Mathf.Abs(correction) < 0.001f)
                return;

            current.y = desiredRootY;
            transform.position = current;
            if (body != null && body.isKinematic)
                body.position = current;
        }

        private void RestoreAnimatedChildRoots()
        {
            if (directChildren == null || authoredChildPositions == null)
                return;

            int count = Mathf.Min(directChildren.Length, authoredChildPositions.Length);
            for (int i = 0; i < count; i++)
            {
                Transform child = directChildren[i];
                if (child == null)
                    continue;

                // Only undo the tiny vertical offset injected by PedestrianAgent.
                // X/Z are deliberately preserved in case another system moves a helper.
                Vector3 local = child.localPosition;
                local.y = authoredChildPositions[i].y;
                child.localPosition = local;
            }
        }
    }
}
