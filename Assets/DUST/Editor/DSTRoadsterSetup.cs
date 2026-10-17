using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using DUST.Vehicles;

namespace DUST.EditorTools
{
    // Roadster（issue #8）：Blender 匯出的 SM_DST_Vehicle_Roadster.fbx + roadster_layout.json → HDRP 材質 + Prefab。
    // Blender 原始檔：DUST/blender/SM_DST_Vehicle_Roadster.blend
    // 重建 / 匯出腳本：Tools/Blender/roadster_build.py、roadster_export_unity.py
    // FBX 是實車尺寸（約 4.3 m），importer 的 globalScale 0.5 縮成半尺寸世界。
    // roadster_layout.json 已經是 Unity 座標（半尺寸、車頭 +Z、+X = 車的右邊）：輪心、10 個噴嘴（出口 + 噴氣方向）、
    // 4 個風扇噴射引擎、尾翼樞紐、駕駛眼睛、重心 —— 數值只在 Blender 腳本裡一份，這裡照讀。
    // 階層：PF_Vehicle_Roadster（乾淨的根：Rigidbody、控制）> Model（FBX）> RD_Root > RD_Chassis（不動的部分）、
    //   RD_Wheel_FL/FR/RL/RR（原點 = 輪轂）、RD_Yoke、RD_Wing（> WingStrut_L/R）、RD_FanRotor_0..3、RD_Nozzle_*、燈。
    public static class DSTRoadsterSetup
    {
        const string Dir = "Assets/DUST/Vehicles/Roadster";
        const string Fbx = Dir + "/SM_DST_Vehicle_Roadster.fbx";
        const string LayoutPath = Dir + "/roadster_layout.json";
        const string MatDir = Dir + "/Materials";
        public const string PrefabPath = Dir + "/PF_Vehicle_Roadster.prefab";

        // 實車約 1,700 kg（MotorTrend 估），半尺寸 ×1/8；這是氣瓶滿的時候（DSTThrusterSystem 會隨用氣減少）
        const float Mass = 212.5f;
        // 每個噴嘴最大推力（N，半尺寸 = 實車 ×1/8）：後 5.4 kN、下 10 kN、側 5 kN（設計估算）
        const float RearThrust = 680f, DownThrust = 1250f, SideThrust = 625f;
        // 駕駛的鬃狼像臥佛一樣側躺在兩個座位上、單手（右前腳掌）按著方向盤中央開車（Tools/Blender/wolf_roadster_recline.py）。
        // 駕駛座視角不放在躺著的頭上：那裡跟儀表板一樣高，幾乎看不到路（實測）；維持坐姿眼睛高度（身體在這個視角只投影子）。

        [System.Serializable] public class WheelL { public string name; public float[] pos; public float radius, width; public bool front, left; }
        [System.Serializable] public class NozzleL { public string name, obj, group; public float[] pos, dir; public float radius; }
        [System.Serializable] public class FanL { public string name; public float[] pos, dir; public float radius; }
        [System.Serializable] public class WingL { public string obj; public float[] pivot; public float chord, span; }
        [System.Serializable]
        public class Layout
        {
            public float scale, length, width, height;
            public WheelL[] wheels;
            public NozzleL[] nozzles;
            public FanL[] fans;
            public WingL wing;
            public float[] seatEye, com;
        }

        public static Layout LoadLayout() => JsonUtility.FromJson<Layout>(System.IO.File.ReadAllText(LayoutPath));
        static Vector3 V(float[] a) => new Vector3(a[0], a[1], a[2]);

        [MenuItem("DUST/Vehicles/Build Roadster Prefab")]
        public static GameObject Build()
        {
            SetupImporter();
            var lay = LoadLayout();
            // FBX 根帶著 Blender 的軸向旋轉，外面包一層乾淨的根（跟米格-25 一樣）
            var go = new GameObject("PF_Vehicle_Roadster");
            var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Fbx));
            model.name = "Model";
            model.transform.SetParent(go.transform, false);
            foreach (var r in model.GetComponentsInChildren<Renderer>())
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.maxAngularVelocity = 20f;
            AddColliders(go.transform, lay);

            var com = Child(go.transform, "CenterOfMass", V(lay.com));
            var eye = Child(go.transform, "Seat_Driver_Eye", V(lay.seatEye));
            eye.localRotation = Quaternion.Euler(8f, 0f, 0f);   // 往下 8°：方向盤、儀表在畫面下緣，前方路面在中間

            var car = go.AddComponent<DSTRoadsterController>();
            car.centerOfMass = com;
            car.wheels = System.Array.ConvertAll(lay.wheels, w => new DSTRoadsterController.WheelSpec
            {
                name = w.name, local = V(w.pos), radius = w.radius, front = w.front, left = w.left
            });
            car.yoke = FindDeep(go.transform, "RD_Yoke");

            // 冷氣推進器：10 個噴嘴（位置 / 噴氣方向從 layout），推力照 DSTThrusterSystem 開頭的估算
            var thr = go.AddComponent<DSTThrusterSystem>();
            thr.thrusters = System.Array.ConvertAll(lay.nozzles, n => new DSTThrusterSystem.Thruster
            {
                name = n.name,
                group = n.group == "rear" ? DSTThrusterSystem.Group.Rear : n.group == "down" ? DSTThrusterSystem.Group.Down : DSTThrusterSystem.Group.Side,
                localPos = V(n.pos), localDir = V(n.dir).normalized, radius = n.radius,
                maxThrust = n.group == "rear" ? RearThrust : n.group == "down" ? DownThrust : SideThrust,
            });
            var fx = go.AddComponent<DSTThrusterFX>();
            fx.vaporMaterial = VaporMaterial();

            // 主動空力 + 吸地風扇（模式：低風阻 / 高下壓力；低速吸地 / 高速流場）
            var aero = go.AddComponent<DSTRoadsterAero>();
            aero.wing = FindDeep(go.transform, lay.wing.obj);
            aero.fanRotors = System.Array.ConvertAll(lay.fans, f => FindDeep(go.transform, f.name));
            go.AddComponent<DSTPhysicsClaimer>();

            // 上下車：左邊（駕駛座側，-X）、右邊、車尾
            var entry = go.AddComponent<DSTVehicleEntry>();
            entry.vehicle = car;
            entry.maxExitSpeedKmh = 10f;
            entry.chaseDistance = 3.4f;    // 車只有 2.15 m 長（半尺寸）：4.6 m 時車在畫面裡太小（實測）
            entry.chaseHeight = 1.1f;
            entry.exitPoints = new[]
            {
                Child(go.transform, "Exit_Left", new Vector3(-0.95f, 0f, -0.2f)),
                Child(go.transform, "Exit_Right", new Vector3(0.95f, 0f, -0.2f)),
                Child(go.transform, "Exit_Rear", new Vector3(0f, 0f, -1.6f)),
            };
            // 駕駛的鬃狼躺進兩個座位（以前留在上車的地方，玩家回報）
            DSTSeatRiderSetup.AddNative(go, "PL_Roadster_Recline", "PL_Roadster_Recline_SteerLeft", "PL_Roadster_Recline_SteerRight");

            PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            Object.DestroyImmediate(go);
            DSTNetSetup.SetupVehiclePrefab(PrefabPath);
            Debug.Log("[DUST] built " + PrefabPath);
            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        static Transform Child(Transform root, string name, Vector3 local)
        {
            var t = new GameObject(name).transform;
            t.SetParent(root, false);
            t.localPosition = local;
            return t;
        }

        // 簡化碰撞體（半尺寸、車頭 +Z）：車身下半（底盤離地 4.5 cm，碰撞體底再高一點，懸吊壓縮時才不會先刮地）、
        // 車頭楔形、座艙。輪子不放碰撞體，接地交給 DSTRoadsterController 的懸吊射線。
        static void AddColliders(Transform root, Layout lay)
        {
            var cols = new GameObject("Colliders").transform;
            cols.SetParent(root, false);
            float halfL = lay.length * 0.5f;
            Box(cols, "Body", new Vector3(0f, 0.25f, -0.05f), new Vector3(0.96f, 0.36f, 1.80f));
            Box(cols, "Nose", new Vector3(0f, 0.17f, halfL - 0.17f), new Vector3(0.86f, 0.20f, 0.34f));
            Box(cols, "Tail", new Vector3(0f, 0.31f, -halfL + 0.12f), new Vector3(0.90f, 0.24f, 0.24f));
            Box(cols, "Cabin", new Vector3(0f, 0.47f, -0.15f), new Vector3(0.70f, 0.16f, 1.20f));
        }

        static void Box(Transform parent, string name, Vector3 c, Vector3 size)
        {
            var b = new GameObject(name).AddComponent<BoxCollider>();
            b.transform.SetParent(parent, false);
            b.center = c;
            b.size = size;
        }

        // ------------------------------------------------------------------ 材質
        static void SetupImporter()
        {
            System.IO.Directory.CreateDirectory(MatDir);
            var remap = new (string src, Material dst)[]
            {
                // Ano White：緞面白金屬漆 + 透明亮光層
                ("M_DST_Roadster_Paint", DSTVehicleSetup.LitAt(MatDir, "M_DST_Roadster_Paint", new Color(0.80f, 0.80f, 0.78f), 0.35f, 0.55f, 1f)),
                // 金色調玻璃：深色半透明、很亮的反射
                // 從座位往外看要看得清楚：透明度高一點、金屬感低一點（反射交給菲涅耳）
                ("M_DST_Roadster_Glass", TwoSided(DSTVehicleSetup.LitAt(MatDir, "M_DST_Roadster_Glass", new Color(0.10f, 0.08f, 0.04f, 0.62f), 0.05f, 0.93f, 0f, true))),
                ("M_DST_Roadster_Carbon", DSTVehicleSetup.LitAt(MatDir, "M_DST_Roadster_Carbon", new Color(0.025f, 0.025f, 0.03f), 0f, 0.72f, 1f)),
                ("M_DST_Roadster_Titanium", DSTVehicleSetup.LitAt(MatDir, "M_DST_Roadster_Titanium", new Color(0.55f, 0.53f, 0.50f), 1f, 0.62f)),
                ("M_DST_Roadster_Black", TwoSided(DSTVehicleSetup.LitAt(MatDir, "M_DST_Roadster_Black", new Color(0.025f, 0.025f, 0.028f), 0f, 0.35f))),
                ("M_DST_Roadster_Rubber", DSTVehicleSetup.LitAt(MatDir, "M_DST_Roadster_Rubber", new Color(0.03f, 0.03f, 0.03f), 0f, 0.15f)),
                ("M_DST_Roadster_Spoke", DSTVehicleSetup.LitAt(MatDir, "M_DST_Roadster_Spoke", new Color(0.05f, 0.05f, 0.055f), 0.7f, 0.6f)),
                ("M_DST_Roadster_Brass", DSTVehicleSetup.LitAt(MatDir, "M_DST_Roadster_Brass", new Color(0.72f, 0.55f, 0.33f), 1f, 0.65f)),
                // 後方出氣管：偏暗的暖灰燒灼金屬（Gravity 圖），不要亮銀
                ("M_DST_Roadster_Burnt", TwoSided(DSTVehicleSetup.LitAt(MatDir, "M_DST_Roadster_Burnt", new Color(0.36f, 0.28f, 0.20f), 1f, 0.55f))),
                // 風扇噴射引擎的子彈形輪轂：深石墨金屬
                ("M_DST_Roadster_Graphite", DSTVehicleSetup.LitAt(MatDir, "M_DST_Roadster_Graphite", new Color(0.07f, 0.07f, 0.075f), 0.8f, 0.62f)),
                ("M_DST_Roadster_Interior", DSTVehicleSetup.LitAt(MatDir, "M_DST_Roadster_Interior", new Color(0.16f, 0.16f, 0.165f), 0f, 0.4f)),
                ("M_DST_Roadster_Screen", Emissive("M_DST_Roadster_Screen", new Color(0.01f, 0.012f, 0.015f), new Color(0.25f, 0.35f, 0.45f) * 1.2f)),   // ×6 會整片白（實測）
                ("M_DST_Roadster_HeadLamp", Emissive("M_DST_Roadster_HeadLamp", new Color(0.9f, 0.95f, 1f), new Color(0.85f, 0.92f, 1f) * 60f)),
                ("M_DST_Roadster_TailLamp", Emissive("M_DST_Roadster_TailLamp", new Color(0.4f, 0.02f, 0.02f), new Color(1f, 0.03f, 0.02f) * 12f)),
            };
            var mi = (ModelImporter)AssetImporter.GetAtPath(Fbx);
            mi.globalScale = 0.5f;                 // 半尺寸世界
            mi.useFileScale = true;
            mi.bakeAxisConversion = true;
            mi.addCollider = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.importNormals = ModelImporterNormals.Import;      // Blender 的銳邊（20° 以上的折線）照用
            mi.importTangents = ModelImporterTangents.CalculateMikk;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            foreach (var (src, dst) in remap)
                mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), src), dst);
            mi.SaveAndReimport();
            AssetDatabase.SaveAssets();
        }

        // 自發光：數值是 HDRP 的 nits 量級（曝光固定 EV 13.8，平常的亮面約幾千 nits；燈要比它亮很多）
        static Material Emissive(string name, Color baseColor, Color emission)
        {
            var m = DSTVehicleSetup.LitAt(MatDir, name, baseColor, 0f, 0.8f);
            m.SetColor("_EmissiveColor", emission);
            m.SetColor("_EmissiveColorLDR", emission.maxColorComponent > 0f ? emission / emission.maxColorComponent : Color.black);
            m.SetFloat("_UseEmissiveIntensity", 0f);
            m.SetFloat("_EmissiveExposureWeight", 0f);    // 不吃曝光：數值直接是畫面亮度（跟米格-25 的火焰一樣好調）
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        // 白霧粒子：DUST/VaporParticle（吃粒子頂點顏色、軟粒子）+ 邊緣很軟、中間有點雲絮的圓形貼圖
        static Material VaporMaterial()
        {
            const int N = 64;
            string texPath = Dir + "/Textures/T_DST_Roadster_Vapor.png";
            System.IO.Directory.CreateDirectory(Dir + "/Textures");
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false);
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float u = (x + 0.5f) / N, v = (y + 0.5f) / N;
                float r = Mathf.Sqrt((u - 0.5f) * (u - 0.5f) + (v - 0.5f) * (v - 0.5f)) * 2f;
                float soft = Mathf.Pow(Mathf.Clamp01(1f - r), 1.7f);
                float n = 0.7f + 0.3f * Mathf.PerlinNoise(u * 5f + 2f, v * 5f + 7f);
                t.SetPixel(x, y, new Color(1f, 1f, 1f, soft * n));
            }
            t.Apply();
            System.IO.File.WriteAllBytes(texPath, t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(texPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(texPath);
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.alphaIsTransparency = true;
            ti.SaveAndReimport();
            string path = MatDir + "/M_DST_Roadster_Vapor.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("DUST/VaporParticle");
            if (!m) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
            m.shader = shader;
            m.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            m.SetColor("_Color", Color.white);
            m.SetFloat("_Brightness", 1f);
            m.SetFloat("_SoftDistance", 0.25f);
            m.renderQueue = 3000;
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material TwoSided(Material m)
        {
            m.SetFloat("_DoubleSidedEnable", 1f);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        // ------------------------------------------------------------------ 場景
        // 遺跡二：停在皮卡旁邊找得到的空地；隨機世界由 DSTWorld.Relocate 停到出生點旁（跟其他車一起）
        [MenuItem("DUST/Vehicles/Place Roadster In Open Scene")]
        public static GameObject PlaceInScene()
        {
            var prefab = Build();
            Vector3 pos = Vector3.zero;
            float yaw = 0f;
            var old = GameObject.Find("PF_Vehicle_Roadster");
            if (old)
            {
                pos = old.transform.position;
                yaw = old.transform.eulerAngles.y;
                Undo.DestroyObjectImmediate(old);
            }
            else if (!FindSpotNear(out pos, out yaw))
                Debug.LogWarning("[DUST] 找不到放 Roadster 的空地，先放在原點");
            var car = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            car.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            Undo.RegisterCreatedObjectUndo(car, "Place Roadster");
            EditorSceneManager.MarkSceneDirty(car.scene);
            return car;
        }

        // 從皮卡旁邊開始一圈圈往外找一塊平的、放得下車（1.1 × 2.3 m）的地方
        static bool FindSpotNear(out Vector3 best, out float yaw)
        {
            best = Vector3.zero;
            var pickup = GameObject.Find("PF_Vehicle_Pickup");
            Vector3 c = pickup ? pickup.transform.position : Vector3.zero;
            yaw = pickup ? pickup.transform.eulerAngles.y : 0f;
            var rot = Quaternion.Euler(0f, yaw, 0f);
            for (float rad = 4f; rad < 40f; rad += 1.5f)
            for (int k = 0; k < 16; k++)
            {
                float a = k * Mathf.PI / 8f;
                var p = c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * rad;
                if (!Physics.Raycast(p + Vector3.up * 20f, Vector3.down, out var hit, 60f, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (hit.normal.y < 0.97f) continue;
                if (Physics.CheckBox(hit.point + Vector3.up * 0.75f, new Vector3(0.75f, 0.6f, 1.45f), rot, ~0, QueryTriggerInteraction.Ignore)) continue;
                best = hit.point + Vector3.up * 0.02f;
                return true;
            }
            return false;
        }
    }
}
