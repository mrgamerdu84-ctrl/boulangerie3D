using UnityEngine;
using System.Collections.Generic;

namespace Boulangerie3D.Traffic
{
    /// <summary>
    /// Raccorde tous les vrais accessoires de circulation visibles à la simulation.
    /// Chaque feu est ensuite associé par TrafficControlPoint à l'intersection la plus proche.
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

            System.Array.Sort(transforms, CompareDepth);

            var lightCandidates = new List<Transform>();
            var stopCandidates = new List<Transform>();

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

                if (isTrafficLight) lightCandidates.Add(current);
                else stopCandidates.Add(current);
            }

            // Plusieurs carrefours existent dans la ville : on ne limite plus le raccordement
            // au premier groupe de quatre feux. Tous les feux visibles doivent fonctionner.
            int lightsAdded = 0;
            for (int i = 0; i < lightCandidates.Count; i++)
            {
                Transform light = lightCandidates[i];
                if (light == null || light.GetComponent<TrafficControlPoint>() != null)
                    continue;

                TrafficControlPoint control = light.gameObject.AddComponent<TrafficControlPoint>();
                // Tolérance assez large pour absorber les petits décalages de waypoint,
                // mais TrafficControlPoint filtre ensuite l'axe et le côté d'approche.
                control.Configure(TrafficControlKind.TrafficLight, 24f, 8f);
                lightsAdded++;
            }

            int stopsAdded = 0;
            for (int i = 0; i < stopCandidates.Count; i++)
            {
                Transform stop = stopCandidates[i];
                if (stop == null || stop.GetComponent<TrafficControlPoint>() != null)
                    continue;

                TrafficControlPoint control = stop.gameObject.AddComponent<TrafficControlPoint>();
                control.Configure(TrafficControlKind.Stop, 16f, 4.5f);
                stopsAdded++;
            }

            Debug.Log($"[MobileTraffic] Tous carrefours : {lightsAdded} feu(x) raccordé(s), {stopsAdded} STOP raccordé(s)." );
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
