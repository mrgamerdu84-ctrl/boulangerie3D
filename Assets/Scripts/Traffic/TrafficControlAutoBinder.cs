using UnityEngine;
using System.Collections.Generic;

namespace Boulangerie3D.Traffic
{
    /// <summary>
    /// Raccorde uniquement les vrais accessoires de circulation visibles à la simulation.
    /// Pour les feux, on travaille carrefour par carrefour : le groupe de quatre feux le
    /// plus compact est traité comme un seul carrefour et chaque feu reçoit une zone
    /// d'approche cohérente avec le centre du groupe.
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

            // Le projet est volontairement repris carrefour par carrefour. Tant qu'il n'y
            // a qu'un carrefour, les quatre feux visibles doivent tous appartenir au même
            // groupe. On évite ainsi qu'un ancien objet éloigné influence les voitures.
            List<Transform> activeLights = SelectFirstIntersection(lightCandidates);
            int lightsAdded = 0;
            for (int i = 0; i < activeLights.Count; i++)
            {
                TrafficControlPoint control = activeLights[i].gameObject.AddComponent<TrafficControlPoint>();
                control.Configure(TrafficControlKind.TrafficLight, 20f, 5.25f);
                lightsAdded++;
            }

            int stopsAdded = 0;
            for (int i = 0; i < stopCandidates.Count; i++)
            {
                TrafficControlPoint control = stopCandidates[i].gameObject.AddComponent<TrafficControlPoint>();
                control.Configure(TrafficControlKind.Stop, 16f, 4.5f);
                stopsAdded++;
            }

            Debug.Log($"[MobileTraffic] Premier carrefour : {lightsAdded} feu(x) raccordé(s), {stopsAdded} STOP raccordé(s)." );
        }

        private static List<Transform> SelectFirstIntersection(List<Transform> candidates)
        {
            if (candidates.Count <= 4)
                return candidates;

            // Cherche le groupe de quatre feux dont les distances mutuelles sont les plus
            // petites. C'est robuste même si les noms des quatre prefabs sont identiques.
            float bestScore = float.MaxValue;
            var best = new List<Transform>(4);

            for (int anchor = 0; anchor < candidates.Count; anchor++)
            {
                Transform a = candidates[anchor];
                var ordered = new List<Transform>(candidates);
                ordered.Sort((x, y) => HorizontalSqr(a.position, x.position).CompareTo(HorizontalSqr(a.position, y.position)));
                if (ordered.Count < 4) continue;

                float score = 0f;
                Vector3 center = Vector3.zero;
                for (int i = 0; i < 4; i++) center += ordered[i].position;
                center /= 4f;
                for (int i = 0; i < 4; i++) score += HorizontalSqr(center, ordered[i].position);

                if (score >= bestScore) continue;
                bestScore = score;
                best.Clear();
                for (int i = 0; i < 4; i++) best.Add(ordered[i]);
            }

            return best.Count == 4 ? best : candidates;
        }

        private static float HorizontalSqr(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return x * x + z * z;
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
