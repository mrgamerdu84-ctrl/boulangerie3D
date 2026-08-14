using UnityEngine;

namespace Boulangerie3D.Traffic
{
    /// <summary>
    /// Connects decorative traffic-light and STOP props to the traffic simulation once
    /// when the scene starts. It does not move, rotate or otherwise edit authored objects.
    /// </summary>
    public static class TrafficControlAutoBinder
    {
        private static bool bound;

        public static void BindSceneControls()
        {
            if (bound)
                return;
            bound = true;

            Transform[] transforms = Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            int lightsAdded = 0;
            int stopsAdded = 0;

            System.Array.Sort(transforms, CompareDepth);

            for (int i = 0; i < transforms.Length; i++)
            {
                Transform current = transforms[i];
                if (current == null || current.GetComponentInParent<TrafficControlPoint>() != null)
                    continue;

                string normalized = Normalize(current.name);
                bool isTrafficLight = LooksLikeTrafficLight(normalized);
                bool isStop = !isTrafficLight && LooksLikeStopSign(normalized);
                if (!isTrafficLight && !isStop)
                    continue;

                if (current.GetComponentInChildren<Renderer>(true) == null)
                    continue;

                TrafficControlPoint control = current.gameObject.AddComponent<TrafficControlPoint>();
                if (isTrafficLight)
                {
                    // Roadside props can sit several metres away from the lane centre.
                    // The intersection/approach binding prevents this wider tolerance from
                    // accidentally controlling the perpendicular road.
                    control.Configure(TrafficControlKind.TrafficLight, 20f, 9f);
                    lightsAdded++;
                }
                else
                {
                    control.Configure(TrafficControlKind.Stop, 18f, 9f);
                    stopsAdded++;
                }
            }

            Debug.Log($"[MobileTraffic] Raccordement automatique : {lightsAdded} feu(x), {stopsAdded} STOP ajouté(s)." );
        }

        private static bool LooksLikeTrafficLight(string name)
        {
            return name.Contains("trafficlight") ||
                   name.Contains("trafficsignal") ||
                   name.Contains("stoplight") ||
                   name.Contains("trafficlamp") ||
                   name.Contains("semaphore") ||
                   name.Contains("feutricolore") ||
                   name.Contains("feucirculation") ||
                   name.Contains("feutraf") ||
                   name.Contains("signallumineux");
        }

        private static bool LooksLikeStopSign(string name)
        {
            return name == "stop" ||
                   name.Contains("stopsign") ||
                   name.Contains("signstop") ||
                   name.Contains("roadsignstop") ||
                   name.Contains("panneaustop") ||
                   name.Contains("panneauaustop") ||
                   name.Contains("trafficstop");
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.ToLowerInvariant()
                .Replace(" ", string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(".", string.Empty);
        }

        private static int CompareDepth(Transform a, Transform b)
        {
            return GetDepth(a).CompareTo(GetDepth(b));
        }

        private static int GetDepth(Transform transform)
        {
            int depth = 0;
            Transform current = transform;
            while (current != null)
            {
                depth++;
                current = current.parent;
            }
            return depth;
        }
    }
}
