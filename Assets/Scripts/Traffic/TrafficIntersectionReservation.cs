using UnityEngine;

namespace Boulangerie3D.Traffic
{
    [RequireComponent(typeof(BoxCollider))]
    public sealed class TrafficIntersectionReservation : MonoBehaviour
    {
        private TrafficVehicleAgent owner;
        private bool ownerEntered;
        private BoxCollider box;
        public Bounds Bounds => box.bounds;
        private void Awake(){ box=GetComponent<BoxCollider>(); box.isTrigger=true; }
        public bool TryReserve(TrafficVehicleAgent vehicle){ if(owner==null||owner==vehicle){owner=vehicle;return true;} return false; }
        public void UpdateOwner(TrafficVehicleAgent vehicle){ if(owner!=vehicle)return; if(Bounds.Contains(vehicle.transform.position))ownerEntered=true; else if(ownerEntered){owner=null;ownerEntered=false;} }
    }
}
