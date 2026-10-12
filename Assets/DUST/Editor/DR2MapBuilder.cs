using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.EditorTools
{
    // 沙漠遺跡二：依 Blender 匯出的 dr2_layout.json 擺放模組 FBX。
    // Blender 改了格局 → 重跑 tools/dr2_build_map.py、重新匯出 JSON → 這裡再按一次 Build。
    public static class DR2MapBuilder
    {
        const string Root = "Assets/DUST/DR2_Map";
        const string MeshDir = Root + "/Meshes";
        const string LayoutPath = Root + "/dr2_layout.json";
        const string HeroMatDir = "Assets/DUST/DR2_HeroTest/Materials";
        const string MapMatDir = Root + "/Materials";

        [System.Serializable]
        class Item
        {
            public string mesh;
            public string name;
            public string marker;
            public float[] pos;
            public float rotZ;
            public string group;
        }

        [System.Serializable]
        class Layout
        {
            public string coords;
            public Item[] items;
        }

        // Blender（Z 朝上、右手）→ Unity（Y 朝上、左手），與 FBX 匯出設定（-Z forward, Y up, bake space transform）一致
        static Vector3 ToUnity(float[] p) => new Vector3(-p[0], p[2], -p[1]);
        static Quaternion YawToUnity(float rotZDeg) => Quaternion.Euler(0f, -rotZDeg, 0f);

        [MenuItem("DUST/DR2/Setup Importers + Materials")]
        public static void SetupImporters()
        {
            Directory.CreateDirectory(MapMatDir);
            var remap = new Dictionary<string, Material>
            {
                { "M_DST_Sandstone_Tile", AssetDatabase.LoadAssetAtPath<Material>(HeroMatDir + "/M_DST_Sandstone_Hero.mat") },
                { "M_DST_Trim_Sandstone", AssetDatabase.LoadAssetAtPath<Material>(HeroMatDir + "/M_DST_Trim_Hero.mat") },
                { "M_DST_Wood_Weathered", AssetDatabase.LoadAssetAtPath<Material>(HeroMatDir + "/M_DST_Wood_Hero.mat") },
                { "M_DST_Terrain_Sand", AssetDatabase.LoadAssetAtPath<Material>(HeroMatDir + "/M_DST_Ground_Sand_Hero.mat") },
                { "M_DST_Metal_Rusty", SimpleLit("M_DST_Metal_Rusty", new Color(0.36f, 0.19f, 0.10f), 0.6f, 0.25f) },
                { "M_DST_Metal_Iron", SimpleLit("M_DST_Metal_Iron", new Color(0.12f, 0.12f, 0.12f), 0.9f, 0.45f) },
                { "M_DST_Clay", SimpleLit("M_DST_Clay", new Color(0.62f, 0.36f, 0.22f), 0f, 0.2f) },
                { "M_DST_Glass_Dirty", SimpleLit("M_DST_Glass_Dirty", new Color(0.05f, 0.07f, 0.07f), 0f, 0.75f) },
            };

            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { MeshDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mi = (ModelImporter)AssetImporter.GetAtPath(path);
                mi.globalScale = 1;
                mi.useFileScale = true;
                mi.importCameras = false;
                mi.importLights = false;
                mi.importAnimation = false;
                mi.animationType = ModelImporterAnimationType.None;
                mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
                // 牆、地板、道具都給碰撞；積沙只是視覺，不給
                mi.addCollider = !Path.GetFileName(path).Contains("Sand_Drift");
                foreach (var kv in remap)
                    if (kv.Value != null)
                        mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
                mi.SaveAndReimport();
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[DR2] importers configured");
        }

        static Material SimpleLit(string name, Color color, float metallic, float smoothness)
        {
            var path = MapMatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("HDRP/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smoothness);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        [MenuItem("DUST/DR2/Build Map In Open Scene")]
        public static GameObject Build()
        {
            var layout = JsonUtility.FromJson<Layout>(File.ReadAllText(LayoutPath));
            var old = GameObject.Find("DR2_Map");
            if (old != null)
                Object.DestroyImmediate(old);

            var root = new GameObject("DR2_Map");
            var groups = new Dictionary<string, Transform>();
            var prefabs = new Dictionary<string, GameObject>();
            int placed = 0, missing = 0;

            foreach (var it in layout.items)
            {
                if (!string.IsNullOrEmpty(it.marker))
                {
                    var mk = new GameObject(it.marker);
                    mk.transform.SetParent(Group(root, groups, "DR2_Map_Markers"), false);
                    mk.transform.localPosition = ToUnity(it.pos);
                    mk.transform.localRotation = YawToUnity(it.rotZ);
                    continue;
                }

                if (!prefabs.TryGetValue(it.mesh, out var prefab))
                {
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MeshDir + "/" + it.mesh + ".fbx");
                    prefabs[it.mesh] = prefab;
                }
                if (prefab == null)
                {
                    missing++;
                    continue;
                }

                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.name = it.mesh;
                go.transform.SetParent(Group(root, groups, it.group), false);
                go.transform.localPosition = ToUnity(it.pos);
                go.transform.localRotation = YawToUnity(it.rotZ);
                GameObjectUtility.SetStaticEditorFlags(go,
                    StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic |
                    StaticEditorFlags.BatchingStatic | StaticEditorFlags.ReflectionProbeStatic);
                placed++;
            }

            Undo.RegisterCreatedObjectUndo(root, "Build DR2 Map");
            Debug.Log($"[DR2] placed {placed} instances, {missing} missing meshes");
            return root;
        }

        static Transform Group(GameObject root, Dictionary<string, Transform> groups, string name)
        {
            if (string.IsNullOrEmpty(name))
                name = "DR2_Map_Misc";
            if (!groups.TryGetValue(name, out var t))
            {
                t = new GameObject(name).transform;
                t.SetParent(root.transform, false);
                groups[name] = t;
            }
            return t;
        }
    }
}
