using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Boulangerie3D.EditorTools
{
    internal static class PrepareMobileUrpMigration
    {
        const string Root = "Assets/URP_Migrated";
        const string Marker = Root + "/MigrationComplete.txt";
        static readonly string[] Packs = { "Assets/Arabic Neoclassical", "Assets/Versatile Studio Assets", "Assets/asset_free_Ukraine_cars", "Assets/High Matters" };
        static readonly string[] Prefabs = {
            "Assets/asset_free_Ukraine_cars/prefabs_With_Colliders/cars_colliiders/car/car_5/car_5_blue.prefab",
            "Assets/asset_free_Ukraine_cars/prefabs_With_Colliders/cars_colliiders/car/car_6/car_6_black.prefab",
            "Assets/asset_free_Ukraine_cars/prefabs_With_Colliders/cars_colliiders/van/van_1/van_1_black.prefab",
            "Assets/High Matters/Free American Sedans/Prefabs/Car_stylized.prefab",
            "Assets/High Matters/Free American Sedans/Prefabs/Police_stylized.prefab",
            "Assets/High Matters/Free American Sedans/Prefabs/Taxi_stylized.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/small_house_1.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/small_house_2.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/small_house_3.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/mid_house_1.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/mid_house_1_2.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/mid_house_2.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/mid_house_3.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/mid_house_4.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/mid_house_4_2.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/mid_house_5.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/office_building_1.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/office_building_2.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/office_building_3.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/office_building_4.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/factory_building_small.prefab",
            "Assets/Versatile Studio Assets/Demo City By Versatile Studio/Prefabs/factory_building_big.prefab"
        };

        [MenuItem("Tools/Boulangerie3D/Prepare Mobile URP Migration %#u")]
        static void Run()
        {
            if (File.Exists(Marker) || EditorApplication.isPlayingOrWillChangePlaymode) return;
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) { Debug.LogError("[URP Migration] URP/Lit unavailable."); return; }
            Folder(Root + "/Materials"); Folder(Root + "/Prefabs/Vehicles"); Folder(Root + "/Prefabs/Buildings");
            var map = new Dictionary<Material, Material>();
            foreach (string pack in Packs)
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { pack }))
            {
                string srcPath = AssetDatabase.GUIDToAssetPath(guid);
                Material src = AssetDatabase.LoadAssetAtPath<Material>(srcPath);
                if (!src) continue;
                string folder = Root + "/Materials/" + Safe(pack.Substring(7)); Folder(folder);
                Material dst = Convert(src, srcPath, lit);
                string dstPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Safe(src.name) + "_URP.mat");
                AssetDatabase.CreateAsset(dst, dstPath); map[src] = dst;
            }
            var report = new List<string> { "Sources unchanged. SampleScene unchanged.", "Materials copied: " + map.Count, "Selected prefabs:" };
            foreach (string srcPath in CollectPrefabs())
            {
                if (!AssetDatabase.LoadAssetAtPath<GameObject>(srcPath)) { report.Add("MISSING | " + srcPath); continue; }
                GameObject root = PrefabUtility.LoadPrefabContents(srcPath);
                try {
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) {
                        var mats = r.sharedMaterials; bool changed = false;
                        for (int i=0;i<mats.Length;i++) if (mats[i] && map.TryGetValue(mats[i], out Material m)) { mats[i]=m; changed=true; }
                        if (changed) r.sharedMaterials=mats;
                    }
                    string type = srcPath.Contains("Versatile Studio") ? "Buildings" : "Vehicles";
                    string dstPath = AssetDatabase.GenerateUniqueAssetPath(Root + "/Prefabs/" + type + "/" + Safe(Path.GetFileNameWithoutExtension(srcPath)) + "_URP.prefab");
                    PrefabUtility.SaveAsPrefabAsset(root, dstPath);
                    report.Add(dstPath + " | triangles=" + Triangles(root));
                } finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            ValidateInAdditiveTestScene(report);
            File.WriteAllLines(Marker, report); AssetDatabase.ImportAsset(Marker); AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            Debug.Log("[URP Migration] Complete: " + map.Count + " materials.");
        }

        static IEnumerable<string> CollectPrefabs()
        {
            return Packs.SelectMany(p => AssetDatabase.FindAssets("t:Prefab", new[] { p }))
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !p.EndsWith("demo_city_by_versatile_studio.prefab", StringComparison.OrdinalIgnoreCase))
                .Distinct().OrderBy(p => p);
        }

        static void ValidateInAdditiveTestScene(List<string> report)
        {
            Folder(Root + "/Tests");
            Scene test = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            string[] candidates = AssetDatabase.FindAssets("t:Prefab", new[] { Root + "/Prefabs" })
                .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p).Take(16).ToArray();
            int invalid = 0; float x = 0;
            foreach (string path in candidates)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, test);
                instance.transform.position = new Vector3(x, 0, 0); x += 8;
                foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
                foreach (Material material in renderer.sharedMaterials)
                    if (!material || !material.shader || material.shader.name == "Hidden/InternalErrorShader" ||
                        !material.shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal))
                        invalid++;
            }
            EditorSceneManager.SaveScene(test, Root + "/Tests/URP_Migration_Test.unity");
            EditorSceneManager.CloseScene(test, true);
            report.Add("Test scene instances: " + candidates.Length);
            report.Add("Invalid/non-URP renderer material slots: " + invalid);
            if (invalid > 0) Debug.LogError("[URP Migration] Validation found " + invalid + " invalid material slots.");
        }

        static Material Convert(Material s, string path, Shader lit)
        {
            Color color = C(s,"_BaseColor",C(s,"_Color",Color.white));
            Texture baseMap=T(s,"_BaseMap")??T(s,"_MainTex"), normal=T(s,"_BumpMap"), metal=T(s,"_MetallicGlossMap"), spec=T(s,"_SpecGlossMap"), occ=T(s,"_OcclusionMap"), emi=T(s,"_EmissionMap");
            Color emiColor=C(s,"_EmissionColor",Color.black);
            Vector2 scale=s.HasProperty("_BaseMap")?s.GetTextureScale("_BaseMap"):s.HasProperty("_MainTex")?s.GetTextureScale("_MainTex"):Vector2.one;
            Vector2 offset=s.HasProperty("_BaseMap")?s.GetTextureOffset("_BaseMap"):s.HasProperty("_MainTex")?s.GetTextureOffset("_MainTex"):Vector2.zero;
            if(path.EndsWith("/Dirt_Mat.mat",StringComparison.OrdinalIgnoreCase)) Arabic("Assets/Arabic Neoclassical/Street Road/Materials/Dirty Version/","T_Road_Dirt_V03",ref baseMap,ref normal,ref metal);
            if(path.EndsWith("/M_V03.mat",StringComparison.OrdinalIgnoreCase)) Arabic("Assets/Arabic Neoclassical/Street Road/Materials/V3/","T_Road_V03",ref baseMap,ref normal,ref metal);
            var d=new Material(lit){name=s.name+"_URP",enableInstancing=true}; d.SetColor("_BaseColor",color);
            if(baseMap){d.SetTexture("_BaseMap",baseMap);d.SetTextureScale("_BaseMap",scale);d.SetTextureOffset("_BaseMap",offset);}
            if(normal){d.SetTexture("_BumpMap",normal);d.EnableKeyword("_NORMALMAP");}
            if(metal){d.SetTexture("_MetallicGlossMap",metal);d.EnableKeyword("_METALLICSPECGLOSSMAP");}
            if(spec){d.SetFloat("_WorkflowMode",0);d.SetTexture("_SpecGlossMap",spec);d.EnableKeyword("_METALLICSPECGLOSSMAP");}
            if(occ)d.SetTexture("_OcclusionMap",occ);
            if(emi||emiColor.maxColorComponent>.001f){if(emi)d.SetTexture("_EmissionMap",emi);d.SetColor("_EmissionColor",emiColor);d.EnableKeyword("_EMISSION");}
            d.SetFloat("_Metallic",F(s,"_Metallic",0)); d.SetFloat("_Smoothness",F(s,"_Smoothness",F(s,"_Glossiness",.35f))); d.SetFloat("_Cutoff",F(s,"_Cutoff",.5f));
            return d;
        }
        static void Arabic(string f,string n,ref Texture b,ref Texture normal,ref Texture metal){b=AssetDatabase.LoadAssetAtPath<Texture>(f+n+"_AlbedoTransparency.png");normal=AssetDatabase.LoadAssetAtPath<Texture>(f+n+"_Normal.png");metal=AssetDatabase.LoadAssetAtPath<Texture>(f+n+"_MetallicSmoothness.png");}
        static int Triangles(GameObject root){var ms=new HashSet<Mesh>();foreach(var f in root.GetComponentsInChildren<MeshFilter>(true))if(f.sharedMesh)ms.Add(f.sharedMesh);foreach(var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))if(r.sharedMesh)ms.Add(r.sharedMesh);long n=0;foreach(var m in ms)for(int i=0;i<m.subMeshCount;i++)n+=(long)m.GetIndexCount(i);return(int)(n/3);}
        static Texture T(Material m,string p)=>m.HasProperty(p)?m.GetTexture(p):null;
        static Color C(Material m,string p,Color d)=>m.HasProperty(p)?m.GetColor(p):d;
        static float F(Material m,string p,float d)=>m.HasProperty(p)?m.GetFloat(p):d;
        static string Safe(string s){foreach(char c in Path.GetInvalidFileNameChars())s=s.Replace(c,'_');return s.Replace(' ','_');}
        static void Folder(string path){if(AssetDatabase.IsValidFolder(path))return;string parent=Path.GetDirectoryName(path).Replace('\\','/');Folder(parent);AssetDatabase.CreateFolder(parent,Path.GetFileName(path));}
    }
}
