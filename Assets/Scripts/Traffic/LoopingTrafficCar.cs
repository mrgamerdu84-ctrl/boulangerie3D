using UnityEngine;

namespace Boulangerie3D.Traffic
{
    [DisallowMultipleComponent]
    public sealed class LoopingTrafficCar : MonoBehaviour
    {
        [SerializeField] private float speed = 4.5f;
        [SerializeField] private float minimumX = -36f;
        [SerializeField] private float maximumX = 36f;
        [SerializeField] private int direction = 1;

        private void Update()
        {
            transform.position += Vector3.right * (direction * speed * Time.deltaTime);
            Vector3 position = transform.position;
            if (direction > 0 && position.x > maximumX) position.x = minimumX;
            else if (direction < 0 && position.x < minimumX) position.x = maximumX;
            transform.position = position;
        }

        public void Configure(float movementSpeed, int travelDirection)
        {
            speed = Mathf.Max(0.1f, movementSpeed);
            direction = travelDirection < 0 ? -1 : 1;
        }
    }
}