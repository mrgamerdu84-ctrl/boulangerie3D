using System.Collections.Generic;
using UnityEngine;

namespace Boulangerie3D.Traffic
{
    [RequireComponent(typeof(BoxCollider))]
    public sealed class CrosswalkPriorityZone : MonoBehaviour
    {
        private readonly HashSet<PedestrianAgent> occupants = new HashSet<PedestrianAgent>();
        private BoxCollider cachedCollider;

        public bool HasPedestrian => occupants.Count > 0;
        public Bounds Bounds => cachedCollider.bounds;

        public bool HasPedestrianNear(Vector3 position, float horizontalDistance)
        {
            float distanceSquared = horizontalDistance * horizontalDistance;
            foreach (PedestrianAgent pedestrian in occupants)
            {
                if (pedestrian == null || !pedestrian.isActiveAndEnabled)
                    continue;

                Vector3 offset = pedestrian.transform.position - position;
                offset.y = 0f;
                if (offset.sqrMagnitude <= distanceSquared)
                    return true;
            }
            return false;
        }

        private void Awake()
        {
            cachedCollider = GetComponent<BoxCollider>();
            cachedCollider.isTrigger = true;
        }

        public void Enter(PedestrianAgent pedestrian)
        {
            if (pedestrian != null)
                occupants.Add(pedestrian);
        }

        public void Exit(PedestrianAgent pedestrian)
        {
            if (pedestrian != null)
                occupants.Remove(pedestrian);
        }

        private void OnDisable() => occupants.Clear();
    }
}
