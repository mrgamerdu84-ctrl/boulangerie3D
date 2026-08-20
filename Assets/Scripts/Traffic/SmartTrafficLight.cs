using System;
using System.Linq;
using UnityEngine;

namespace Boulangerie3D.Traffic
{
    [DisallowMultipleComponent]
    public sealed class SmartTrafficLight : TrafficControlPoint
    {
        [Header("Smart traffic light debug")]
        [SerializeField] private bool showDebugGizmos = true;
        [SerializeField] private Color controlledZoneColor = new Color(0.1f, 0.8f, 1f, 0.75f);
        [SerializeField, Min(1f)] private float controlledZoneLength = 8f;

        public bool ShowDebugGizmos => showDebugGizmos;

        protected override void Awake()
        {
            base.Awake();
            Configure(TrafficControlKind.TrafficLight, DetectionDistance, LaneTolerance);
            RefreshAutomaticBinding();
        }

        private void Reset()
        {
            Configure(TrafficControlKind.TrafficLight, DetectionDistance, LaneTolerance);
            RefreshAutomaticBinding();
        }

        private void OnValidate()
        {
            Configure(TrafficControlKind.TrafficLight, DetectionDistance, LaneTolerance);
        }

        [ContextMenu("Refresh automatic traffic binding")]
        public void RefreshAutomaticBinding()
        {
            BoxCollider nearest = FindNearestJunctionCollider();
            if (nearest == null)
                return;

            CrosswalkPriorityZone[] crosswalks =
                FindObjectsByType<CrosswalkPriorityZone>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Where(zone => zone != null && zone.gameObject.scene == gameObject.scene)
                    .ToArray();

            BindToIntersectionFromOrientation(nearest.bounds, nearest.name, crosswalks);
        }

        private BoxCollider FindNearestJunctionCollider()
        {
            BoxCollider[] colliders =
                FindObjectsByType<BoxCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            BoxCollider nearest = null;
            float nearestSqr = float.MaxValue;
            Vector3 position = transform.position;
            position.y = 0f;

            for (int i = 0; i < colliders.Length; i++)
            {
                BoxCollider candidate = colliders[i];
                if (candidate == null || candidate.gameObject.scene != gameObject.scene ||
                    !candidate.name.StartsWith("JunctionCollider_", StringComparison.Ordinal))
                    continue;

                Vector3 center = candidate.bounds.center;
                center.y = 0f;
                float sqr = (center - position).sqrMagnitude;
                if (sqr >= nearestSqr)
                    continue;

                nearestSqr = sqr;
                nearest = candidate;
            }

            return nearest;
        }

        private void OnDrawGizmosSelected()
        {
            if (!showDebugGizmos)
                return;

            RefreshAutomaticBinding();
            if (!HasRuntimeIntersection)
                return;

            Vector3 stop = LogicalStopLinePosition;
            Vector3 approach = DetectedAxis == TrafficLightAxis.X ? Vector3.right : Vector3.forward;
            Vector3 lateral = DetectedAxis == TrafficLightAxis.X ? Vector3.forward : Vector3.right;
            int approachSign = DetectedDirection.StartsWith("-") ? -1 : 1;
            Vector3 towardJunction = -approach * approachSign;

            Gizmos.color = controlledZoneColor;
            Gizmos.DrawLine(stop - lateral * LaneTolerance, stop + lateral * LaneTolerance);
            Gizmos.DrawWireCube(
                stop - towardJunction * (controlledZoneLength * 0.5f),
                lateral * (LaneTolerance * 2f) + approach * controlledZoneLength + Vector3.up * 0.15f);
            Gizmos.DrawLine(stop, stop + towardJunction * 2f);
            Gizmos.DrawSphere(stop, 0.18f);
        }
    }
}
