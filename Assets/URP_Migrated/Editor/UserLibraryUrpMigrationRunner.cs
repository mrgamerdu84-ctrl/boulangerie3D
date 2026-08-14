using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Boulangerie3D.EditorTools
{
    [InitializeOnLoad]
    internal static class UserLibraryUrpMigrationRunner
    {
        private const string Root = "Assets/URP_Migrated/UserLibrary";
        private const string ValidationRoot = Root + "/Validation";
        private const string Marker = ValidationRoot + "/UserLibraryUrpMigration.completed";
        private const string SessionKey = "Boulangerie3D.UserLibraryUrpMigration.Ran";
        private static readonly string[] Categories =
        {
            "Buildings", "Commercial", "Roads", "Vehicles", "Nature", "StreetFurniture", "Signs"
        };

        private static readonly string[] BaseTextureNames =
        {
            "_BaseMap", "_MainTex", "_BaseColorMap", "_Albedo", "_AlbedoMap", "_Diffuse", "_DiffuseMap", "_ColorMap", "_BaseTexture"
        };
        private static readonly string[] BaseColorNames = { "_BaseColor", "_Color", "_TintColor", "_Tint", "_DiffuseColor" };
        private static readonly string[] NormalTextureNames = { "_BumpMap", "_NormalMap", "_NormalTex", "_BumpTex", "_Normals" };
        private static readonly string[] MetallicTextureNames = { "_MetallicGlossMap", "_MetallicMap", "_MetalnessMap" };
        private static readonly string[] SpecularTextureNames = { "_SpecGlossMap", "_SpecularMap", "_SpecMap" };
        private static readonly string[] OcclusionTextureNames = { "_OcclusionMap", "_AOMap", "_AmbientOcclusionMap" };
        private static readonly string[] EmissionTextureNames = { "_EmissionMap", "_EmissiveColorMap", "_EmissiveMap" };

        [Serializable]
        private sealed class AssetRecord
        {
            public string category;
            public string source;
            public string destination;
            public string sourceShader;
            public string destinationShader;
            public string note;
        }

        [Serializable]
        private sealed class CategoryRecord
        {
            public string category;
            public int materials;
            public int prefabs;
            public int invalidSlots;
            public int magentaPixels;
            public string preview;
        }

        [Serializable]
        private sealed class MigrationReport
        {
            public string startedUtc;
            public string completedUtc;
            public string unityVersion;
            public string originalScenePath;
            public string originalSceneHashBefore;
            public string originalSceneHashAfter;
            public bool originalSceneUnchanged;
            public int scannedMaterialAssets;
            public int scannedPrefabs;
            public int incompatibleMaterialsFound;
            public int correctedMaterialCopies;
            public int affectedPrefabsFound;
            public int correctedPrefabCopies;
            public int validationInvalidMaterialSlots;
            public int validationMagentaPixels;
            public List<CategoryRecord> categories = new List<CategoryRecord>();
            public List<AssetRecord> materials = new List<AssetRecord>();
            public List<AssetRecord> prefabs = new List<AssetRecord>();
            public List<AssetRecord> approximations = new List<AssetRecord>();
            public List<AssetRecord> impossible = new List<AssetRecord>();
            public List<string> notes = new List<string>();
        }

        private sealed class SavedProperties
        {
            public readonly Dictionary<string, Texture> textures = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Vector2> scales = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Vector2> offsets = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Color> colors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, float> floats = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PrefabCandidate
        {
            public string sourcePath;
            public string category;
            public GameObject source;
            public List<Material> incompatible = new List<Material>();
            public int missingSlots;
        }

        static UserLibraryUrpMigrationRunner()
        {
            EditorApplication.delayCall += TryAutoRun;
        }

        [MenuItem("Tools/Boulangerie3D/Migrate User Library URP (non destructive)")]
        private static void RunFromMenu()
        {
            RunMigration();
        }

        private static void TryAutoRun()
        {
            if (SessionState.GetBool(SessionKey, false) || File.Exists(AbsolutePath(Marker))) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += TryAutoRun;
                return;
            }

            SessionState.SetBool(SessionKey, true);
            RunMigration();
        }

        private static void RunMigration()
        {
            var report = new MigrationReport
            {
                startedUtc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                originalScenePath = SceneManager.GetActiveScene().path
            };

            string sceneAbsolute = string.IsNullOrEmpty(report.originalScenePath) ? string.Empty : AbsolutePath(report.originalScenePath);
            report.originalSceneHashBefore = HashFile(sceneAbsolute);

            try
            {
                Shader lit = Shader.Find("Universal Render Pipeline/Lit");
                Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
                if (lit == null || unlit == null)
                    throw new InvalidOperationException("Les shaders Universal Render Pipeline/Lit et Unlit doivent être disponibles.");

                EnsureFolder(Root);
                EnsureFolder(ValidationRoot);
                foreach (string category in Categories)
                {
                    EnsureFolder(Root + "/" + category);
                    EnsureFolder(Root + "/" + category + "/Materials");
                    EnsureFolder(Root + "/" + category + "/Prefabs");
                }

                string[] materialGuids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
                string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
                report.scannedMaterialAssets = materialGuids.Length;
                report.scannedPrefabs = prefabGuids.Length;

                var prefabCandidates = CollectPrefabCandidates(prefabGuids, report);
                report.affectedPrefabsFound = prefabCandidates.Count;

                var relevantMaterials = new Dictionary<string, Tuple<Material, string, string>>();
                foreach (PrefabCandidate candidate in prefabCandidates)
                {
                    foreach (Material material in candidate.incompatible)
                    {
                        string key = MaterialKey(material) + "|" + candidate.category;
                        if (!relevantMaterials.ContainsKey(key))
                            relevantMaterials.Add(key, Tuple.Create(material, candidate.category, AssetDatabase.GetAssetPath(material)));
                    }
                }

                foreach (string guid in materialGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path) || IsValidationAsset(path)) continue;
                    foreach (Material material in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>())
                    {
                        if (!IsIncompatible(material)) continue;
                        string category = Classify(path + "/" + material.name);
                        if (string.IsNullOrEmpty(category)) continue;
                        string key = MaterialKey(material) + "|" + category;
                        if (!relevantMaterials.ContainsKey(key))
                            relevantMaterials.Add(key, Tuple.Create(material, category, path));
                    }
                }

                report.incompatibleMaterialsFound = relevantMaterials.Count;
                var migratedMaterials = new Dictionary<string, Material>();
                foreach (KeyValuePair<string, Tuple<Material, string, string>> pair in relevantMaterials.OrderBy(p => p.Value.Item2).ThenBy(p => p.Value.Item3))
                {
                    Material source = pair.Value.Item1;
                    string category = pair.Value.Item2;
                    string sourcePath = pair.Value.Item3;
                    Material migrated = CreateOrUpdateMaterialCopy(source, sourcePath, category, lit, unlit, report);
                    if (migrated != null)
                        migratedMaterials[pair.Key] = migrated;
                }
                report.correctedMaterialCopies = migratedMaterials.Count;

                var correctedPrefabPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string category in Categories) correctedPrefabPaths[category] = new List<string>();
                foreach (PrefabCandidate candidate in prefabCandidates.OrderBy(p => p.category).ThenBy(p => p.sourcePath))
                {
                    string corrected = CreatePrefabCopy(candidate, migratedMaterials, report);
                    if (!string.IsNullOrEmpty(corrected)) correctedPrefabPaths[candidate.category].Add(corrected);
                }
                report.correctedPrefabCopies = correctedPrefabPaths.Sum(p => p.Value.Count);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                ValidateInSeparateScene(correctedPrefabPaths, report, lit);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception exception)
            {
                report.notes.Add("ECHEC: " + exception);
                Debug.LogException(exception);
            }
            finally
            {
                report.completedUtc = DateTime.UtcNow.ToString("O");
                report.originalSceneHashAfter = HashFile(sceneAbsolute);
                report.originalSceneUnchanged = string.Equals(report.originalSceneHashBefore, report.originalSceneHashAfter, StringComparison.OrdinalIgnoreCase);
                EnsureFolder(ValidationRoot);
                string reportPath = ValidationRoot + "/MigrationReport.json";
                File.WriteAllText(AbsolutePath(reportPath), JsonUtility.ToJson(report, true), Encoding.UTF8);
                File.WriteAllText(AbsolutePath(Marker), report.originalSceneUnchanged ? "COMPLETE" : "WARNING_SCENE_HASH_CHANGED", Encoding.UTF8);
                AssetDatabase.ImportAsset(reportPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(Marker, ImportAssetOptions.ForceUpdate);
                AssetDatabase.SaveAssets();
                Debug.Log($"[UserLibrary URP] Terminé: {report.correctedMaterialCopies} matériaux, {report.correctedPrefabCopies} prefabs, {report.impossible.Count} impossibles. Scène source inchangée={report.originalSceneUnchanged}.");
            }
        }

        private static List<PrefabCandidate> CollectPrefabCandidates(IEnumerable<string> prefabGuids, MigrationReport report)
        {
            var candidates = new List<PrefabCandidate>();
            foreach (string guid in prefabGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || IsValidationAsset(path)) continue;
                string category = Classify(path);
                if (string.IsNullOrEmpty(category)) continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                var candidate = new PrefabCandidate { sourcePath = path, category = category, source = prefab };
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null)
                        {
                            candidate.missingSlots++;
                            continue;
                        }
                        if (!IsIncompatible(material)) continue;
                        if (seen.Add(MaterialKey(material))) candidate.incompatible.Add(material);
                    }
                }

                if (candidate.incompatible.Count == 0 && candidate.missingSlots == 0) continue;
                candidates.Add(candidate);
                if (candidate.missingSlots > 0)
                {
                    report.impossible.Add(new AssetRecord
                    {
                        category = category,
                        source = path,
                        note = candidate.missingSlots + " emplacement(s) de matériau manquant(s) dans le prefab source."
                    });
                }
            }
            return candidates;
        }

        private static Material CreateOrUpdateMaterialCopy(Material source, string sourcePath, string category, Shader lit, Shader unlit, MigrationReport report)
        {
            if (source == null) return null;
            SavedProperties saved = ReadSavedProperties(source);
            string originalShader = source.shader != null ? source.shader.name : "<missing>";
            bool useUnlit = ShouldUseUnlit(sourcePath, source.name, originalShader);
            Shader targetShader = useUnlit ? unlit : lit;
            string id = ShortId(MaterialKey(source));
            string destination = Root + "/" + category + "/Materials/" + Safe(source.name) + "_URP_" + id + ".mat";
            Material target = AssetDatabase.LoadAssetAtPath<Material>(destination);
            if (target == null)
            {
                target = new Material(targetShader);
                AssetDatabase.CreateAsset(target, destination);
            }
            else
            {
                target.shader = targetShader;
            }

            target.name = source.name + "_URP";
            target.enableInstancing = true;
            ApplyMappedProperties(source, saved, target, sourcePath, originalShader);
            EditorUtility.SetDirty(target);

            bool approximate = IsSpecializedShader(originalShader);
            var record = new AssetRecord
            {
                category = category,
                source = sourcePath + (AssetDatabase.IsMainAsset(source) ? string.Empty : "::" + source.name),
                destination = destination,
                sourceShader = originalShader,
                destinationShader = targetShader.name,
                note = approximate ? "Shader spécialisé rapproché avec URP standard; textures et paramètres récupérables conservés." : "Conversion URP standard."
            };
            report.materials.Add(record);
            if (approximate) report.approximations.Add(record);

            bool hasRecoverableLook = FindTexture(saved, BaseTextureNames) != null || FindColor(saved, BaseColorNames, Color.clear) != Color.clear;
            if ((source.shader == null || originalShader == "Hidden/InternalErrorShader") && !hasRecoverableLook)
            {
                report.impossible.Add(new AssetRecord
                {
                    category = category,
                    source = record.source,
                    destination = destination,
                    sourceShader = originalShader,
                    destinationShader = targetShader.name,
                    note = "Shader source perdu et aucune texture/couleur d'apparence récupérable. Copie URP neutre créée, reproduction exacte impossible."
                });
            }
            return target;
        }

        private static string CreatePrefabCopy(PrefabCandidate candidate, Dictionary<string, Material> migratedMaterials, MigrationReport report)
        {
            GameObject contents = null;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(candidate.sourcePath);
                int replaced = 0;
                foreach (Renderer renderer in contents.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] materials = renderer.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < materials.Length; i++)
                    {
                        Material sourceMaterial = materials[i];
                        if (sourceMaterial == null || !IsIncompatible(sourceMaterial)) continue;
                        string key = MaterialKey(sourceMaterial) + "|" + candidate.category;
                        if (migratedMaterials.TryGetValue(key, out Material migrated))
                        {
                            materials[i] = migrated;
                            changed = true;
                            replaced++;
                        }
                    }
                    if (changed) renderer.sharedMaterials = materials;
                }

                string guid = AssetDatabase.AssetPathToGUID(candidate.sourcePath);
                string destination = Root + "/" + candidate.category + "/Prefabs/" + Safe(Path.GetFileNameWithoutExtension(candidate.sourcePath)) + "_URP_" + ShortId(guid) + ".prefab";
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(contents, destination);
                if (saved == null) throw new InvalidOperationException("Impossible d'enregistrer le prefab: " + destination);
                report.prefabs.Add(new AssetRecord
                {
                    category = candidate.category,
                    source = candidate.sourcePath,
                    destination = destination,
                    note = replaced + " emplacement(s) de matériau remappé(s)."
                });
                return destination;
            }
            catch (Exception exception)
            {
                report.impossible.Add(new AssetRecord
                {
                    category = candidate.category,
                    source = candidate.sourcePath,
                    note = "Création de copie prefab impossible: " + exception.Message
                });
                return string.Empty;
            }
            finally
            {
                if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private static void ApplyMappedProperties(Material source, SavedProperties saved, Material target, string sourcePath, string shaderName)
        {
            Texture baseMap = FindTexture(saved, BaseTextureNames) ?? FindSemanticTexture(saved, "albedo", "diffuse", "base", "color");
            Texture normal = FindTexture(saved, NormalTextureNames) ?? FindSemanticTexture(saved, "normal", "bump");
            Texture metallic = FindTexture(saved, MetallicTextureNames) ?? FindSemanticTexture(saved, "metal", "metallic", "metalness");
            Texture specular = FindTexture(saved, SpecularTextureNames) ?? FindSemanticTexture(saved, "spec", "specular");
            Texture occlusion = FindTexture(saved, OcclusionTextureNames) ?? FindSemanticTexture(saved, "occlusion", "ambient", "ao");
            Texture emission = FindTexture(saved, EmissionTextureNames) ?? FindSemanticTexture(saved, "emission", "emissive");
            Color baseColor = FindColor(saved, BaseColorNames, Color.white);
            Color emissionColor = FindColor(saved, new[] { "_EmissionColor", "_EmissiveColor" }, Color.black);

            target.SetColor("_BaseColor", baseColor);
            if (baseMap != null)
            {
                target.SetTexture("_BaseMap", baseMap);
                string sourceProperty = FindTextureProperty(saved, baseMap);
                if (!string.IsNullOrEmpty(sourceProperty))
                {
                    if (saved.scales.TryGetValue(sourceProperty, out Vector2 scale)) target.SetTextureScale("_BaseMap", scale);
                    if (saved.offsets.TryGetValue(sourceProperty, out Vector2 offset)) target.SetTextureOffset("_BaseMap", offset);
                }
            }
            else target.SetTexture("_BaseMap", null);

            if (normal != null)
            {
                target.SetTexture("_BumpMap", normal);
                target.SetFloat("_BumpScale", FindFloat(saved, new[] { "_BumpScale", "_NormalScale" }, 1f));
                target.EnableKeyword("_NORMALMAP");
            }
            else
            {
                target.SetTexture("_BumpMap", null);
                target.DisableKeyword("_NORMALMAP");
            }

            if (metallic != null)
            {
                target.SetTexture("_MetallicGlossMap", metallic);
                target.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            else target.SetTexture("_MetallicGlossMap", null);
            if (specular != null && target.HasProperty("_SpecGlossMap"))
            {
                target.SetFloat("_WorkflowMode", 0f);
                target.SetTexture("_SpecGlossMap", specular);
                target.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            target.SetFloat("_Metallic", Mathf.Clamp01(FindFloat(saved, new[] { "_Metallic", "_Metalness" }, 0f)));
            target.SetFloat("_Smoothness", Mathf.Clamp01(FindFloat(saved, new[] { "_Smoothness", "_Glossiness", "_Gloss" }, 0.35f)));

            if (occlusion != null)
            {
                target.SetTexture("_OcclusionMap", occlusion);
                target.SetFloat("_OcclusionStrength", Mathf.Clamp01(FindFloat(saved, new[] { "_OcclusionStrength", "_AOStrength" }, 1f)));
                target.EnableKeyword("_OCCLUSIONMAP");
            }
            else
            {
                target.SetTexture("_OcclusionMap", null);
                target.DisableKeyword("_OCCLUSIONMAP");
            }

            if (emission != null || emissionColor.maxColorComponent > 0.001f)
            {
                target.SetTexture("_EmissionMap", emission);
                target.SetColor("_EmissionColor", emissionColor);
                target.EnableKeyword("_EMISSION");
                target.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            }
            else
            {
                target.SetTexture("_EmissionMap", null);
                target.SetColor("_EmissionColor", Color.black);
                target.DisableKeyword("_EMISSION");
            }

            float cutoff = Mathf.Clamp01(FindFloat(saved, new[] { "_Cutoff", "_AlphaCutoff", "_AlphaClipThreshold" }, 0.5f));
            target.SetFloat("_Cutoff", cutoff);
            bool transparent = IsTransparent(source, saved, shaderName, sourcePath, baseColor);
            bool cutout = !transparent && IsCutout(source, saved, shaderName, sourcePath);
            ConfigureSurface(target, transparent, cutout);
            target.doubleSidedGI = source.doubleSidedGI;
            if (target.HasProperty("_Cull"))
                target.SetFloat("_Cull", FindFloat(saved, new[] { "_Cull", "_CullMode" }, (float)CullMode.Back));
        }

        private static void ConfigureSurface(Material material, bool transparent, bool cutout)
        {
            material.SetFloat("_AlphaClip", cutout ? 1f : 0f);
            if (cutout) material.EnableKeyword("_ALPHATEST_ON"); else material.DisableKeyword("_ALPHATEST_ON");
            if (transparent)
            {
                material.SetFloat("_Surface", 1f);
                material.SetFloat("_Blend", 0f);
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)RenderQueue.Transparent;
            }
            else
            {
                material.SetFloat("_Surface", 0f);
                material.SetFloat("_SrcBlend", (float)BlendMode.One);
                material.SetFloat("_DstBlend", (float)BlendMode.Zero);
                material.SetFloat("_ZWrite", 1f);
                material.SetOverrideTag("RenderType", cutout ? "TransparentCutout" : "Opaque");
                material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = cutout ? (int)RenderQueue.AlphaTest : (int)RenderQueue.Geometry;
            }
        }

        private static void ValidateInSeparateScene(Dictionary<string, List<string>> correctedPrefabPaths, MigrationReport report, Shader lit)
        {
            Scene original = SceneManager.GetActiveScene();
            Scene test = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(test);
            try
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
                GameObject lightObject = new GameObject("Validation_DirectionalLight");
                SceneManager.MoveGameObjectToScene(lightObject, test);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                lightObject.transform.rotation = Quaternion.Euler(45f, -35f, 0f);

                GameObject cameraObject = new GameObject("Validation_Camera");
                SceneManager.MoveGameObjectToScene(cameraObject, test);
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.12f, 0.15f, 0.18f, 1f);
                camera.orthographic = true;

                float categoryOffset = 0f;
                var categoryRoots = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
                foreach (string category in Categories)
                {
                    GameObject categoryRoot = new GameObject(category);
                    SceneManager.MoveGameObjectToScene(categoryRoot, test);
                    categoryRoot.transform.position = new Vector3(categoryOffset, 0f, 0f);
                    categoryRoots[category] = categoryRoot.transform;
                    int index = 0;
                    foreach (string path in correctedPrefabPaths[category])
                    {
                        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (prefab == null) continue;
                        GameObject instance = PrefabUtility.InstantiatePrefab(prefab, test) as GameObject;
                        if (instance == null) continue;
                        instance.name = Path.GetFileNameWithoutExtension(path);
                        instance.transform.SetParent(categoryRoot.transform, false);
                        int col = index % 5;
                        int row = index / 5;
                        instance.transform.localPosition = new Vector3(col * 14f, 0f, row * 14f);
                        index++;
                    }
                    categoryOffset += 250f;
                }

                int totalInvalid = 0;
                int totalMagenta = 0;
                foreach (string category in Categories)
                {
                    categoryRoots.TryGetValue(category, out Transform root);
                    var categoryRecord = new CategoryRecord { category = category };
                    categoryRecord.materials = report.materials.Count(r => r.category == category);
                    categoryRecord.prefabs = correctedPrefabPaths[category].Count;
                    if (root != null)
                    {
                        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                        {
                            foreach (Material material in renderer.sharedMaterials)
                            {
                                if (material == null || material.shader == null || !material.shader.isSupported ||
                                    !material.shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal))
                                    categoryRecord.invalidSlots++;
                            }
                        }
                    }
                    categoryRecord.preview = ValidationRoot + "/Preview_" + category + ".png";
                    categoryRecord.magentaPixels = RenderCategoryPreview(camera, root, categoryRoots, categoryRecord.preview);
                    totalInvalid += categoryRecord.invalidSlots;
                    totalMagenta += categoryRecord.magentaPixels;
                    report.categories.Add(categoryRecord);
                }
                report.validationInvalidMaterialSlots = totalInvalid;
                report.validationMagentaPixels = totalMagenta;
                if (totalInvalid > 0)
                    report.notes.Add(totalInvalid + " emplacement(s) de matériau non URP/manquant(s) restent dans les copies de validation.");
                if (totalMagenta > 0)
                    report.notes.Add(totalMagenta + " pixel(s) magenta pur détecté(s) dans les aperçus; voir les images de validation.");

                foreach (Transform child in test.GetRootGameObjects().Select(go => go.transform)) child.gameObject.SetActive(true);
                EditorSceneManager.SaveScene(test, ValidationRoot + "/UserLibrary_URP_Test.unity");
            }
            finally
            {
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                EditorSceneManager.CloseScene(test, true);
            }
        }

        private static int RenderCategoryPreview(Camera camera, Transform categoryRoot, Dictionary<string, Transform> categoryRoots, string assetPath)
        {
            if (categoryRoot == null || categoryRoot.childCount == 0)
            {
                File.WriteAllText(AbsolutePath(assetPath + ".empty.txt"), "Aucun prefab corrigé dans cette catégorie.");
                return 0;
            }
            foreach (Transform root in categoryRoots.Values)
                if (root != null) root.gameObject.SetActive(root == categoryRoot);

            Renderer[] renderers = categoryRoot.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return 0;
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            float extent = Mathf.Max(bounds.extents.x, bounds.extents.z, bounds.extents.y * 1.4f, 2f);
            camera.orthographicSize = extent * 1.2f;
            camera.transform.position = bounds.center + new Vector3(extent * 1.3f, extent * 0.9f, -extent * 1.3f);
            camera.transform.LookAt(bounds.center + Vector3.up * bounds.extents.y * 0.15f);
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = extent * 10f + 100f;

            const int width = 1280;
            const int height = 720;
            RenderTexture renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            Texture2D image = new Texture2D(width, height, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            camera.targetTexture = renderTexture;
            camera.Render();
            RenderTexture.active = renderTexture;
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply();
            Color32[] pixels = image.GetPixels32();
            int magenta = pixels.Count(p => p.r >= 248 && p.g <= 12 && p.b >= 248);
            File.WriteAllBytes(AbsolutePath(assetPath), image.EncodeToPNG());
            camera.targetTexture = null;
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(renderTexture);
            UnityEngine.Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return magenta;
        }

        private static SavedProperties ReadSavedProperties(Material material)
        {
            var saved = new SavedProperties();
            var serialized = new SerializedObject(material);
            ReadTexEnvs(serialized.FindProperty("m_SavedProperties.m_TexEnvs"), saved);
            ReadColors(serialized.FindProperty("m_SavedProperties.m_Colors"), saved);
            ReadFloats(serialized.FindProperty("m_SavedProperties.m_Floats"), saved);

            if (material.shader != null && material.shader.name != "Hidden/InternalErrorShader")
            {
                int count = ShaderUtil.GetPropertyCount(material.shader);
                for (int i = 0; i < count; i++)
                {
                    string name = ShaderUtil.GetPropertyName(material.shader, i);
                    switch (ShaderUtil.GetPropertyType(material.shader, i))
                    {
                        case ShaderUtil.ShaderPropertyType.TexEnv:
                            if (!saved.textures.ContainsKey(name)) saved.textures[name] = material.GetTexture(name);
                            if (!saved.scales.ContainsKey(name)) saved.scales[name] = material.GetTextureScale(name);
                            if (!saved.offsets.ContainsKey(name)) saved.offsets[name] = material.GetTextureOffset(name);
                            break;
                        case ShaderUtil.ShaderPropertyType.Color:
                        case ShaderUtil.ShaderPropertyType.Vector:
                            if (!saved.colors.ContainsKey(name)) saved.colors[name] = material.GetColor(name);
                            break;
                        case ShaderUtil.ShaderPropertyType.Float:
                        case ShaderUtil.ShaderPropertyType.Range:
                            if (!saved.floats.ContainsKey(name)) saved.floats[name] = material.GetFloat(name);
                            break;
                    }
                }
            }
            return saved;
        }

        private static void ReadTexEnvs(SerializedProperty array, SavedProperties saved)
        {
            if (array == null || !array.isArray) return;
            for (int i = 0; i < array.arraySize; i++)
            {
                SerializedProperty item = array.GetArrayElementAtIndex(i);
                string name = item.FindPropertyRelative("first")?.stringValue;
                SerializedProperty value = item.FindPropertyRelative("second");
                if (string.IsNullOrEmpty(name) || value == null) continue;
                saved.textures[name] = value.FindPropertyRelative("m_Texture")?.objectReferenceValue as Texture;
                saved.scales[name] = value.FindPropertyRelative("m_Scale")?.vector2Value ?? Vector2.one;
                saved.offsets[name] = value.FindPropertyRelative("m_Offset")?.vector2Value ?? Vector2.zero;
            }
        }

        private static void ReadColors(SerializedProperty array, SavedProperties saved)
        {
            if (array == null || !array.isArray) return;
            for (int i = 0; i < array.arraySize; i++)
            {
                SerializedProperty item = array.GetArrayElementAtIndex(i);
                string name = item.FindPropertyRelative("first")?.stringValue;
                SerializedProperty value = item.FindPropertyRelative("second");
                if (!string.IsNullOrEmpty(name) && value != null) saved.colors[name] = value.colorValue;
            }
        }

        private static void ReadFloats(SerializedProperty array, SavedProperties saved)
        {
            if (array == null || !array.isArray) return;
            for (int i = 0; i < array.arraySize; i++)
            {
                SerializedProperty item = array.GetArrayElementAtIndex(i);
                string name = item.FindPropertyRelative("first")?.stringValue;
                SerializedProperty value = item.FindPropertyRelative("second");
                if (!string.IsNullOrEmpty(name) && value != null) saved.floats[name] = value.floatValue;
            }
        }

        private static bool IsIncompatible(Material material)
        {
            if (material == null || material.shader == null || !material.shader.isSupported) return true;
            string shader = material.shader.name;
            if (shader == "Hidden/InternalErrorShader") return true;
            return !shader.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal);
        }

        private static bool IsValidationAsset(string path)
        {
            return path.StartsWith(ValidationRoot, StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith("UserLibraryUrpMigrationRunner.cs", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldUseUnlit(string path, string name, string shader)
        {
            string value = (path + " " + name + " " + shader).ToLowerInvariant();
            return value.Contains("unlit") || value.Contains("sprite") || value.Contains("particle") ||
                   value.Contains("billboard") || value.Contains("icon") || value.Contains("decal");
        }

        private static bool IsSpecializedShader(string shader)
        {
            if (string.IsNullOrEmpty(shader)) return true;
            string value = shader.ToLowerInvariant();
            return value.Contains("water") || value.Contains("glass") || value.Contains("terrain") ||
                   value.Contains("vertex") || value.Contains("triplanar") || value.Contains("decal") ||
                   value.Contains("outline") || value.Contains("foliage") || value.Contains("wind");
        }

        private static bool IsTransparent(Material source, SavedProperties saved, string shader, string path, Color color)
        {
            string value = (shader + " " + path + " " + source.name).ToLowerInvariant();
            float mode = FindFloat(saved, new[] { "_Mode", "_Surface", "_RenderingMode" }, 0f);
            return source.renderQueue >= 3000 || mode >= 2f || color.a < 0.995f ||
                   value.Contains("transparent") || value.Contains("glass") || value.Contains("window");
        }

        private static bool IsCutout(Material source, SavedProperties saved, string shader, string path)
        {
            string value = (shader + " " + path + " " + source.name).ToLowerInvariant();
            return source.renderQueue >= 2450 && source.renderQueue < 3000 ||
                   FindFloat(saved, new[] { "_AlphaClip", "_AlphaTest" }, 0f) > 0.5f ||
                   value.Contains("cutout") || value.Contains("leaf") || value.Contains("foliage") || value.Contains("fence");
        }

        private static Texture FindTexture(SavedProperties saved, IEnumerable<string> names)
        {
            foreach (string name in names)
                if (saved.textures.TryGetValue(name, out Texture texture) && texture != null) return texture;
            return null;
        }

        private static Texture FindSemanticTexture(SavedProperties saved, params string[] terms)
        {
            foreach (KeyValuePair<string, Texture> pair in saved.textures)
            {
                if (pair.Value == null) continue;
                string key = (pair.Key + " " + pair.Value.name).ToLowerInvariant();
                if (terms.Any(term => key.Contains(term))) return pair.Value;
            }
            return null;
        }

        private static string FindTextureProperty(SavedProperties saved, Texture texture)
        {
            foreach (KeyValuePair<string, Texture> pair in saved.textures)
                if (pair.Value == texture) return pair.Key;
            return string.Empty;
        }

        private static Color FindColor(SavedProperties saved, IEnumerable<string> names, Color fallback)
        {
            foreach (string name in names)
                if (saved.colors.TryGetValue(name, out Color color)) return color;
            return fallback;
        }

        private static float FindFloat(SavedProperties saved, IEnumerable<string> names, float fallback)
        {
            foreach (string name in names)
                if (saved.floats.TryGetValue(name, out float value)) return value;
            return fallback;
        }

        private static string Classify(string path)
        {
            string value = path.Replace('\\', '/').ToLowerInvariant();
            if (HasAny(value, "vehicle", "vehicles", "car", "cars", "sedan", "taxi", "police", "truck", "van", "bus", "ukraine", "high matters")) return "Vehicles";
            if (HasAny(value, "traffic light", "trafficlight", "road sign", "roadsign", "/sign", "sign_", "stop sign", "speed limit", "signal")) return "Signs";
            if (HasAny(value, "road", "roads", "street road", "asphalt", "crosswalk", "cross_walk", "junction", "intersection", "pavement")) return "Roads";
            if (HasAny(value, "tree", "bush", "flower", "plant", "grass", "nature", "vegetation", "hedge", "shrub")) return "Nature";
            if (HasAny(value, "bench", "trash", "garbage", "bin", "lamp", "light pole", "streetlight", "street light", "bollard", "barrier", "hydrant", "street furniture", "street_furniture", "fountain")) return "StreetFurniture";
            if (HasAny(value, "shop", "store", "commercial", "cafe", "coffee", "restaurant", "market", "hotel", "office", "factory", "warehouse", "mall")) return "Commercial";
            if (HasAny(value, "building", "buildings", "house", "home", "residential", "architecture", "arabic neoclassical", "versatile studio", "apartment", "villa")) return "Buildings";
            return string.Empty;
        }

        private static bool HasAny(string value, params string[] terms)
        {
            return terms.Any(term => value.Contains(term));
        }

        private static string MaterialKey(Material material)
        {
            if (material == null) return "<null>";
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long localId))
                return guid + ":" + localId;
            return AssetDatabase.GetAssetPath(material) + ":" + material.name;
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown";
            using (SHA1 sha = SHA1.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                return BitConverter.ToString(bytes, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string HashFile(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath)) return string.Empty;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(absolutePath))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unnamed";
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value.Replace(' ', '_').Replace('/', '_').Replace('\\', '_');
        }

        private static string AbsolutePath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return string.Empty;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            string parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) return;
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetPath));
        }
    }
}
