using DUST.Vehicles;
using DUST.World;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace DUST.EditorTools
{
    // 把地圖上的箱子 / 木桶從靜態場景物件改成可推動（箱子還可撞碎）的連線物理物件。可以重複執行。
    // 碎片來源：DUST/blender/SM_DST_Prop_Crate_Debris.blend（大箱子切成 50 塊；小箱子共用、縮 0.5）
    public static class DSTPhysicsPropSetup
    {
        const string DebrisDir = "Assets/DUST/Props/Crate";
        const string DebrisFbx = DebrisDir + "/SM_DST_Prop_Crate_Debris.fbx";
        const string DebrisPrefab = DebrisDir + "/PF_Prop_Crate_Debris.prefab";
        const string WoodMat = "Assets/DUST/DR2_HeroTest/Materials/M_DST_Wood_Hero.mat";
        const float DebrisSourceSize = 1.28f;     // 碎片是照大箱子（1.28 m）切的

        [MenuItem("DUST/World/Setup Physics Props")]
        public static void Setup()
        {
            var debris = BuildDebrisPrefab();
            var props = GameObject.Find("DR2_Map_Props");
            if (!props) { Debug.LogError("[DUST] 找不到 DR2_Map_Props"); return; }

            int crates = 0, barrels = 0;
            foreach (Transform t in props.transform)
            {
                var mf = t.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh) continue;
                string mesh = mf.sharedMesh.name;
                if (mesh.StartsWith("SM_DST_Prop_Crate_L") && !mesh.Contains("Broken") || mesh.StartsWith("SM_DST_Prop_Crate_S"))
                {
                    bool large = mesh.StartsWith("SM_DST_Prop_Crate_L");
                    var p = MakePhysical(t.gameObject, large ? 25f : 8f, box: true);
                    p.breakable = true;
                    p.breakDeltaV = large ? 5f : 4f;
                    p.debrisPrefab = debris;
                    p.debrisScale = mf.sharedMesh.bounds.size.y / DebrisSourceSize * t.lossyScale.x;
                    crates++;
                }
                else if (mesh.StartsWith("SM_DST_Prop_Barrel"))
                {
                    var p = MakePhysical(t.gameObject, mesh.Contains("Metal") ? 40f : 30f, box: false);
                    p.breakable = false;
                    barrels++;
                }
            }

            foreach (var path in new[] { DSTVehicleSetup.PrefabPath, DSTDroneSetup.PrefabPath })
            {
                var root = PrefabUtility.LoadPrefabContents(path);
                Ensure<DSTPhysicsClaimer>(root);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                PrefabUtility.UnloadPrefabContents(root);
            }

            EditorSceneManager.MarkSceneDirty(props.scene);
            EditorSceneManager.SaveScene(props.scene);
            Debug.Log($"[DUST] Physics props: {crates} crates (breakable), {barrels} barrels (pushable)");
        }

        // 編輯器裡 GetComponent 找不到時會回傳假 null，不能用 ??
        static T Ensure<T>(GameObject go) where T : Component => go.TryGetComponent<T>(out var c) ? c : go.AddComponent<T>();

        static DSTPhysicsProp MakePhysical(GameObject go, float mass, bool box)
        {
            // 原本是 FBX 的 Prefab 實例；連線的場景物件要是一般物件，先拆掉
            if (PrefabUtility.IsPartOfPrefabInstance(go))
                PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetOutermostPrefabInstanceRoot(go), PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            // 會動的東西不能是靜態（靜態合批會讓它看起來不動）
            GameObjectUtility.SetStaticEditorFlags(go, 0);

            foreach (var mc in go.GetComponents<MeshCollider>()) Object.DestroyImmediate(mc);
            var bounds = go.GetComponent<MeshFilter>().sharedMesh.bounds;
            if (box)
            {
                var bc = Ensure<BoxCollider>(go);
                bc.center = bounds.center;
                bc.size = bounds.size;
            }
            else
            {
                var cc = Ensure<CapsuleCollider>(go);
                cc.center = bounds.center;
                cc.direction = 1;
                cc.height = bounds.size.y;
                cc.radius = Mathf.Max(bounds.size.x, bounds.size.z) * 0.5f;
            }

            var rb = Ensure<Rigidbody>(go);
            rb.mass = mass;
            rb.linearDamping = 0.1f;
            rb.angularDamping = 0.3f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // 被推走之後，動物的導航會繞開它現在的位置
            var obs = Ensure<NavMeshObstacle>(go);
            obs.shape = NavMeshObstacleShape.Box;
            obs.center = bounds.center;
            obs.size = bounds.size;
            obs.carving = true;
            obs.carveOnlyStationary = true;

            var no = Ensure<NetworkObject>(go);
            no.DontDestroyWithOwner = true;
            no.AutoObjectParentSync = false;          // 父物件 DR2_Map_Props 不是連線物件
            var nt = Ensure<NetworkTransform>(go);
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
            var nrb = Ensure<NetworkRigidbody>(go);
            nrb.UseRigidBodyForMotion = true;         // 非擁有者用 MovePosition，撞到別的剛體才推得動

            var prop = Ensure<DSTPhysicsProp>(go);
            EditorUtility.SetDirty(go);
            return prop;
        }

        static GameObject BuildDebrisPrefab()
        {
            var wood = AssetDatabase.LoadAssetAtPath<Material>(WoodMat);
            var mi = (ModelImporter)AssetImporter.GetAtPath(DebrisFbx);
            mi.addCollider = false;
            mi.isReadable = true;                     // 建置版執行期要能讀網格（保險起見；碰撞體已在 Prefab 裡先建好）
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.importCameras = false;
            mi.importLights = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            if (wood) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "M_DST_Wood_Weathered"), wood);
            mi.SaveAndReimport();

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(DebrisFbx);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = "PF_Prop_Crate_Debris";
            Ensure<DSTDebris>(go);
            // 碰撞體在編輯器先建好（建置時會預先烘焙），執行期只加剛體
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                var mc = Ensure<MeshCollider>(mf.gameObject);
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = true;
            }
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, DebrisPrefab);
            Object.DestroyImmediate(go);
            return prefab;
        }
    }
}
