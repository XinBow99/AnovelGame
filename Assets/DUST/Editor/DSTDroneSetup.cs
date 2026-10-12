using DUST.Vehicles;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DUST.EditorTools
{
    // 把 Blender 匯出的 SM_DST_Vehicle_Drone.fbx（三角翼推進式無人機）組成可駕駛的 Prefab，並放進場景。
    // Blender 原始檔：DUST/blender/SM_DST_Vehicle_Drone.blend（實機尺寸 3.5 m，匯出時 ×0.5）
    public static class DSTDroneSetup
    {
        const string Dir = "Assets/DUST/Vehicles/Drone";
        const string Fbx = Dir + "/SM_DST_Vehicle_Drone.fbx";
        const string MatDir = Dir + "/Materials";
        public const string PrefabPath = Dir + "/PF_Vehicle_Drone.prefab";
        const float Mass = 60f;

        [MenuItem("DUST/Vehicles/Build Drone Prefab")]
        public static GameObject Build()
        {
            SetupImporter();
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            go.name = "PF_Vehicle_Drone";

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var col = go.transform.Find("COL_Body");
            var mc = col.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = col.GetComponent<MeshFilter>().sharedMesh;
            mc.convex = true;
            Object.DestroyImmediate(col.GetComponent<MeshRenderer>());

            var ctrl = go.AddComponent<DSTDroneController>();
            ctrl.propeller = go.transform.Find("Propeller");
            ctrl.centerOfMass = go.transform.Find("CenterOfMass");

            // 下機點：左右翼尖外側、機尾後方、機頭前方
            var b = go.transform.Find("Body").GetComponent<MeshFilter>().sharedMesh.bounds;
            float clearance = DSTPlayerSetup.Radius + 0.15f;
            var entry = go.AddComponent<DSTVehicleEntry>();
            entry.vehicle = ctrl;
            entry.maxExitSpeedKmh = 6f;
            entry.exitPoints = new[]
            {
                ExitPoint(go.transform, "Exit_Left", new Vector3(b.min.x - clearance, 0f, b.center.z)),
                ExitPoint(go.transform, "Exit_Right", new Vector3(b.max.x + clearance, 0f, b.center.z)),
                ExitPoint(go.transform, "Exit_Rear", new Vector3(0f, 0f, b.min.z - clearance)),
                ExitPoint(go.transform, "Exit_Front", new Vector3(0f, 0f, b.max.z + clearance)),
            };

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            Object.DestroyImmediate(go);
            Debug.Log("[DUST] built " + PrefabPath);
            return prefab;
        }

        // 放在皮卡旁邊；已經有就換成新的
        [MenuItem("DUST/Vehicles/Place Drone In Open Scene")]
        public static GameObject PlaceInScene()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (!prefab) prefab = Build();
            Vector3 pos = Vector3.zero;
            float yaw = 0f;
            var old = GameObject.Find("PF_Vehicle_Drone");
            if (old)
            {
                pos = old.transform.position;
                yaw = old.transform.eulerAngles.y;
                Undo.DestroyObjectImmediate(old);
            }
            else
            {
                var pickup = GameObject.Find("PF_Vehicle_Pickup");
                var spawn = GameObject.Find("DR2_Spawn_T");
                var anchor = pickup ? pickup.transform : spawn ? spawn.transform : null;
                if (anchor)
                {
                    pos = anchor.position + anchor.right * 2.5f;
                    yaw = anchor.eulerAngles.y;
                }
                // 貼地
                if (Physics.Raycast(pos + Vector3.up * 10f, Vector3.down, out var hit, 30f, ~0, QueryTriggerInteraction.Ignore))
                    pos = hit.point + Vector3.up * 0.02f;
            }
            var drone = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            drone.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            drone.GetComponent<DSTDroneController>().flightArea = MapFlightArea();
            Undo.RegisterCreatedObjectUndo(drone, "Place Drone");
            EditorSceneManager.MarkSceneDirty(drone.scene);
            return drone;
        }

        // 飛行範圍 = 地圖地板的範圍往內縮 3 m（地圖外沒有地面）
        static Bounds MapFlightArea()
        {
            var floors = GameObject.Find("DR2_Map_Floors");
            if (!floors) return default;
            var cols = floors.GetComponentsInChildren<Collider>();
            if (cols.Length == 0) return default;
            var b = cols[0].bounds;
            foreach (var c in cols) b.Encapsulate(c.bounds);
            b.Expand(new Vector3(-6f, 0f, -6f));
            return b;
        }

        static Transform ExitPoint(Transform root, string name, Vector3 local)
        {
            var t = new GameObject(name).transform;
            t.SetParent(root, false);
            t.localPosition = local;
            return t;
        }

        static void SetupImporter()
        {
            var mats = new[]
            {
                DSTVehicleSetup.LitAt(MatDir, "M_DST_Drone_Paint", new Color(0.62f, 0.63f, 0.60f), 0f, 0.45f),
                DSTVehicleSetup.LitAt(MatDir, "M_DST_Drone_Dark", new Color(0.06f, 0.06f, 0.06f), 0.3f, 0.55f),
                DSTVehicleSetup.LitAt(MatDir, "M_DST_Drone_Prop", new Color(0.03f, 0.03f, 0.03f), 0f, 0.4f),
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
            foreach (var m in mats)
                mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), m.name), m);
            mi.SaveAndReimport();
        }
    }
}
