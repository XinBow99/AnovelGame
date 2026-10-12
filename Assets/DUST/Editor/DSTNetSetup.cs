using System.Linq;
using DUST.Animals;
using DUST.Net;
using DUST.Player;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DUST.EditorTools
{
    // 把玩家、車輛、動物 Prefab 和目前開著的場景設定成可連線。可以重複執行。
    // 注意：重新跑 DSTPlayerSetup / DSTVehicleSetup / DSTDroneSetup / DSTAnimalSetup 重建 Prefab 之後，要再跑一次這個。
    public static class DSTNetSetup
    {
        const string NetDir = "Assets/DUST/Net";
        const string PrefabListPath = NetDir + "/NPL_DUST.asset";
        const string BodyMatPath = "Assets/DUST/Player/M_PlayerBody.mat";

        [MenuItem("DUST/Network/Setup Multiplayer")]
        public static void SetupAll()
        {
            System.IO.Directory.CreateDirectory(NetDir);
            var player = SetupPlayerPrefab();
            SetupVehiclePrefab(DSTVehicleSetup.PrefabPath);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(DSTDroneSetup.PrefabPath))
                SetupVehiclePrefab(DSTDroneSetup.PrefabPath);
            var spawner = Object.FindAnyObjectByType<DSTAnimalSpawner>();
            var animals = spawner ? spawner.species.Where(s => s && s.prefab).Select(s => s.prefab).Distinct().ToArray() : new GameObject[0];
            foreach (var a in animals) SetupAnimalPrefab(AssetDatabase.GetAssetPath(a));
            var nm = SetupScene(player);
            RegisterPrefabs(nm, animals);
            EditorSceneManager.MarkSceneDirty(nm.gameObject.scene);
            EditorSceneManager.SaveScene(nm.gameObject.scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[DUST] Multiplayer setup done: player, vehicle, {animals.Length} animal prefab(s), NetworkManager");
        }

        // 編輯器裡 GetComponent 找不到時會回傳假 null，不能用 ??
        static T Ensure<T>(GameObject go) where T : Component => go.TryGetComponent<T>(out var c) ? c : go.AddComponent<T>();

        // ------------------------------------------------------------------ 玩家
        static GameObject SetupPlayerPrefab()
        {
            var root = PrefabUtility.LoadPrefabContents(DSTPlayerSetup.PrefabPath);
            Ensure<NetworkObject>(root);
            var nt = Ensure<NetworkTransform>(root);
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;   // 自己的移動自己算，手感不延遲
            nt.SyncRotAngleX = nt.SyncRotAngleZ = false;
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;

            var pc = root.GetComponent<DSTPlayerController>();
            var ia = root.GetComponent<DSTPlayerInteractor>();
            // 預設全關，DSTNetPlayer 只幫自己的角色打開（別人的角色不吃輸入、不開鏡頭）
            pc.enabled = false;
            if (ia) ia.enabled = false;
            var cam = pc.head.GetComponent<Camera>();
            if (cam) cam.enabled = false;
            var al = pc.head.GetComponent<AudioListener>();
            if (al) al.enabled = false;

            // 別人看到的身體：膠囊 + 指示面向的護目鏡
            var mat = AssetDatabase.LoadAssetAtPath<Material>(BodyMatPath);
            if (!mat)
            {
                mat = new Material(Shader.Find("HDRP/Lit"));
                mat.SetColor("_BaseColor", new Color(0.78f, 0.42f, 0.18f));
                mat.SetFloat("_Smoothness", 0.35f);
                AssetDatabase.CreateAsset(mat, BodyMatPath);
            }
            var cc = root.GetComponent<CharacterController>();
            float h = pc.standHeight, r = cc.radius;
            var body = Primitive(root.transform, "Body", PrimitiveType.Capsule, mat);
            body.localPosition = Vector3.up * (h * 0.5f);
            body.localScale = new Vector3(r * 2f, h * 0.5f, r * 2f);
            var visor = Primitive(pc.head, "Visor", PrimitiveType.Cube, mat);
            visor.localPosition = new Vector3(0f, 0f, r * 0.85f);
            visor.localScale = new Vector3(r * 1.4f, r * 0.45f, r * 0.5f);

            var net = Ensure<DSTNetPlayer>(root);
            net.body = new[] { body.GetComponent<Renderer>(), visor.GetComponent<Renderer>() };

            var saved = PrefabUtility.SaveAsPrefabAsset(root, DSTPlayerSetup.PrefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            return saved;
        }

        static Transform Primitive(Transform parent, string name, PrimitiveType type, Material mat)
        {
            var old = parent.Find(name);
            if (old) Object.DestroyImmediate(old.gameObject);
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());   // 碰撞交給 CharacterController
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        // ------------------------------------------------------------------ 車輛
        static void SetupVehiclePrefab(string path)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            var no = Ensure<NetworkObject>(root);
            no.DontDestroyWithOwner = true;          // 駕駛斷線，車留在原地交還主機
            var nt = Ensure<NetworkTransform>(root);
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;   // 誰開車誰跑物理
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
            Ensure<NetworkRigidbody>(root);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
        }

        // ------------------------------------------------------------------ 動物
        static void SetupAnimalPrefab(string path)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            Ensure<NetworkObject>(root);
            var nt = Ensure<NetworkTransform>(root);
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Server;  // AI 只在主機跑
            nt.SyncRotAngleX = nt.SyncRotAngleZ = false;
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
            var anim = root.GetComponentInChildren<Animator>();
            if (anim)
            {
                var na = Ensure<NetworkAnimator>(anim.gameObject);
                na.AuthorityMode = NetworkAnimator.AuthorityModes.Server;
                na.Animator = anim;
            }
            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
        }

        // NGO 會自動把有 NetworkObject 的 Prefab 收進 DefaultNetworkPrefabs；
        // 只有現有清單都沒收到的才補進自己的清單，重複登記會報 duplicate GlobalObjectIdHash
        static void RegisterPrefabs(NetworkManager nm, GameObject[] prefabs)
        {
            var lists = nm.NetworkConfig.Prefabs.NetworkPrefabsLists;
            lists.RemoveAll(l => l == null);
            var own = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(PrefabListPath);
            if (own)
            {
                lists.Remove(own);
                AssetDatabase.DeleteAsset(PrefabListPath);
                own = null;
            }
            foreach (var p in prefabs)
            {
                if (lists.Any(l => l.Contains(p))) continue;
                if (!own)
                {
                    own = ScriptableObject.CreateInstance<NetworkPrefabsList>();
                    AssetDatabase.CreateAsset(own, PrefabListPath);
                    lists.Add(own);
                }
                own.Add(new NetworkPrefab { Prefab = p });
                EditorUtility.SetDirty(own);
            }
            EditorUtility.SetDirty(nm);
        }

        // ------------------------------------------------------------------ 場景
        static NetworkManager SetupScene(GameObject playerPrefab)
        {
            // 玩家改成連線後才生成，場景裡那隻拿掉
            foreach (var pc in Object.FindObjectsByType<DSTPlayerController>(FindObjectsInactive.Include))
                if (pc.gameObject.scene.IsValid()) Undo.DestroyObjectImmediate(pc.gameObject);

            var go = GameObject.Find("NetworkManager");
            if (!go)
            {
                go = new GameObject("NetworkManager");
                Undo.RegisterCreatedObjectUndo(go, "Create NetworkManager");
            }
            var nm = Ensure<NetworkManager>(go);
            var utp = Ensure<UnityTransport>(go);
            nm.NetworkConfig ??= new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = utp;
            nm.NetworkConfig.PlayerPrefab = playerPrefab;

            var session = Ensure<DSTNetSession>(go);
            var lobby = GameObject.Find("Camera_Main");
            if (lobby) session.lobbyCamera = lobby.GetComponent<Camera>();
            EditorUtility.SetDirty(nm);
            EditorUtility.SetDirty(session);

            // 連線場景管理要求場景在 Build 清單裡，放第一個
            var scenePath = go.scene.path;
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != scenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            return nm;
        }
    }
}
