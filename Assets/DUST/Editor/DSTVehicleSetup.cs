using System.Collections.Generic;
using System.IO;
using DUST.Vehicles;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.EditorTools
{
    // 把 Blender 匯出的 SM_DST_Vehicle_Pickup.fbx 組成可駕駛的 Prefab
    public static class DSTVehicleSetup
    {
        const string Dir = "Assets/DUST/Vehicles/Pickup";
        const string Fbx = Dir + "/SM_DST_Vehicle_Pickup.fbx";
        const string MatDir = Dir + "/Materials";
        public const string PrefabPath = Dir + "/PF_Vehicle_Pickup.prefab";

        // 車輛尺寸是 Blender 原設計（車長 4.9 m）的 Scale 倍；物理參數都從這兩個數字推算
        const float Scale = 0.5f;
        const float Mass = 600f;

        const float WheelRadius = 0.375f * Scale;
        const float SuspensionDistance = 0.30f * Scale;
        const float TargetPosition = 0.5f;
        // 懸吊頻率約 2.6 Hz、阻尼比約 0.5（與原尺寸相近的手感）
        static float CornerMass => Mass / 4f;
        static float Spring => CornerMass * Mathf.Pow(2f * Mathf.PI * 2.6f, 2f);
        static float Damper => 0.5f * 2f * Mathf.Sqrt(Spring * CornerMass);
        // 靜止時輪心在掛點下方：target 位置減掉車重造成的下沉
        static float RestDropBelowMount => SuspensionDistance * (1f - TargetPosition) - CornerMass * 9.81f / Spring;

        [MenuItem("DUST/Vehicles/Build Pickup Prefab")]
        public static GameObject Build()
        {
            SetupImporter();
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            go.name = "PF_Vehicle_Pickup";

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mass;
            rb.linearDamping = 0.02f;
            rb.angularDamping = 0.3f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var col = go.transform.Find("COL_Body");
            var mc = col.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = col.GetComponent<MeshFilter>().sharedMesh;
            mc.convex = true;
            Object.DestroyImmediate(col.GetComponent<MeshRenderer>());

            var wcRoot = new GameObject("WheelColliders").transform;
            wcRoot.SetParent(go.transform, false);
            WheelCollider MakeWheel(string visualName, float sideStiffness = 1.6f)
            {
                var vis = go.transform.Find(visualName);
                var t = new GameObject("WC_" + visualName.Replace("Wheel_", "")).transform;
                t.SetParent(wcRoot, false);
                t.localPosition = vis.localPosition + Vector3.up * RestDropBelowMount;
                var wc = t.gameObject.AddComponent<WheelCollider>();
                wc.mass = 25f * Scale;
                wc.radius = WheelRadius;
                wc.wheelDampingRate = 0.4f * Scale;
                wc.suspensionDistance = SuspensionDistance;
                wc.forceAppPointDistance = 0.15f * Scale;
                wc.suspensionSpring = new JointSpring { spring = Spring, damper = Damper, targetPosition = TargetPosition };
                // 沙地：側向抓地比柏油低一點，比較會滑
                wc.forwardFriction = new WheelFrictionCurve { extremumSlip = 0.4f, extremumValue = 1f, asymptoteSlip = 0.8f, asymptoteValue = 0.6f, stiffness = 1.4f };
                wc.sidewaysFriction = new WheelFrictionCurve { extremumSlip = 0.25f, extremumValue = 1f, asymptoteSlip = 0.6f, asymptoteValue = 0.7f, stiffness = sideStiffness };
                return wc;
            }

            var ctrl = go.AddComponent<DSTVehicleController>();
            ctrl.front = new DSTVehicleController.Axle
            {
                left = MakeWheel("Wheel_FL"), right = MakeWheel("Wheel_FR"),
                leftVisual = go.transform.Find("Wheel_FL"), rightVisual = go.transform.Find("Wheel_FR"),
                steer = true, motor = true, antiRoll = 9000f * MassRatio
            };
            ctrl.rear = new DSTVehicleController.Axle
            {
                // 後輪側向抓地較高：短軸距的小車才不會一打方向就甩尾
                left = MakeWheel("Wheel_RL", 2.1f), right = MakeWheel("Wheel_RR", 2.1f),
                leftVisual = go.transform.Find("Wheel_RL"), rightVisual = go.transform.Find("Wheel_RR"),
                motor = true, handbrake = true, antiRoll = 7000f * MassRatio
            };
            // 扭力 / 煞車：維持與原尺寸相同的加減速（力 ∝ 車重，扭力 = 力 × 輪半徑）
            float torqueRatio = MassRatio * Scale;
            ctrl.maxMotorTorque = 750f * torqueRatio;
            ctrl.maxBrakeTorque = 3000f * torqueRatio;
            ctrl.handbrakeTorque = 5000f * torqueRatio;
            ctrl.downforce = 2.5f * MassRatio;
            // 軸距只有原本的一半，同樣轉向角下偏航率加倍 → 高速轉向角要更小
            ctrl.steerAngleAtSpeed = 6f;
            ctrl.steerFalloffSpeedKmh = 70f;
            ctrl.steerRateDegPerSec = 120f;
            var com = go.transform.Find("CenterOfMass");
            com.localPosition = new Vector3(0f, 0.55f, 0.15f) * Scale;
            ctrl.centerOfMass = com;
            ctrl.steeringWheel = go.transform.Find("SteeringWheel");
            ctrl.steeringWheelAxis = ThinAxis(ctrl.steeringWheel);

            ctrl.playerControlled = false;

            // 上下車：駕駛側（左，-X）、乘客側、車尾，距車身留出角色半徑 + 餘裕
            var body = go.transform.Find("Body").GetComponent<MeshFilter>().sharedMesh.bounds;
            float clearance = DSTPlayerSetup.Radius + 0.12f;
            var entry = go.AddComponent<DSTVehicleEntry>();
            entry.vehicle = ctrl;
            entry.exitPoints = new[]
            {
                ExitPoint(go.transform, "Exit_Driver", new Vector3(body.min.x - clearance, 0f, body.center.z + 0.05f)),
                ExitPoint(go.transform, "Exit_Passenger", new Vector3(body.max.x + clearance, 0f, body.center.z + 0.05f)),
                ExitPoint(go.transform, "Exit_Rear", new Vector3(0f, 0f, body.min.z - clearance)),
            };
            var oldExit = go.transform.Find("Exit_Left");
            if (oldExit) Object.DestroyImmediate(oldExit.gameObject);
            go.AddComponent<DSTPhysicsClaimer>();     // 撞箱子、撞倒 / 撞碎隨機世界的擺設

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            Object.DestroyImmediate(go);
            Debug.Log("[DUST] built " + PrefabPath);
            return prefab;
        }

        static Transform ExitPoint(Transform root, string name, Vector3 local)
        {
            var t = new GameObject(name).transform;
            t.SetParent(root, false);
            t.localPosition = local;
            return t;
        }

        const float OriginalMass = 1800f;
        static float MassRatio => Mass / OriginalMass;

        // 方向盤 mesh 最薄的軸就是轉軸
        static Vector3 ThinAxis(Transform t)
        {
            var s = t.GetComponent<MeshFilter>().sharedMesh.bounds.size;
            if (s.x <= s.y && s.x <= s.z) return Vector3.right;
            if (s.y <= s.x && s.y <= s.z) return Vector3.up;
            return Vector3.forward;
        }

        static void SetupImporter()
        {
            Directory.CreateDirectory(MatDir);
            var mats = new Dictionary<string, Material>
            {
                { "M_DST_Vehicle_Paint", Lit("M_DST_Vehicle_Paint", new Color(0.20f, 0.29f, 0.36f), 0f, 0.42f, coat: 0.25f) },
                { "M_DST_Vehicle_Metal", Lit("M_DST_Vehicle_Metal", new Color(0.05f, 0.05f, 0.05f), 0.6f, 0.35f) },
                { "M_DST_Vehicle_Glass", Lit("M_DST_Vehicle_Glass", new Color(0.10f, 0.12f, 0.12f, 0.35f), 0f, 0.95f, transparent: true) },
                { "M_DST_Vehicle_Chrome", Lit("M_DST_Vehicle_Chrome", new Color(0.85f, 0.85f, 0.85f), 1f, 0.85f) },
                { "M_DST_Vehicle_Rubber", Lit("M_DST_Vehicle_Rubber", new Color(0.03f, 0.03f, 0.03f), 0f, 0.18f) },
                { "M_DST_Vehicle_HeadLens", Lit("M_DST_Vehicle_HeadLens", new Color(0.9f, 0.9f, 0.85f), 0f, 0.95f) },
                { "M_DST_Vehicle_TailLens", Lit("M_DST_Vehicle_TailLens", new Color(0.45f, 0.02f, 0.02f), 0f, 0.9f) },
                { "M_DST_Vehicle_Interior", Lit("M_DST_Vehicle_Interior", new Color(0.17f, 0.13f, 0.10f), 0f, 0.2f) },
                { "M_DST_Vehicle_Rim", Lit("M_DST_Vehicle_Rim", new Color(0.75f, 0.73f, 0.66f), 0.3f, 0.5f) },
            };
            var mi = (ModelImporter)AssetImporter.GetAtPath(Fbx);
            mi.globalScale = 1;
            mi.useFileScale = true;
            mi.addCollider = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            foreach (var kv in mats)
                mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            mi.SaveAndReimport();
        }

        static Material Lit(string name, Color c, float metallic, float smoothness, float coat = 0f, bool transparent = false)
            => LitAt(MatDir, name, c, metallic, smoothness, coat, transparent);

        // 其他載具（無人機）也用同一套 HDRP Lit 材質設定
        internal static Material LitAt(string dir, string name, Color c, float metallic, float smoothness, float coat = 0f, bool transparent = false)
        {
            Directory.CreateDirectory(dir);
            var path = dir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("HDRP/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smoothness);
            if (coat > 0f)
            {
                m.SetFloat("_MaterialID", 1);      // Standard
                m.SetFloat("_CoatMask", coat);
            }
            HDMaterial.SetSurfaceType(m, transparent);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
