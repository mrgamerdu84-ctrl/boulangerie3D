using Boulangerie3D.Traffic;
using UnityEditor;
using UnityEngine;

namespace Boulangerie3D.Traffic.Editor
{
    [CustomEditor(typeof(SmartTrafficLight))]
    [CanEditMultipleObjects]
    public sealed class SmartTrafficLightEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (targets.Length != 1)
                return;

            SmartTrafficLight light = (SmartTrafficLight)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Automatic detection (read only)", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Bound", light.HasRuntimeIntersection);
                EditorGUILayout.EnumPopup("Detected Axis", light.DetectedAxis);
                EditorGUILayout.TextField("Detected Direction", light.DetectedDirection);
                EditorGUILayout.TextField("Intersection", light.AssociatedIntersection);
                EditorGUILayout.Vector3Field("Logical Stop Line", light.LogicalStopLinePosition);
            }

            if (GUILayout.Button("Refresh Detection"))
            {
                light.RefreshAutomaticBinding();
                SceneView.RepaintAll();
            }
        }
    }
}
