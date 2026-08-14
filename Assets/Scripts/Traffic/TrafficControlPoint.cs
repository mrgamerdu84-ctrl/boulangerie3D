using UnityEngine;
using System.Collections.Generic;

namespace Boulangerie3D.Traffic
{
    public enum TrafficControlKind
    {
        Stop,
        TrafficLight
    }

    public enum TrafficLightState
    {
        Green,
        Yellow,
        Red
    }

    public sealed class TrafficControlPoint : MonoBehaviour
    {
        [SerializeField] private TrafficControlKind kind;
        [SerializeField, Min(1f)] private float detectionDistance = 7f;
        [SerializeField, Min(1f)] private float laneTolerance = 2.75f;
        [SerializeField, Min(2f)] private float greenDuration = 8f;
        [SerializeField, Min(0.5f)] private float yellowDuration = 2f;
        [SerializeField, Min(2f)] private float redDuration = 8f;
        [SerializeField, Range(0.5f, 3f)] private float stopHoldDuration = 1f;
        [SerializeField] private float phaseOffset;

        [Header("Visible traffic-light lamps")]
        [SerializeField] private Renderer[] redRenderers = new Renderer[0];
        [SerializeField] private Renderer[] yellowRenderers = new Renderer[0];
        [SerializeField] private Renderer[] greenRenderers = new Renderer[0];

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private MaterialPropertyBlock propertyBlock;
        private TrafficLightState lastVisualState;
        private bool visualStateInitialized;

        public TrafficControlKind Kind => kind;
        public float DetectionDistance => detectionDistance;
        public float StopHoldDuration => stopHoldDuration;

        private void Awake()
        {
            DiscoverLampRenderersIfNeeded();
            ApplyVisualState(true);
        }

        private void Update()
        {
            if (kind != TrafficControlKind.TrafficLight)
                return;

            TrafficLightState state = LightState;
            if (!visualStateInitialized || state != lastVisualState)
                ApplyVisualState(false);
        }

        public void Configure(TrafficControlKind controlKind, float detectDistance, float tolerance)
        {
            kind = controlKind;
            detectionDistance = Mathf.Max(1f, detectDistance);
            laneTolerance = Mathf.Max(1f, tolerance);
            DiscoverLampRenderersIfNeeded();
            ApplyVisualState(true);
        }

        public TrafficLightState LightState
        {
            get
            {
                if (kind != TrafficControlKind.TrafficLight)
                    return TrafficLightState.Green;

                // Preserve the original green+red cycle so existing phase offsets in the
                // authored Unity scene stay synchronised. Yellow uses the end of green.
                float effectiveYellow = Mathf.Min(yellowDuration, Mathf.Max(0.5f, greenDuration - 0.5f));
                float fullGreenEnd = Mathf.Max(0f, greenDuration - effectiveYellow);
                float cycle = greenDuration + redDuration;
                float phase = Mathf.Repeat(Time.time + phaseOffset, cycle);

                if (phase < fullGreenEnd)
                    return TrafficLightState.Green;
                if (phase < greenDuration)
                    return TrafficLightState.Yellow;
                return TrafficLightState.Red;
            }
        }

        public bool IsRed => kind == TrafficControlKind.TrafficLight && LightState == TrafficLightState.Red;
        public bool IsYellow => kind == TrafficControlKind.TrafficLight && LightState == TrafficLightState.Yellow;
        public bool IsGreen => kind != TrafficControlKind.TrafficLight || LightState == TrafficLightState.Green;

        public float DistanceAhead(Vector3 position, Vector3 travelDirection)
        {
            travelDirection.y = 0f;
            if (travelDirection.sqrMagnitude < 0.01f)
                return float.MaxValue;

            travelDirection.Normalize();
            Vector3 delta = transform.position - position;
            delta.y = 0f;
            return Vector3.Dot(delta, travelDirection);
        }

        public bool Affects(Vector3 position, Vector3 travelDirection)
        {
            Vector3 controlForward = transform.forward;
            controlForward.y = 0f;
            travelDirection.y = 0f;
            if (travelDirection.sqrMagnitude < 0.01f)
                return false;

            travelDirection.Normalize();

            // Some authored STOP/light objects face toward approaching traffic while
            // others face in the same direction as traffic. Accept both conventions.
            if (controlForward.sqrMagnitude > 0.01f &&
                Mathf.Abs(Vector3.Dot(controlForward.normalized, travelDirection)) < 0.55f)
                return false;

            Vector3 delta = transform.position - position;
            delta.y = 0f;
            float ahead = Vector3.Dot(delta, travelDirection);
            float lateral = (delta - travelDirection * ahead).magnitude;
            return ahead >= -0.5f && ahead <= detectionDistance && lateral < laneTolerance;
        }

        private void DiscoverLampRenderersIfNeeded()
        {
            if (kind != TrafficControlKind.TrafficLight)
                return;

            bool needRed = redRenderers == null || redRenderers.Length == 0;
            bool needYellow = yellowRenderers == null || yellowRenderers.Length == 0;
            bool needGreen = greenRenderers == null || greenRenderers.Length == 0;
            if (!needRed && !needYellow && !needGreen)
                return;

            Renderer[] allRenderers = GetComponentsInChildren<Renderer>(true);
            var reds = new List<Renderer>();
            var yellows = new List<Renderer>();
            var greens = new List<Renderer>();

            for (int i = 0; i < allRenderers.Length; i++)
            {
                Renderer renderer = allRenderers[i];
                if (renderer == null)
                    continue;

                string key = renderer.name.ToLowerInvariant();
                Material[] materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    if (materials[m] != null)
                        key += " " + materials[m].name.ToLowerInvariant();
                }

                if (ContainsAny(key, "red", "rouge"))
                    reds.Add(renderer);
                else if (ContainsAny(key, "yellow", "amber", "orange", "jaune"))
                    yellows.Add(renderer);
                else if (ContainsAny(key, "green", "vert"))
                    greens.Add(renderer);
            }

            if (needRed && reds.Count > 0)
                redRenderers = reds.ToArray();
            if (needYellow && yellows.Count > 0)
                yellowRenderers = yellows.ToArray();
            if (needGreen && greens.Count > 0)
                greenRenderers = greens.ToArray();
        }

        private void ApplyVisualState(bool force)
        {
            if (kind != TrafficControlKind.TrafficLight)
                return;

            TrafficLightState state = LightState;
            if (!force && visualStateInitialized && state == lastVisualState)
                return;

            if (propertyBlock == null)
                propertyBlock = new MaterialPropertyBlock();

            SetLampGroup(redRenderers, state == TrafficLightState.Red, new Color(1f, 0.04f, 0.02f, 1f));
            SetLampGroup(yellowRenderers, state == TrafficLightState.Yellow, new Color(1f, 0.55f, 0.02f, 1f));
            SetLampGroup(greenRenderers, state == TrafficLightState.Green, new Color(0.04f, 1f, 0.12f, 1f));

            lastVisualState = state;
            visualStateInitialized = true;
        }

        private void SetLampGroup(Renderer[] renderers, bool active, Color activeColor)
        {
            if (renderers == null)
                return;

            Color visibleColor = active
                ? activeColor
                : new Color(0.025f, 0.025f, 0.025f, 1f);
            Color emissionColor = active ? activeColor * 2f : Color.black;
            emissionColor.a = 1f;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                propertyBlock.Clear();
                renderer.GetPropertyBlock(propertyBlock);
                propertyBlock.SetColor(BaseColorId, visibleColor);
                propertyBlock.SetColor(ColorId, visibleColor);
                propertyBlock.SetColor(EmissionColorId, emissionColor);
                renderer.SetPropertyBlock(propertyBlock);
            }
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            for (int i = 0; i < tokens.Length; i++)
                if (value.Contains(tokens[i]))
                    return true;
            return false;
        }
    }
}
