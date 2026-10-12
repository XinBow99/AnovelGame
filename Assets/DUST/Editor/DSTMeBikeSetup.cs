using DUST.Vehicles;
using DUST.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DUST.EditorTools
{
    // MeBike（台灣公共自行車風格，不放任何商標）：組 Prefab、做站點 Prefab、在遺跡二出生點旁放一站、
    // 交給隨機世界在營地放一站，最後重跑連線設定（註冊連線 Prefab）。可以重複執行。
    // Blender 原始檔：DUST/blender/SM_DST_Vehicle_MeBike.blend（實車尺寸，匯出 ×0.5）
    public static class DSTMeBikeSetup
    {
        const string Dir = "Assets/DUST/Vehicles/MeBike";
        const string Fbx = Dir + "/SM_DST_Vehicle_MeBike.fbx";
        const string DockFbx = Dir + "/SM_DST_Prop_MeBike_Dock.fbx";
        const string SignFbx = Dir + "/SM_DST_Prop_MeBike_Sign.fbx";
        const string MatDir = Dir + "/Materials";
        public const string PrefabPath = Dir + "/PF_Vehicle_MeBike.prefab";
        public const string StationPath = Dir + "/PF_MeBike_Station.prefab";
        const int Docks = 5;
        const float DockSpacing = 0.6f;

        [MenuItem("DUST/Vehicles/Setup MeBike")]
        public static void Setup()
        {
            SetupImporters();
            var bike = BuildBike();
            var station = BuildStation(bike);
            PlaceInScene(station);
            DSTNetSetup.SetupAll();   // 註冊 MeBike 連線 Prefab、補上 NetworkObject 等
        }

        static void SetupImporters()
        {
            var mats = new[]
            {
                DSTVehicleSetup.LitAt(MatDir, "M_DST_MeBike_Frame", new Color(0.86f, 0.86f, 0.84f), 0f, 0.6f),
                DSTVehicleSetup.LitAt(MatDir, "M_DST_MeBike_Accent", new Color(0.95f, 0.55f, 0.06f), 0f, 0.55f),
                DSTVehicleSetup.LitAt(MatDir, "M_DST_MeBike_Dark", new Color(0.035f, 0.035f, 0.04f), 0f, 0.25f),
                DSTVehicleSetup.LitAt(MatDir, "M_DST_MeBike_Metal", new Color(0.62f, 0.62f, 0.64f), 1f, 0.7f),
                DSTVehicleSetup.LitAt(MatDir, "M_DST_MeBike_Screen", new Color(0.02f, 0.05f, 0.06f), 0f, 0.92f),
                DSTVehicleSetup.LitAt(MatDir, "M_DST_MeBike_Light", new Color(0.75f, 0.04f, 0.02f), 0f, 0.7f),
            };
            var body = AssetDatabase.LoadAssetAtPath<Material>("Assets/DUST/Player/M_PlayerBody.mat");
            foreach (var (path, collider) in new[] { (Fbx, false), (DockFbx, true), (SignFbx, true) })
            {
                var mi = (ModelImporter)AssetImporter.GetAtPath(path);
                mi.globalScale = 1;
                mi.useFileScale = true;
                mi.addCollider = collider;
                mi.importCameras = false;
                mi.importLights = false;
                mi.importAnimation = false;
                mi.animationType = ModelImporterAnimationType.None;
                mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
                foreach (var m in mats) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), m.name), m);
                if (body) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "M_PlayerBody"), body);
                mi.SaveAndReimport();
            }
        }

        static GameObject BuildBike()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = "PF_Vehicle_MeBike";
            var t = go.transform;

            // 外觀放進 Visual（過彎傾斜、上下坡俯仰都轉這個），前輪跟著龍頭轉
            var visual = new GameObject("Visual").transform;
            visual.SetParent(t, false);
            foreach (var n in new[] { "Body", "Steer", "RearWheel", "Crank", "Rider" })
                t.Find(n).SetParent(visual, true);
            var steer = visual.Find("Steer");
            t.Find("FrontWheel").SetParent(steer, true);

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 20f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

            var col = t.Find("COL_Body");
            var mc = col.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = col.GetComponent<MeshFilter>().sharedMesh;
            mc.convex = true;
            mc.sharedMaterial = Slippery();
            Object.DestroyImmediate(col.GetComponent<MeshRenderer>());

            var ctrl = go.AddComponent<DSTBikeController>();
            ctrl.visual = visual;
            ctrl.steer = steer;
            ctrl.frontWheel = steer.Find("FrontWheel");
            ctrl.rearWheel = visual.Find("RearWheel");
            ctrl.crank = visual.Find("Crank");
            ctrl.rider = visual.Find("Rider").gameObject;
            ctrl.rider.SetActive(false);

            float clearance = DSTPlayerSetup.Radius + 0.12f;
            var entry = go.AddComponent<DSTVehicleEntry>();
            entry.vehicle = ctrl;
            entry.maxExitSpeedKmh = 6f;
            entry.chaseDistance = 3.2f;
            entry.chaseHeight = 1.3f;
            entry.exitPoints = new[]
            {
                ExitPoint(t, "Exit_Left", new Vector3(-0.2f - clearance, 0f, 0.05f)),
                ExitPoint(t, "Exit_Right", new Vector3(0.2f + clearance, 0f, 0.05f)),
                ExitPoint(t, "Exit_Rear", new Vector3(0f, 0f, -0.5f - clearance)),
            };
            go.AddComponent<DSTPhysicsClaimer>();

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            Object.DestroyImmediate(go);
            Debug.Log("[DUST] built " + PrefabPath);
            return prefab;
        }

        // 速度是腳本直接給的，車身不要被地面摩擦卡住
        static PhysicsMaterial Slippery()
        {
            string path = Dir + "/PM_MeBike.physicMaterial";
            var pm = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (!pm)
            {
                pm = new PhysicsMaterial("PM_MeBike");
                AssetDatabase.CreateAsset(pm, path);
            }
            pm.dynamicFriction = 0f;
            pm.staticFriction = 0f;
            pm.frictionCombine = PhysicsMaterialCombine.Minimum;
            pm.bounciness = 0f;
            EditorUtility.SetDirty(pm);
            return pm;
        }

        static Transform ExitPoint(Transform root, string name, Vector3 local)
        {
            var e = new GameObject(name).transform;
            e.SetParent(root, false);
            e.localPosition = local;
            return e;
        }

        // 站點：一排車柱（沿本地 X）＋站牌；車柱正面朝本地 +Z，車子停在 +Z 那側
        static GameObject BuildStation(GameObject bike)
        {
            var root = new GameObject("PF_MeBike_Station");
            var st = root.AddComponent<DSTMeBikeStation>();
            st.bikePrefab = bike;
            st.docks = new Transform[Docks];
            var dockModel = AssetDatabase.LoadAssetAtPath<GameObject>(DockFbx);
            for (int i = 0; i < Docks; i++)
            {
                var d = (GameObject)PrefabUtility.InstantiatePrefab(dockModel, root.transform);
                d.name = $"Dock_{i}";
                d.transform.localPosition = new Vector3(i * DockSpacing, 0f, 0f);
                st.docks[i] = d.transform;
            }
            var sign = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(SignFbx), root.transform);
            sign.name = "Sign";
            sign.transform.localPosition = new Vector3(-0.7f, 0f, 0.15f);
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, StationPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // 遺跡二：出生點附近找一塊空地放一站（放在 DR2_Map 底下，隨機世界時跟著地圖一起藏起來）；
        // 隨機世界：交給 DSTWorld 在營地放
        static void PlaceInScene(GameObject stationPrefab)
        {
            var world = Object.FindAnyObjectByType<DSTWorld>();
            if (world)
            {
                world.meBikeStation = stationPrefab;
                EditorUtility.SetDirty(world);
            }

            var map = GameObject.Find("DR2_Map");
            var old = GameObject.Find("MeBike_Station_DR2");
            if (old) Object.DestroyImmediate(old);
            var spawn = GameObject.Find("DR2_Spawn_T");
            if (!map || !spawn) return;

            float length = (Docks - 1) * DockSpacing + 1.6f, depth = 1.4f;
            for (float r = 3.5f; r < 16f; r += 0.5f)   // 離出生點至少 3.5 m（多人出生會左右錯開）
            for (int a = 0; a < 16; a++)
            for (int yawStep = 0; yawStep < 4; yawStep++)
            {
                float ang = a / 16f * Mathf.PI * 2f;
                var pos = spawn.transform.position + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
                var rot = Quaternion.Euler(0f, yawStep * 90f, 0f);
                var center = pos + rot * new Vector3((Docks - 1) * DockSpacing * 0.5f - 0.35f, 0.6f, 0.45f);
                // 地板要平、範圍內沒有牆或道具（碰撞體底部高於地面 5 cm 以上的都算障礙）
                bool clear = true;
                foreach (var c in Physics.OverlapBox(center, new Vector3(length * 0.5f, 0.55f, depth * 0.5f), rot, ~0, QueryTriggerInteraction.Ignore))
                    if (c.bounds.max.y > pos.y + 0.05f) { clear = false; break; }
                if (!clear) continue;
                bool flat = true;
                foreach (var corner in new[] { new Vector3(-0.8f, 0, -0.3f), new Vector3(length - 0.8f, 0, -0.3f), new Vector3(-0.8f, 0, 1.2f), new Vector3(length - 0.8f, 0, 1.2f) })
                {
                    var p = pos + rot * corner;
                    if (!Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out var hit, 4f) || Mathf.Abs(hit.point.y - pos.y) > 0.05f) { flat = false; break; }
                }
                if (!flat) continue;

                var inst = (GameObject)PrefabUtility.InstantiatePrefab(stationPrefab, map.transform);
                inst.name = "MeBike_Station_DR2";
                inst.transform.SetPositionAndRotation(pos, rot);
                EditorSceneManager.MarkSceneDirty(map.scene);
                EditorSceneManager.SaveScene(map.scene);
                Debug.Log($"[DUST] MeBike station placed at {pos} yaw {yawStep * 90}");
                return;
            }
            Debug.LogWarning("[DUST] 遺跡二出生點附近找不到放 MeBike 站的空地");
        }
    }
}
