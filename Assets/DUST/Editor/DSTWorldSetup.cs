using System.Collections.Generic;
using DUST.World;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DUST.EditorTools
{
    // 隨機世界：設定石塊匯入、建立調色盤（地形材質 + 擺設清單）、在場景放一個 World 連線物件。可以重複執行。
    // 石塊來源：DUST/blender/SM_DST_World_Rocks.blend
    public static class DSTWorldSetup
    {
        const string Dir = "Assets/DUST/World";
        const string RockDir = Dir + "/Rocks";
        const string PalettePath = Dir + "/DST_WorldPalette.asset";
        const string Kit = "Assets/DUST/DR2_Map/Meshes/";
        const string SandMat = "Assets/DUST/DR2_HeroTest/Materials/M_DST_Ground_Sand_Hero.mat";
        const string RockMat = "Assets/DUST/DR2_HeroTest/Materials/M_DST_Sandstone_Hero.mat";

        [MenuItem("DUST/World/Setup Random World")]
        public static void Setup()
        {
            var palette = BuildPalette();
            SetupScene(palette);
        }

        static DSTWorldPalette BuildPalette()
        {
            var sandstone = AssetDatabase.LoadAssetAtPath<Material>(RockMat);
            var rocks = new List<GameObject>();
            foreach (var guid in AssetDatabase.FindAssets("t:Model SM_DST_Rock", new[] { RockDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mi = (ModelImporter)AssetImporter.GetAtPath(path);
                mi.addCollider = true;                 // 匯入時就產生碰撞（建置版不用在執行期讀網格）
                mi.importAnimation = false;
                mi.animationType = ModelImporterAnimationType.None;
                mi.importCameras = false;
                mi.importLights = false;
                mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
                if (sandstone) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "M_DST_Sandstone_Hero"), sandstone);
                mi.SaveAndReimport();
                rocks.Add(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            }

            var p = AssetDatabase.LoadAssetAtPath<DSTWorldPalette>(PalettePath);
            if (!p)
            {
                p = ScriptableObject.CreateInstance<DSTWorldPalette>();
                AssetDatabase.CreateAsset(p, PalettePath);
            }
            p.sand = AssetDatabase.LoadAssetAtPath<Material>(SandMat);
            p.rock = sandstone;
            p.rocks = rocks.ToArray();
            p.smallRocks = rocks.FindAll(r => !r.name.Contains("Pillar")).ToArray();
            p.drifts = Models("SM_DST_Prop_Sand_Drift_A", "SM_DST_Prop_Sand_Drift_B", "SM_DST_Prop_Sand_Drift_C");
            p.rubble = Models("SM_DST_Prop_Rubble_Pile_S", "SM_DST_Prop_Rubble_Pile_M", "SM_DST_Prop_Rubble_Pile_L");
            p.pots = Models("SM_DST_Prop_Pot_Clay_S", "SM_DST_Prop_Pot_Clay_L");
            p.wreck = Model("SM_DST_Prop_Car_Wreck");
            p.brokenCrate = Model("SM_DST_Prop_Crate_L_Broken");
            p.wallStraight = Models("SM_DST_Wall_Straight_400x400_A", "SM_DST_Wall_Straight_400x400_B");
            p.wallDamaged = Models("SM_DST_Wall_Straight_400x400_Damaged");
            p.wallWindow = Models("SM_DST_Wall_Window_Arch_M_400");
            p.wallGate = Model("SM_DST_Wall_Gate_DoubleDoor_400");
            p.wallCorner = Model("SM_DST_Wall_Corner_Outer");
            p.wallHalf = Models("SM_DST_Wall_Straight_200x400_A", "SM_DST_Wall_Straight_200x400_B");

            // 聚落
            p.road = RoadMaterial(p.sand);
            p.water = LitMaterial("M_DST_Water", new Color(0.07f, 0.15f, 0.13f), 0.96f, false);
            p.roofCap = Model("SM_DST_Roof_Cap_400x400");
            p.corniceStraight = Model("SM_DST_Trim_Cornice_400");
            p.corniceCorner = Model("SM_DST_Trim_Cornice_Corner_Out");
            p.plinthStraight = Model("SM_DST_Trim_Plinth_400");
            p.plinthCorner = Model("SM_DST_Trim_Plinth_Corner_Out");
            p.barrels = Models("SM_DST_Prop_Barrel_Wood", "SM_DST_Prop_Barrel_Metal_Closed", "SM_DST_Prop_Barrel_Metal_Dented");
            p.crates = Models("SM_DST_Prop_Crate_S", "SM_DST_Prop_Crate_L");
            var remap = new Dictionary<string, Material>
            {
                { "M_DST_Sandstone_Hero", sandstone },
                { "M_DST_Wood_Hero", AssetDatabase.LoadAssetAtPath<Material>(WoodMat) },
                { "M_DST_Clay", AssetDatabase.LoadAssetAtPath<Material>(ClayMat) },
                { "M_DST_Cloth_Red", LitMaterial("M_DST_Cloth_Red", new Color(0.5f, 0.1f, 0.07f), 0.25f, true) },
                { "M_DST_Cloth_Blue", LitMaterial("M_DST_Cloth_Blue", new Color(0.09f, 0.2f, 0.42f), 0.25f, true) },
                { "M_DST_Cloth_Ochre", LitMaterial("M_DST_Cloth_Ochre", new Color(0.62f, 0.42f, 0.14f), 0.25f, false) },
                { "M_DST_Palm_Trunk", LitMaterial("M_DST_Palm_Trunk", new Color(0.28f, 0.2f, 0.13f), 0.15f, false) },
                { "M_DST_Palm_Leaf", LitMaterial("M_DST_Palm_Leaf", new Color(0.2f, 0.29f, 0.1f), 0.35f, true) },
                { "M_DST_Palm_Date", LitMaterial("M_DST_Palm_Date", new Color(0.5f, 0.25f, 0.05f), 0.5f, false) },
            };
            p.well = SettlementModel("SM_DST_Settle_Well", remap);
            p.stalls = new[] { SettlementModel("SM_DST_Settle_Stall_A", remap), SettlementModel("SM_DST_Settle_Stall_B", remap) };
            p.palms = new[] { SettlementModel("SM_DST_Settle_Palm_A", remap), SettlementModel("SM_DST_Settle_Palm_B", remap) };
            EditorUtility.SetDirty(p);
            AssetDatabase.SaveAssets();
            Debug.Log($"[DUST] World palette: {rocks.Count} rocks");
            return p;
        }

        const string SettleDir = Dir + "/Settlement";
        const string MatDir = Dir + "/Materials";
        const string WoodMat = "Assets/DUST/DR2_HeroTest/Materials/M_DST_Wood_Hero.mat";
        const string ClayMat = "Assets/DUST/DR2_Map/Materials/M_DST_Clay.mat";

        static GameObject SettlementModel(string name, Dictionary<string, Material> remap)
        {
            string path = SettleDir + "/" + name + ".fbx";
            var mi = (ModelImporter)AssetImporter.GetAtPath(path);
            if (mi == null) { Debug.LogWarning("[DUST] 找不到 " + path); return null; }
            mi.addCollider = true;
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.importCameras = false;
            mi.importLights = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            foreach (var kv in remap)
                if (kv.Value) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            mi.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        // 單色的 HDRP Lit 材質（布、椰棗樹、水）；布和葉子是單面網格 → 雙面顯示
        static Material LitMaterial(string name, Color color, float smoothness, bool doubleSided)
        {
            if (!AssetDatabase.IsValidFolder(MatDir)) AssetDatabase.CreateFolder(Dir, "Materials");
            string path = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m)
            {
                m = new Material(Shader.Find("HDRP/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_DoubleSidedEnable", doubleSided ? 1f : 0f);
            m.SetFloat("_DoubleSidedNormalMode", 1f);   // 背面法線鏡射
            UnityEngine.Rendering.HighDefinition.HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        // 街道：被踩實的土。用全新的 HDRP Lit，只借沙地的貼圖與世界座標投影
        // （整個複製沙地材質會連視差位移 / 深度偏移一起帶過來，街道看起來像凹下去的坑）
        static Material RoadMaterial(Material sand)
        {
            if (!AssetDatabase.IsValidFolder(MatDir)) AssetDatabase.CreateFolder(Dir, "Materials");
            string path = MatDir + "/M_DST_Ground_Road.mat";
            var fresh = new Material(Shader.Find("HDRP/Lit"));
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m)
            {
                m = new Material(fresh);
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = fresh.shader;
            m.CopyPropertiesFromMaterial(fresh);
            m.shaderKeywords = new string[0];
            foreach (var tex in new[] { "_BaseColorMap", "_NormalMap", "_MaskMap" })
                m.SetTexture(tex, sand.GetTexture(tex));
            foreach (var f in new[] { "_UVBase", "_TexWorldScale", "_SmoothnessRemapMin", "_SmoothnessRemapMax", "_AORemapMin", "_AORemapMax" })
                if (sand.HasProperty(f)) m.SetFloat(f, sand.GetFloat(f));
            m.SetVector("_UVMappingMask", sand.GetVector("_UVMappingMask"));
            m.SetColor("_BaseColor", new Color(0.95f, 0.87f, 0.78f));
            m.SetFloat("_NormalScale", 0.6f);
            m.SetFloat("_DoubleSidedEnable", 1f);
            UnityEngine.Rendering.HighDefinition.HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            Object.DestroyImmediate(fresh);
            return m;
        }

        static GameObject Model(string name)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(Kit + name + ".fbx");
            if (!go) Debug.LogWarning("[DUST] 找不到模組 " + name);
            return go;
        }

        static GameObject[] Models(params string[] names)
        {
            var list = new List<GameObject>();
            foreach (var n in names) { var m = Model(n); if (m) list.Add(m); }
            return list.ToArray();
        }

        static void SetupScene(DSTWorldPalette palette)
        {
            var go = GameObject.Find("World");
            if (!go) go = new GameObject("World");
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var no = Ensure<NetworkObject>(go);
            no.AutoObjectParentSync = false;
            var w = Ensure<DSTWorld>(go);
            Ensure<DSTWorldNavMesh>(go);
            w.palette = palette;
            w.dr2Map = GameObject.Find("DR2_Map");
            var vol = GameObject.Find("Volume_Global");
            w.fogVolume = vol ? vol.GetComponent<Volume>() : null;
            EditorUtility.SetDirty(w);
            EditorSceneManager.MarkSceneDirty(go.scene);
            EditorSceneManager.SaveScene(go.scene);
            Debug.Log("[DUST] World object ready");
        }

        // 編輯器裡 GetComponent 找不到時會回傳假 null，不能用 ??
        static T Ensure<T>(GameObject go) where T : Component => go.TryGetComponent<T>(out var c) ? c : go.AddComponent<T>();
    }
}
