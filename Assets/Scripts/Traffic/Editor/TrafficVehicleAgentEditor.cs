using Boulangerie3D.Traffic;
using UnityEditor;
using UnityEngine;

namespace Boulangerie3D.Traffic.Editor
{
    [CustomEditor(typeof(TrafficVehicleAgent))]
    [CanEditMultipleObjects]
    public sealed class TrafficVehicleAgentEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (targets.Length != 1)
                return;

            TrafficVehicleAgent vehicle = (TrafficVehicleAgent)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Traffic braking debug (read only)", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.FloatField("Current Speed", vehicle.CurrentSpeed);
                EditorGUILayout.FloatField("Braking Distance", vehicle.BrakingDistance);
                EditorGUILayout.FloatField(
                    "Required Detection Range",
                    vehicle.RequiredTrafficDetectionDistance);
                EditorGUILayout.ObjectField(
                    "Accepted Traffic Light",
                    vehicle.AcceptedTrafficLight,
                    typeof(TrafficControlPoint),
                    true);
                EditorGUILayout.Toggle("Uses Road Graph", vehicle.UsesRoadGraph);
                EditorGUILayout.TextField("Runtime Segment", vehicle.CurrentRuntimeSegment);
                EditorGUILayout.TextField("Next Runtime Segment", vehicle.NextRuntimeSegment);
                EditorGUILayout.TextField("Active Connector", vehicle.ActiveRuntimeConnector);
                EditorGUILayout.ObjectField(
                    "Current Segment",
                    vehicle.CurrentRoadSegment,
                    typeof(TrafficRoadSegment),
                    true);
                EditorGUILayout.ObjectField(
                    "Next Segment",
                    vehicle.NextRoadSegment,
                    typeof(TrafficRoadSegment),
                    true);
                EditorGUILayout.EnumPopup("Junction Direction", vehicle.NextTurnDirection);
            }

            EditorGUILayout.LabelField("Available Exits", EditorStyles.boldLabel);
            if (vehicle.AvailableRuntimeConnectors.Count > 0)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    for (int i = 0; i < vehicle.AvailableRuntimeConnectors.Count; i++)
                        EditorGUILayout.TextField(
                            $"Runtime Exit {i + 1}",
                            vehicle.AvailableRuntimeConnectors[i].Id);
                }
                return;
            }
            if (vehicle.AvailableRoadExits.Count == 0)
            {
                EditorGUILayout.LabelField("None");
            }
            else
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    for (int i = 0; i < vehicle.AvailableRoadExits.Count; i++)
                        EditorGUILayout.ObjectField(
                            $"Exit {i + 1}",
                            vehicle.AvailableRoadExits[i],
                            typeof(TrafficRoadSegment),
                            true);
                }
            }
        }
    }
}
