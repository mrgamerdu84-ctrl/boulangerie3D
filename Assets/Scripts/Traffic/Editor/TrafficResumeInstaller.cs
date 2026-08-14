using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Boulangerie3D.Traffic.Editor
{
    [InitializeOnLoad]
    public static class TrafficResumeInstaller
    {
        private const string ScenePath = "Assets/BOULANGERIE.unity";
        private const string Root = "Assets/TrafficSystem";
        private const string ValidationRoot = Root + "/Validation";
        private const string InstallReport = ValidationRoot + "/TrafficResumeInstall.txt";
        private const string ValidationReport = ValidationRoot + "/TrafficPlayValidation.txt";
        private const string CompleteMarker = ValidationRoot + "/TrafficResume.completed";
        private const string PendingKey = "Boulangerie3D.TrafficResume.PendingValidation";
        private const string InstalledKey = "Boulangerie3D.TrafficResume.InstalledThisSession";
        private const string StartKey = "Boulangerie3D.TrafficResume.ValidationStart";
        private const string ResultKey = "Boulangerie3D.TrafficResume.ValidationResult";
        private const string PedestrianRoot = "Assets/URP_Migrated/UserLibrary/Pedestrians";

        private static readonly string[] CharacterSources =
        {
            "Assets/npc_casual_set_00/Prefabs/npc_csl_00_character_01f_01.prefab",
            "Assets/npc_casual_set_00/Prefabs/npc_csl_00_character_01f_02.prefab",
            "Assets/npc_casual_set_00/Prefabs/npc_csl_00_character_01m_03.prefab",
            "Assets/npc_casual_set_00/Prefabs/npc_csl_00_character_02m_01.prefab"
        };

        static TrafficResumeInstaller()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;
            Application.logMessageReceived += OnLogMessage;
            EditorApplication.delayCall += TryInstall;
        }

        [MenuItem("Tools/Boulangerie3D/Resume Traffic And Pedestrians")]
        private static void RunFromMenu()
        {
            RunBatch();
        }

        public static void RunBatch()
        {
            SessionState.SetBool(PendingKey, false);
            SessionState.SetBool(InstalledKey, false);
            TryInstall();
        }

        public static void RunValidationBatch()
        {
            SessionState.SetBool(PendingKey, false);
            SessionState.SetBool(InstalledKey, true);
            SessionState.SetString(ResultKey, string.Empty);

            Scene active = SceneManager.GetActiveScene();
            if (active.path != ScenePath)
            {
                bool isDisposableBatchScene = Application.isBatchMode && string.IsNullOrEmpty(active.path);
                if (active.isDirty && !isDisposableBatchScene)
                {
                    Debug.LogError("TRAFFIC_VALIDATION_ABORT: active scene has unsaved changes.");
                    if (Application.isBatchMode) EditorApplication.Exit(4);
                    return;
                }
                active = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            if (active.isDirty)
            {
                Debug.LogError("TRAFFIC_VALIDATION_ABORT: BOULANGERIE.unity has unsaved changes.");
                if (Application.isBatchMode) EditorApplication.Exit(4);
                return;
            }

            GameObject trafficRoot = GameObject.Find("TrafficSimulation");
            if (trafficRoot == null)
            {
                Debug.LogError("TRAFFIC_VALIDATION_ABORT: TrafficSimulation is missing.");
                if (Application.isBatchMode) EditorApplication.Exit(4);
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(CompleteMarker) != null)
                AssetDatabase.DeleteAsset(CompleteMarker);
            TrafficValidationProbe probe = trafficRoot.GetComponent<TrafficValidationProbe>();
            if (probe == null) probe = trafficRoot.AddComponent<TrafficValidationProbe>();
            EditorUtility.SetDirty(trafficRoot);
            EditorSceneManager.MarkSceneDirty(active);
            EditorSceneManager.SaveScene(active);
            AssetDatabase.SaveAssets();
            SessionState.SetBool(PendingKey, true);
            EditorApplication.delayCall += EditorApplication.EnterPlaymode;
        }

        private static void TryInstall()
        {
            if (File.Exists(AbsolutePath(CompleteMarker)) || SessionState.GetBool(PendingKey, false) ||
                SessionState.GetBool(InstalledKey, false) || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            Scene active = SceneManager.GetActiveScene();
            if (active.path != ScenePath)
            {
                bool isDisposableBatchScene = Application.isBatchMode && string.IsNullOrEmpty(active.path);
                if (active.isDirty && !isDisposableBatchScene)
                {
                    WriteText(InstallReport, "ABORT: la scène active comporte des modifications non enregistrées; aucune scène n'a été touchée.");
                    return;
                }
                active = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            if (active.isDirty)
            {
                WriteText(InstallReport, "ABORT: BOULANGERIE.unity était déjà modifiée dans l'éditeur; installation suspendue pour ne pas écraser le travail en cours.");
                return;
            }

            SessionState.SetBool(InstalledKey, true);
            var report = new List<string>
            {
                "Traffic resume install " + DateTime.Now.ToString("O"),
                "Scene: " + ScenePath,
                "BakeryEnvironmentV2 modified: NO",
                "Existing roads/buildings/park modified: NO"
            };

            try
            {
                EnsureFolder(Root);
                EnsureFolder(ValidationRoot);
                EnsureFolder(PedestrianRoot);
                EnsureFolder(PedestrianRoot + "/Materials");
                EnsureFolder(PedestrianRoot + "/Prefabs");
                EnsureFolder("Assets/SceneBackups");

                string backup = "Assets/SceneBackups/BOULANGERIE_before_traffic_resume_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".unity";
                if (!AssetDatabase.CopyAsset(ScenePath, backup))
                    throw new InvalidOperationException("Impossible de créer la sauvegarde de scène " + backup);
                report.Add("Backup: " + backup);

                GameObject trafficRoot = GameObject.Find("TrafficSimulation");
                if (trafficRoot == null)
                    throw new InvalidOperationException("TrafficSimulation est absent de BOULANGERIE.unity.");
                MobileTrafficController controller = trafficRoot.GetComponent<MobileTrafficController>();
                if (controller == null)
                    throw new InvalidOperationException("MobileTrafficController est absent de TrafficSimulation.");

                TrafficRoutePath[] routes = trafficRoot.GetComponentsInChildren<TrafficRoutePath>(true);
                int vehicleRoutes = routes.Count(route => !route.IsPedestrianRoute);
                int pedestrianRoutes = routes.Count(route => route.IsPedestrianRoute);
                int existingCars = trafficRoot.GetComponentsInChildren<TrafficVehicleAgent>(true).Length;
                int existingPedestrians = trafficRoot.GetComponentsInChildren<PedestrianAgent>(true).Length;
                report.Add($"Preserved existing system: cars={existingCars}, vehicleRoutes={vehicleRoutes}, pedestrianRoutes={pedestrianRoutes}, crosswalks={trafficRoot.GetComponentsInChildren<CrosswalkPriorityZone>(true).Length}");

                List<GameObject> pedestrianPrefabs = CreatePedestrianPrefabs(report);
                Transform pool = GetObjectReference<Transform>(controller, "pedestrianPoolRoot");
                if (pool == null)
                    throw new InvalidOperationException("Le parent pedestrianPoolRoot sérialisé est manquant.");
                pool.name = "PedestrianPool_8_Max";

                if (existingPedestrians == 0)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        GameObject instance = PrefabUtility.InstantiatePrefab(pedestrianPrefabs[i % pedestrianPrefabs.Count], active) as GameObject;
                        if (instance == null) throw new InvalidOperationException("Échec d'instanciation du piéton " + i);
                        instance.name = "PooledPedestrian_" + (i + 1).ToString("00");
                        instance.transform.SetParent(pool, false);
                        instance.transform.localPosition = Vector3.zero;
                        instance.transform.localRotation = Quaternion.identity;
                        instance.SetActive(true);
                    }
                    report.Add("Pedestrian pool added: 8 lightweight npc_casual_set_00 instances (4 shared corrected prefabs). ");
                }
                else
                {
                    report.Add("Pedestrian pool already contained agents; no duplicate was created.");
                }

                SerializedObject controllerSerialized = new SerializedObject(controller);
                controllerSerialized.FindProperty("maxVisibleVehicles").intValue = Mathf.Clamp(existingCars, 4, 6);
                controllerSerialized.FindProperty("maxVisiblePedestrians").intValue = 8;

                Transform signs = FindByName("RoadSigns");
                Transform trafficLights = FindByName("TrafficLights");
                int signChildren = signs == null ? 0 : signs.childCount;
                int lightChildren = trafficLights == null ? 0 : trafficLights.childCount;
                TrafficControlPoint[] existingControls = trafficRoot.GetComponentsInChildren<TrafficControlPoint>(true);
                controllerSerialized.FindProperty("controls").arraySize = existingControls.Length;
                for (int i = 0; i < existingControls.Length; i++)
                    controllerSerialized.FindProperty("controls").GetArrayElementAtIndex(i).objectReferenceValue = existingControls[i];
                controllerSerialized.ApplyModifiedPropertiesWithoutUndo();
                report.Add($"Road-sign hierarchy children={signChildren}, traffic-light hierarchy children={lightChildren}, functional controls preserved={existingControls.Length}.");
                if (signChildren == 0 && lightChildren == 0)
                    report.Add("IMPORTANT: RoadSigns and TrafficLights are genuinely empty in the current scene; no invisible/fake STOP or light was invented.");

                TrafficValidationProbe probe = trafficRoot.GetComponent<TrafficValidationProbe>();
                if (probe == null) probe = trafficRoot.AddComponent<TrafficValidationProbe>();
                EditorUtility.SetDirty(trafficRoot);
                EditorUtility.SetDirty(controller);
                EditorSceneManager.MarkSceneDirty(active);
                EditorSceneManager.SaveScene(active);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                WriteText(InstallReport, string.Join(Environment.NewLine, report));

                SessionState.SetBool(PendingKey, true);
                SessionState.SetString(ResultKey, string.Empty);
                EditorApplication.delayCall += () => EditorApplication.EnterPlaymode();
            }
            catch (Exception exception)
            {
                report.Add("FAILED: " + exception);
                WriteText(InstallReport, string.Join(Environment.NewLine, report));
                Debug.LogException(exception);
                if (Application.isBatchMode) EditorApplication.Exit(3);
            }
        }

        private static List<GameObject> CreatePedestrianPrefabs(List<string> report)
        {
            var result = new List<GameObject>();
            Dictionary<string, Material> uprMaterials = AssetDatabase.FindAssets("t:Material", new[] { "Assets/npc_casual_set_00/MaterialsUPR" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(path => AssetDatabase.LoadAssetAtPath<Material>(path))
                .Where(material => material != null)
                .GroupBy(material => material.name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) throw new InvalidOperationException("Universal Render Pipeline/Lit est indisponible.");

            foreach (string sourcePath in CharacterSources)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath) == null)
                    throw new FileNotFoundException("Prefab piéton introuvable", sourcePath);
                GameObject contents = PrefabUtility.LoadPrefabContents(sourcePath);
                try
                {
                    int remapped = 0;
                    foreach (Renderer renderer in contents.GetComponentsInChildren<Renderer>(true))
                    {
                        Material[] materials = renderer.sharedMaterials;
                        bool changed = false;
                        for (int i = 0; i < materials.Length; i++)
                        {
                            Material source = materials[i];
                            if (source == null) continue;
                            Material destination;
                            if (!uprMaterials.TryGetValue(source.name, out destination) || !IsUrpMaterial(destination))
                                destination = CreateFallbackMaterial(source, lit);
                            materials[i] = destination;
                            changed = true;
                            remapped++;
                        }
                        if (changed) renderer.sharedMaterials = materials;
                        if (renderer is SkinnedMeshRenderer skinned) skinned.updateWhenOffscreen = false;
                    }

                    PedestrianAgent agent = contents.GetComponent<PedestrianAgent>();
                    if (agent == null) agent = contents.AddComponent<PedestrianAgent>();
                    CapsuleCollider capsule = contents.GetComponent<CapsuleCollider>();
                    if (capsule == null) capsule = contents.AddComponent<CapsuleCollider>();
                    capsule.center = new Vector3(0f, 0.95f, 0f);
                    capsule.height = 1.9f;
                    capsule.radius = 0.32f;
                    capsule.direction = 1;
                    capsule.isTrigger = false;
                    Rigidbody body = contents.GetComponent<Rigidbody>();
                    if (body == null) body = contents.AddComponent<Rigidbody>();
                    body.isKinematic = true;
                    body.useGravity = false;
                    body.interpolation = RigidbodyInterpolation.None;
                    foreach (Animator animator in contents.GetComponentsInChildren<Animator>(true)) animator.applyRootMotion = false;

                    string destinationPath = PedestrianRoot + "/Prefabs/" + Path.GetFileNameWithoutExtension(sourcePath) + "_URP_Mobile.prefab";
                    GameObject saved = PrefabUtility.SaveAsPrefabAsset(contents, destinationPath);
                    if (saved == null) throw new InvalidOperationException("Échec création " + destinationPath);
                    result.Add(saved);
                    report.Add($"Pedestrian prefab: {destinationPath} (material slots remapped={remapped}, LODGroups={saved.GetComponentsInChildren<LODGroup>(true).Length})");
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
            return result;
        }

        private static Material CreateFallbackMaterial(Material source, Shader lit)
        {
            string destinationPath = PedestrianRoot + "/Materials/" + Safe(source.name) + "_URP.mat";
            Material destination = AssetDatabase.LoadAssetAtPath<Material>(destinationPath);
            if (destination == null)
            {
                destination = new Material(lit) { name = source.name + "_URP", enableInstancing = true };
                AssetDatabase.CreateAsset(destination, destinationPath);
            }
            Texture texture = source.HasProperty("_BaseMap") ? source.GetTexture("_BaseMap") : source.HasProperty("_MainTex") ? source.GetTexture("_MainTex") : null;
            Color color = source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
            destination.shader = lit;
            destination.SetColor("_BaseColor", color);
            destination.SetTexture("_BaseMap", texture);
            EditorUtility.SetDirty(destination);
            return destination;
        }

        private static bool IsUrpMaterial(Material material)
        {
            return material != null && material.shader != null && material.shader.isSupported &&
                   (material.shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal) ||
                    material.shader.name.StartsWith("Shader Graphs/", StringComparison.Ordinal));
        }

        private static T GetObjectReference<T>(UnityEngine.Object target, string propertyName) where T : UnityEngine.Object
        {
            SerializedObject serialized = new SerializedObject(target);
            return serialized.FindProperty(propertyName).objectReferenceValue as T;
        }

        private static Transform FindByName(string name)
        {
            return UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(transform => transform.name == name);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(PendingKey, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
                SessionState.SetString(StartKey, EditorApplication.timeSinceStartup.ToString(CultureInfo.InvariantCulture));
            else if (state == PlayModeStateChange.EnteredEditMode)
                FinalizeValidation();
        }

        private static void OnEditorUpdate()
        {
            if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying) return;
            if (!string.IsNullOrEmpty(SessionState.GetString(ResultKey, string.Empty)))
            {
                EditorApplication.ExitPlaymode();
                return;
            }
            if (!double.TryParse(SessionState.GetString(StartKey, "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out double start)) return;
            if (EditorApplication.timeSinceStartup - start >= 120d)
                EditorApplication.ExitPlaymode();
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (SessionState.GetBool(PendingKey, false) && condition.StartsWith("TRAFFIC_VALIDATION_", StringComparison.Ordinal))
                SessionState.SetString(ResultKey, condition);
        }

        private static void FinalizeValidation()
        {
            string result = SessionState.GetString(ResultKey, string.Empty);
            if (string.IsNullOrEmpty(result)) result = "TRAFFIC_VALIDATION_FAIL: probe produced no result before timeout.";
            WriteText(ValidationReport, result + Environment.NewLine + "Validated: " + DateTime.Now.ToString("O"));

            Scene active = SceneManager.GetActiveScene();
            if (active.path == ScenePath)
            {
                GameObject trafficRoot = GameObject.Find("TrafficSimulation");
                TrafficValidationProbe probe = trafficRoot == null ? null : trafficRoot.GetComponent<TrafficValidationProbe>();
                if (probe != null) UnityEngine.Object.DestroyImmediate(probe);
                EditorSceneManager.MarkSceneDirty(active);
                EditorSceneManager.SaveScene(active);
            }
            WriteText(CompleteMarker, result.StartsWith("TRAFFIC_VALIDATION_PASS", StringComparison.Ordinal) ? "PASS" : "FAIL");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            SessionState.SetBool(PendingKey, false);
            Debug.Log("[Traffic Resume] " + result);
            if (Application.isBatchMode)
                EditorApplication.Exit(result.StartsWith("TRAFFIC_VALIDATION_PASS", StringComparison.Ordinal) ? 0 : 2);
        }

        private static void WriteText(string assetPath, string content)
        {
            EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
            File.WriteAllText(AbsolutePath(assetPath), content);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        private static string AbsolutePath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string Safe(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value.Replace(' ', '_');
        }

        private static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            string parent = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetPath));
        }
    }
}
