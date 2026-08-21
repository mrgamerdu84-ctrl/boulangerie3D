using System;
using System.Collections.Generic;
using UnityEngine;

namespace Boulangerie3D.Traffic
{
    public sealed class TrafficRuntimeTurnConnector
    {
        private const int SampleCount = 12;
        private readonly Vector3[] samples = new Vector3[SampleCount + 1];

        internal TrafficRuntimeTurnConnector(TrafficRuntimeRoadSegment incoming,
            TrafficRuntimeRoadSegment outgoing, string junctionName, Bounds junctionBounds)
        {
            Incoming = incoming;
            Outgoing = outgoing;
            JunctionName = junctionName;
            Turn = TrafficRuntimeRoadGraph.ClassifyTurn(incoming, outgoing);
            Start = incoming.GetPointAtStep(incoming.PointCount - 1);
            Vector3 outgoingStart = outgoing.GetPointAtStep(0);
            Vector3 outgoingNext = outgoing.GetPointAtStep(Mathf.Min(1, outgoing.PointCount - 1));
            Vector3 outgoingVector = outgoingNext - outgoingStart;
            outgoingVector.y = 0f;
            float mergeDistance = Mathf.Min(3f, outgoingVector.magnitude * 0.25f);
            Vector3 end = outgoingStart + outgoing.StartDirection * mergeDistance;
            end.y = outgoingStart.y;
            End = end;

            float distance = Vector3.Distance(Start, End);
            float handle = Mathf.Clamp(distance * 0.45f, 1.25f, 5f);
            ControlA = Start + incoming.EndDirection * handle;
            ControlB = End - outgoing.StartDirection * handle;
            Bounds safeBounds = junctionBounds;
            safeBounds.Expand(new Vector3(4f, 6f, 4f));
            ControlA = safeBounds.ClosestPoint(ControlA);
            ControlB = safeBounds.ClosestPoint(ControlB);
            for (int i = 0; i <= SampleCount; i++)
                samples[i] = Evaluate((float)i / SampleCount);
            Id = junctionName + ":" + incoming.Id + "=>" + outgoing.Id + ":" + Turn;
        }

        public string Id { get; }
        public string JunctionName { get; }
        public TrafficRuntimeRoadSegment Incoming { get; }
        public TrafficRuntimeRoadSegment Outgoing { get; }
        public TrafficTurnDirection Turn { get; }
        public Vector3 Start { get; }
        public Vector3 End { get; }
        public Vector3 ControlA { get; }
        public Vector3 ControlB { get; }
        public int PointCount => samples.Length;
        public Vector3 GetPoint(int index) => samples[Mathf.Clamp(index, 0, samples.Length - 1)];

        public Vector3 Evaluate(float t)
        {
            t = Mathf.Clamp01(t);
            float inverse = 1f - t;
            return inverse * inverse * inverse * Start + 3f * inverse * inverse * t * ControlA +
                3f * inverse * t * t * ControlB + t * t * t * End;
        }
    }

    public sealed class TrafficRuntimeRoadSegment
    {
        private readonly List<TrafficRuntimeTurnConnector> exits = new List<TrafficRuntimeTurnConnector>(4);

        internal TrafficRuntimeRoadSegment(TrafficRoutePath route, int startIndex,
            int endIndex, string startJunctionName, string endJunctionName)
        {
            Route = route;
            StartWaypointIndex = startIndex;
            EndWaypointIndex = endIndex;
            StartJunctionName = startJunctionName;
            EndJunctionName = endJunctionName;
            Id = route.name + "[" + startIndex + "->" + endIndex + "]";
        }

        public string Id { get; }
        public TrafficRoutePath Route { get; }
        public int StartWaypointIndex { get; }
        public int EndWaypointIndex { get; }
        public string StartJunctionName { get; }
        public string EndJunctionName { get; }
        public IReadOnlyList<TrafficRuntimeTurnConnector> Exits => exits;
        public int UsageCount { get; internal set; }
        public int PointCount
        {
            get
            {
                int count = Route != null ? Route.Count : 0;
                if (count == 0) return 0;
                int distance = EndWaypointIndex - StartWaypointIndex;
                if (distance < 0) distance += count;
                return distance + 1;
            }
        }

        public int GetWaypointIndexAtStep(int step)
        {
            int count = Route != null ? Route.Count : 0;
            if (count == 0) return 0;
            return (StartWaypointIndex + Mathf.Clamp(step, 0, PointCount - 1)) % count;
        }

        public Vector3 GetPointAtStep(int step) => Route.GetPoint(GetWaypointIndexAtStep(step));
        public Vector3 StartDirection => Route.GetDirection(StartWaypointIndex);
        public Vector3 EndDirection => Route.GetDirection(EndWaypointIndex - 1);
        internal void AddExit(TrafficRuntimeTurnConnector connector) => exits.Add(connector);
    }

    public sealed class TrafficRuntimeRoadGraph
    {
        private const float JunctionExpansion = 2f;
        private readonly List<TrafficRuntimeRoadSegment> segments = new List<TrafficRuntimeRoadSegment>();
        private readonly List<TrafficRuntimeTurnConnector> connectors = new List<TrafficRuntimeTurnConnector>();
        private readonly List<string> refusedConnections = new List<string>();
        private readonly Dictionary<string, Bounds> junctionBounds = new Dictionary<string, Bounds>();

        public IReadOnlyList<TrafficRuntimeRoadSegment> Segments => segments;
        public IReadOnlyList<TrafficRuntimeTurnConnector> Connectors => connectors;
        public IReadOnlyList<string> RefusedConnections => refusedConnections;
        public bool IsUsable => segments.Count > 0 && connectors.Count > 0;

        public static TrafficRuntimeRoadGraph Build(TrafficRoutePath[] routes, BoxCollider[] junctions)
        {
            var graph = new TrafficRuntimeRoadGraph();
            if (routes == null) return graph;
            if (junctions != null)
                for (int i = 0; i < junctions.Length; i++)
                    if (junctions[i] != null) graph.junctionBounds[junctions[i].name] = junctions[i].bounds;

            for (int routeIndex = 0; routeIndex < routes.Length; routeIndex++)
            {
                TrafficRoutePath route = routes[routeIndex];
                if (route == null || route.IsPedestrianRoute || !route.IsValid) continue;
                var cuts = new List<(int index, string junction)>(8);
                if (junctions != null)
                {
                    for (int j = 0; j < junctions.Length; j++)
                    {
                        BoxCollider junction = junctions[j];
                        if (junction == null) continue;
                        Bounds expanded = junction.bounds;
                        expanded.Expand(new Vector3(JunctionExpansion * 2f, 4f, JunctionExpansion * 2f));
                        int best = -1;
                        float bestDistance = float.MaxValue;
                        for (int p = 0; p < route.Count; p++)
                        {
                            Vector3 point = route.GetPoint(p);
                            float distance = (expanded.ClosestPoint(point) - point).sqrMagnitude;
                            if (distance < bestDistance) { bestDistance = distance; best = p; }
                        }
                        if (best >= 0 && bestDistance <= 0.01f && !cuts.Exists(c => c.index == best))
                            cuts.Add((best, junction.name));
                    }
                }
                cuts.Sort((a, b) => a.index.CompareTo(b.index));
                if (cuts.Count < 2)
                {
                    graph.refusedConnections.Add(route.name + ": moins de deux carrefours détectés");
                    continue;
                }
                for (int c = 0; c < cuts.Count; c++)
                {
                    var start = cuts[c];
                    var end = cuts[(c + 1) % cuts.Count];
                    graph.segments.Add(new TrafficRuntimeRoadSegment(route, start.index,
                        end.index, start.junction, end.junction));
                }
            }
            graph.ConnectSegments();
            return graph;
        }

        public TrafficRuntimeTurnConnector ChooseNext(TrafficRuntimeRoadSegment incoming,
            Queue<TrafficRuntimeRoadSegment> recent, System.Random random,
            List<TrafficRuntimeTurnConnector> available, out TrafficTurnDirection turn)
        {
            available.Clear();
            turn = TrafficTurnDirection.None;
            if (incoming == null) return null;
            for (int i = 0; i < incoming.Exits.Count; i++) available.Add(incoming.Exits[i]);
            if (available.Count == 0) return null;
            float total = 0f;
            var weights = new float[available.Count];
            for (int i = 0; i < available.Count; i++)
            {
                TrafficRuntimeTurnConnector connector = available[i];
                TrafficRuntimeRoadSegment candidate = connector.Outgoing;
                float exploration = 1f / (1f + candidate.UsageCount);
                float recentPenalty = recent != null && recent.Contains(candidate) ? 0.12f : 1f;
                weights[i] = Mathf.Max(0.0001f,
                    exploration * recentPenalty * GetTurnWeight(connector.Turn));
                total += weights[i];
            }
            float sample = (float)(random != null ? random.NextDouble() : UnityEngine.Random.value) * total;
            TrafficRuntimeTurnConnector selected = available[available.Count - 1];
            for (int i = 0; i < available.Count; i++)
            {
                sample -= weights[i];
                if (sample <= 0f) { selected = available[i]; break; }
            }
            selected.Outgoing.UsageCount++;
            turn = selected.Turn;
            return selected;
        }

        public static TrafficTurnDirection ClassifyTurn(TrafficRuntimeRoadSegment incoming,
            TrafficRuntimeRoadSegment outgoing)
        {
            if (incoming == null || outgoing == null) return TrafficTurnDirection.None;
            float angle = Vector3.SignedAngle(incoming.EndDirection, outgoing.StartDirection, Vector3.up);
            float absolute = Mathf.Abs(angle);
            if (absolute >= 135f) return TrafficTurnDirection.UTurn;
            if (absolute <= 35f) return TrafficTurnDirection.Straight;
            return angle > 0f ? TrafficTurnDirection.Right : TrafficTurnDirection.Left;
        }

        private void ConnectSegments()
        {
            for (int i = 0; i < segments.Count; i++)
            {
                TrafficRuntimeRoadSegment incoming = segments[i];
                for (int j = 0; j < segments.Count; j++)
                {
                    TrafficRuntimeRoadSegment outgoing = segments[j];
                    if (!string.Equals(incoming.EndJunctionName, outgoing.StartJunctionName,
                        StringComparison.Ordinal)) continue;
                    TrafficTurnDirection turn = ClassifyTurn(incoming, outgoing);
                    if (turn == TrafficTurnDirection.UTurn)
                    {
                        refusedConnections.Add(incoming.Id + " => " + outgoing.Id + ": demi-tour interdit");
                        continue;
                    }
                    if (!junctionBounds.TryGetValue(incoming.EndJunctionName, out Bounds bounds))
                    {
                        refusedConnections.Add(incoming.Id + " => " + outgoing.Id +
                            ": limites du carrefour absentes");
                        continue;
                    }
                    var connector = new TrafficRuntimeTurnConnector(incoming, outgoing,
                        incoming.EndJunctionName, bounds);
                    connectors.Add(connector);
                    incoming.AddExit(connector);
                }
            }
        }

        private static float GetTurnWeight(TrafficTurnDirection turn)
        {
            if (turn == TrafficTurnDirection.Left) return 0.8f;
            if (turn == TrafficTurnDirection.Right) return 0.9f;
            return turn == TrafficTurnDirection.UTurn ? 0f : 1f;
        }
    }
}
