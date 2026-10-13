using System.Collections.Generic;
using System.Linq;
using DUST.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DUST.EditorTools
{
    // 隨機世界的擺設全部可以撞倒 / 打碎：
    //   1. Assets/DUST/World/Debris/*_Frac.fbx（Tools/Blender/fracture.py 預先切好的碎塊）→ 碎片 Prefab（凸包碰撞體、DSTDebris）
    //   2. DST_PropCatalog：每種擺設是輕的（會倒）、重的（撞夠快才碎）還是會被撞倒的樹
    //   3. 調色盤指到目錄，DSTWorld 物件上加 DSTPropSync（連線同步誰碎了、被推到哪）
    public static class DSTWorldPropSetup
    {
        const string DebrisDir = "Assets/DUST/World/Debris";
        const string CatalogPath = "Assets/DUST/World/DST_PropCatalog.asset";

        // 名字、模式、質量（kg）、碎裂速度（m/s）、撞的東西至少多重才撞得壞、碎片密度、膠囊
        static readonly (string name, DSTPropCatalog.Mode mode, float mass, float breakSpeed, float minHitter, float density, bool capsule)[] Table =
        {
            ("SM_DST_Rock_Boulder_A", DSTPropCatalog.Mode.Sturdy, 0, 11f, 300f, 2300f, false),
            ("SM_DST_Rock_Boulder_B", DSTPropCatalog.Mode.Sturdy, 0, 11f, 300f, 2300f, false),
            ("SM_DST_Rock_Boulder_C", DSTPropCatalog.Mode.Sturdy, 0, 11f, 300f, 2300f, false),
            ("SM_DST_Rock_Slab_A", DSTPropCatalog.Mode.Sturdy, 0, 10f, 300f, 2300f, false),
            ("SM_DST_Rock_Pillar_A", DSTPropCatalog.Mode.Sturdy, 0, 8f, 300f, 2300f, false),
            ("SM_DST_Prop_Rubble_Pile_S", DSTPropCatalog.Mode.Sturdy, 0, 5f, 15f, 1800f, false),
            ("SM_DST_Prop_Rubble_Pile_M", DSTPropCatalog.Mode.Sturdy, 0, 6f, 200f, 1800f, false),
            ("SM_DST_Prop_Rubble_Pile_L", DSTPropCatalog.Mode.Sturdy, 0, 7f, 250f, 1800f, false),
            ("SM_DST_Wall_Straight_400x400_A", DSTPropCatalog.Mode.Sturdy, 0, 7f, 250f, 1800f, false),
            ("SM_DST_Wall_Straight_400x400_B", DSTPropCatalog.Mode.Sturdy, 0, 7f, 250f, 1800f, false),
            ("SM_DST_Wall_Straight_400x400_Damaged", DSTPropCatalog.Mode.Sturdy, 0, 6f, 250f, 1800f, false),
            ("SM_DST_Wall_Window_Arch_M_400", DSTPropCatalog.Mode.Sturdy, 0, 6f, 250f, 1800f, false),
            ("SM_DST_Wall_Gate_DoubleDoor_400", DSTPropCatalog.Mode.Sturdy, 0, 6f, 250f, 1200f, false),
            ("SM_DST_Wall_Corner_Outer", DSTPropCatalog.Mode.Sturdy, 0, 6f, 250f, 1800f, false),
            ("SM_DST_Wall_Straight_200x400_A", DSTPropCatalog.Mode.Sturdy, 0, 6f, 250f, 1800f, false),
            ("SM_DST_Wall_Straight_200x400_B", DSTPropCatalog.Mode.Sturdy, 0, 6f, 250f, 1800f, false),
            ("SM_DST_Roof_Cap_400x400", DSTPropCatalog.Mode.Sturdy, 0, 6f, 250f, 1500f, false),
            ("SM_DST_Trim_Cornice_400", DSTPropCatalog.Mode.Sturdy, 0, 5f, 15f, 1800f, false),
            ("SM_DST_Trim_Cornice_Corner_Out", DSTPropCatalog.Mode.Sturdy, 0, 5f, 15f, 1800f, false),
            ("SM_DST_Trim_Plinth_400", DSTPropCatalog.Mode.Sturdy, 0, 5f, 15f, 1800f, false),
            ("SM_DST_Trim_Plinth_Corner_Out", DSTPropCatalog.Mode.Sturdy, 0, 5f, 15f, 1800f, false),
            ("SM_DST_Settle_Well", DSTPropCatalog.Mode.Sturdy, 0, 9f, 300f, 2000f, false),
            ("SM_DST_Settle_Stall_A", DSTPropCatalog.Mode.Sturdy, 0, 4f, 15f, 500f, false),
            ("SM_DST_Settle_Stall_B", DSTPropCatalog.Mode.Sturdy, 0, 4f, 15f, 500f, false),
            ("SM_DST_Settle_Palm_A", DSTPropCatalog.Mode.Topple, 350f, 0f, 250f, 0f, true),
            ("SM_DST_Settle_Palm_B", DSTPropCatalog.Mode.Topple, 350f, 0f, 250f, 0f, true),
            // 輕的：Δv 超過 breakSpeed 就碎（走路踢倒、衝刺撞碎、從高處摔下也會碎）
            ("SM_DST_Prop_Pot_Clay_S", DSTPropCatalog.Mode.Loose, 5f, 5f, 0f, 1800f, true),
            ("SM_DST_Prop_Pot_Clay_L", DSTPropCatalog.Mode.Loose, 14f, 5f, 0f, 1800f, true),
            ("SM_DST_Prop_Crate_L_Broken", DSTPropCatalog.Mode.Loose, 20f, 6f, 0f, 550f, false),
            ("SM_DST_Prop_Crate_S", DSTPropCatalog.Mode.Loose, 8f, 6f, 0f, 550f, false),
            ("SM_DST_Prop_Crate_L", DSTPropCatalog.Mode.Loose, 25f, 7f, 0f, 550f, false),
            ("SM_DST_Prop_Barrel_Wood", DSTPropCatalog.Mode.Loose, 30f, 8f, 0f, 600f, true),
            ("SM_DST_Prop_Barrel_Metal_Closed", DSTPropCatalog.Mode.Loose, 40f, 0f, 0f, 0f, true),
            ("SM_DST_Prop_Barrel_Metal_Dented", DSTPropCatalog.Mode.Loose, 40f, 0f, 0f, 0f, true),
            ("SM_DST_Prop_Car_Wreck", DSTPropCatalog.Mode.Loose, 900f, 0f, 0f, 0f, false),
        };

        [MenuItem("DUST/World/Setup Interactive Props")]
        public static void Setup()
        {
            var pal = AssetDatabase.LoadAssetAtPath<DSTWorldPalette>("Assets/DUST/World/DST_WorldPalette.asset");
            if (!pal) { Debug.LogError("[DUST] 找不到 DST_WorldPalette"); return; }
            var sources = new Dictionary<string, GameObject>();
            void Add(GameObject g) { if (g) sources[g.name] = g; }
            void AddAll(GameObject[] a) { if (a != null) foreach (var g in a) Add(g); }
            AddAll(pal.rocks); AddAll(pal.smallRocks); AddAll(pal.rubble); AddAll(pal.pots); Add(pal.wreck); Add(pal.brokenCrate);
            AddAll(pal.wallStraight); AddAll(pal.wallDamaged); AddAll(pal.wallWindow); Add(pal.wallGate); Add(pal.wallCorner); AddAll(pal.wallHalf);
            Add(pal.roofCap); Add(pal.corniceStraight); Add(pal.corniceCorner); Add(pal.plinthStraight); Add(pal.plinthCorner);
            Add(pal.well); AddAll(pal.stalls); AddAll(pal.palms); AddAll(pal.barrels); AddAll(pal.crates);

            var cat = AssetDatabase.LoadAssetAtPath<DSTPropCatalog>(CatalogPath);
            if (!cat)
            {
                cat = ScriptableObject.CreateInstance<DSTPropCatalog>();
                AssetDatabase.CreateAsset(cat, CatalogPath);
            }
            cat.entries.Clear();
            var log = new System.Text.StringBuilder();
            foreach (var row in Table)
            {
                if (!sources.TryGetValue(row.name, out var src)) { log.AppendLine($"  (調色盤裡沒有 {row.name})"); continue; }
                var b = Bounds(src, out _);
                var e = new DSTPropCatalog.Entry
                {
                    name = row.name, mode = row.mode, breakSpeed = row.breakSpeed, minHitterMass = row.minHitter,
                    debrisDensity = row.density, capsule = row.capsule,
                    refVolume = Mathf.Max(0.001f, b.size.x * b.size.y * b.size.z),
                    sourceScale = src.transform.localScale,
                };
                e.mass = row.mass > 0f ? row.mass : e.refVolume * row.density * 0.5f;
                if (row.density > 0f) e.debris = DebrisPrefab(src, row.density, log);
                cat.entries.Add(e);
            }
            EditorUtility.SetDirty(cat);
            pal.props = cat;
            EditorUtility.SetDirty(pal);

            // 載具都要有 DSTPhysicsClaimer（叫醒前方的擺設、撞碎重的）
            foreach (var path in new[] { DSTVehicleSetup.PrefabPath, DSTMeBikeSetup.PrefabPath, DSTDroneSetup.PrefabPath })
            {
                var root = PrefabUtility.LoadPrefabContents(path);
                if (!root.GetComponent<DUST.Vehicles.DSTPhysicsClaimer>())
                {
                    root.AddComponent<DUST.Vehicles.DSTPhysicsClaimer>();
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    log.AppendLine($"  {path} 加上 DSTPhysicsClaimer");
                }
                PrefabUtility.UnloadPrefabContents(root);
            }

            // 每個有 DSTWorld 的場景：同一個物件加上 DSTPropSync
            var world = Object.FindAnyObjectByType<DSTWorld>();
            if (world)
            {
                if (!world.GetComponent<DSTPropSync>()) world.gameObject.AddComponent<DSTPropSync>();
                EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
                EditorSceneManager.SaveScene(world.gameObject.scene);
            }
            else log.AppendLine("  場景裡沒有 DSTWorld，DSTPropSync 沒有加");
            AssetDatabase.SaveAssets();
            Debug.Log($"[DUST] Interactive props: {cat.entries.Count} kinds\n{log}");
        }

        static Bounds Bounds(GameObject prefab, out GameObject inst)
        {
            inst = (GameObject)Object.Instantiate(prefab);
            inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var rs = inst.GetComponentsInChildren<Renderer>();
            var b = rs.Length > 0 ? rs[0].bounds : new Bounds();
            foreach (var r in rs) b.Encapsulate(r.bounds);
            Object.DestroyImmediate(inst);
            inst = null;
            return b;
        }

        // 碎片 Prefab：外層是空物件（Instantiate 時給位置和旋轉），裡面是切好的 FBX；擺到和原模型一樣的位置
        static GameObject DebrisPrefab(GameObject src, float density, System.Text.StringBuilder log)
        {
            string fbx = $"{DebrisDir}/{src.name}_Frac.fbx";
            var mi = AssetImporter.GetAtPath(fbx) as ModelImporter;
            if (!mi) { log.AppendLine($"  沒有碎片 {fbx}"); return null; }
            if (!mi.isReadable || mi.addCollider || mi.importAnimation || mi.materialImportMode != ModelImporterMaterialImportMode.None)
            {
                mi.isReadable = true;                 // 建置版執行期要能讀網格
                mi.addCollider = false;
                mi.importAnimation = false;
                mi.animationType = ModelImporterAnimationType.None;
                mi.importCameras = false;
                mi.importLights = false;
                mi.materialImportMode = ModelImporterMaterialImportMode.None;   // 材質直接用原模型的
                mi.SaveAndReimport();
            }
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            var srcMats = src.GetComponentInChildren<Renderer>().sharedMaterials;

            var root = new GameObject(src.name + "_Debris");
            var body = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            PrefabUtility.UnpackPrefabInstance(body, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            foreach (var r in body.GetComponentsInChildren<MeshRenderer>())
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = srcMats[Mathf.Min(i, srcMats.Length - 1)];
                r.sharedMaterials = mats;
            }

            // 對齊：碎片試幾種軸向，挑和原模型最像的。只比不旋轉時的包圍盒分不出轉 180°（石頭、罐子），
            // 所以兩邊一起轉幾個歪的角度（Instantiate 時給的旋轉）再比，等於比較外形輪廓
            var srcInst = (GameObject)Object.Instantiate(src);
            var want = RendBounds(srcInst);
            var baseRot = body.transform.localRotation;
            var baseScale = body.transform.localScale;
            var tries = new List<Quaternion> { baseRot };
            foreach (float ax in new[] { 0f, -90f, 90f })
            foreach (float ay in new[] { 0f, 90f, 180f, 270f })
                tries.Add(Quaternion.Euler(ax, ay, 0f));
            float[] yaws = { 0f, 23f, 41f, 67f, 113f, 151f };
            float best = float.MaxValue; Quaternion bestRot = baseRot; float bestK = 1f;
            foreach (var q in tries)
            {
                body.transform.localRotation = q;
                body.transform.localScale = baseScale;
                root.transform.rotation = Quaternion.identity;
                body.transform.localPosition = Vector3.zero;
                var g0 = RendBounds(body);
                float k = want.size.magnitude / Mathf.Max(1e-4f, g0.size.magnitude);
                body.transform.localScale = baseScale * k;
                body.transform.localPosition = Vector3.zero;
                body.transform.localPosition = want.center - RendBounds(body).center;
                float err = 0f;
                foreach (float y in yaws)
                {
                    var r = Quaternion.Euler(0f, y, 0f);
                    srcInst.transform.rotation = r;
                    root.transform.rotation = r;
                    var a = RendBounds(srcInst); var b = RendBounds(body);
                    err += ((a.center - b.center).magnitude + (a.size - b.size).magnitude) / want.size.magnitude;
                }
                srcInst.transform.rotation = Quaternion.identity;
                root.transform.rotation = Quaternion.identity;
                if (err < best) { best = err; bestRot = q; bestK = k; }
            }
            Object.DestroyImmediate(srcInst);
            body.transform.localRotation = bestRot;
            body.transform.localScale = baseScale * bestK;
            body.transform.localPosition = Vector3.zero;
            body.transform.localPosition = want.center - RendBounds(body).center;
            best /= yaws.Length;
            if (best > 0.03f || Mathf.Abs(bestK - 1f) > 0.02f) log.AppendLine($"  {src.name}: 對齊誤差 {best:0.000}，縮放 {bestK:0.###}");

            var d = root.AddComponent<DSTDebris>();
            d.density = density;
            d.lifetime = 12f;
            foreach (var mf in body.GetComponentsInChildren<MeshFilter>())
            {
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = true;
            }
            string path = $"{DebrisDir}/PF_{src.name}_Debris.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // 用頂點算的緊包圍盒（Renderer.bounds 是把網格的包圍盒轉過去，一轉就變大，整塊和碎塊比不準）
        static Bounds RendBounds(GameObject go)
        {
            bool first = true;
            var b = new Bounds();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                if (!mf.sharedMesh) continue;
                var m = mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices)
                {
                    var p = m.MultiplyPoint3x4(v);
                    if (first) { b = new Bounds(p, Vector3.zero); first = false; }
                    else b.Encapsulate(p);
                }
            }
            return b;
        }
    }
}
