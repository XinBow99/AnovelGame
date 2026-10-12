using DUST.Player;
using DUST.Vehicles;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.EditorTools
{
    // 建立玩家 Prefab：四足鬃狼（半尺寸世界）。外觀 / 動畫 / 動作系統由 DSTPlayerWolfSetup 準備，這裡只組 Prefab。
    public static class DSTPlayerSetup
    {
        public const string PrefabPath = "Assets/DUST/Player/PF_Player.prefab";

        // 世界是正常尺寸的 Scale 倍（載具、動物都一樣）
        public const float Scale = 0.5f;
        // 鬃狼：背高約 0.45 m、頭頂約 0.55 m；碰撞膠囊包住身體中段
        public const float Height = 0.55f;
        public const float Radius = 0.14f;
        const float EyeHeight = 0.44f;
        const float EyeForward = 0.22f;      // 眼睛在身體中心前方（四足）
        // 重力不變時，維持「同樣的動作節奏」速度要乘 √Scale（Froude 相似）
        static float SpeedScale => Mathf.Sqrt(Scale);

        [MenuItem("DUST/Player/Build Player Prefab")]
        public static GameObject BuildPrefab()
        {
            System.IO.Directory.CreateDirectory("Assets/DUST/Player");
            var go = new GameObject("PF_Player");

            var cc = go.AddComponent<CharacterController>();
            cc.height = Height;
            cc.radius = Radius;
            cc.center = Vector3.up * (Height * 0.5f);
            cc.stepOffset = 0.12f;
            cc.slopeLimit = 45f;
            cc.skinWidth = 0.02f;
            cc.minMoveDistance = 0f;

            // Head = 視線的俯仰軸；鏡頭放在子物件（第三人稱時鏡頭會離開眼睛的位置）
            var head = new GameObject("Head").transform;
            head.SetParent(go.transform, false);
            head.localPosition = new Vector3(0f, EyeHeight, EyeForward);
            var camGo = new GameObject("ViewCamera");
            camGo.transform.SetParent(head, false);
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<HDAdditionalCameraData>();
            camGo.AddComponent<AudioListener>();
            camGo.tag = "MainCamera";
            cam.fieldOfView = 75f;
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 3000f;

            var pc = go.AddComponent<DSTPlayerController>();
            pc.head = head;
            pc.standHeight = Height;
            pc.crouchHeight = 0.40f;
            pc.eyeBelowTop = Height - EyeHeight;
            pc.headForward = EyeForward;
            pc.walkSpeed = 4.2f * SpeedScale;
            pc.sprintSpeed = 6.8f * SpeedScale;
            pc.crouchSpeed = 2.2f * SpeedScale;
            pc.jumpHeight = 1.0f * Scale;

            var ia = go.AddComponent<DSTPlayerInteractor>();
            ia.viewCamera = cam;
            ia.reach = 3.0f * Scale + 0.6f;
            ia.proximity = 0.6f;

            // 鬃狼模型（走路 Animator + 動作系統）
            var lib = AssetDatabase.LoadAssetAtPath<DSTActionLibrary>(DSTPlayerWolfSetup.LibraryPath);
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(DSTPlayerWolfSetup.Fbx);
            if (fbx)
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
                model.name = "Model";
                model.transform.SetParent(go.transform, false);
                model.transform.localRotation = DSTWolfRig.ModelFlip;
                var anim = model.TryGetComponent<Animator>(out var a) ? a : model.AddComponent<Animator>();
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;   // 第一人稱只投影子時也要動
                var rig = go.AddComponent<DSTWolfRig>();
                rig.model = model.transform;
                rig.locomotion = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(DSTPlayerWolfSetup.ControllerPath);
            }
            else Debug.LogWarning("[DUST] 找不到鬃狼 FBX，先跑 DUST/Player/Build Maned Wolf Player");
            var view = go.AddComponent<DSTPlayerView>();
            view.viewCamera = cam;

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            Object.DestroyImmediate(go);
            return prefab;
        }

        [MenuItem("DUST/Player/Place Player In Open Scene")]
        public static GameObject PlaceInScene()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) ?? BuildPrefab();
            var old = GameObject.Find("PF_Player");
            if (old) Object.DestroyImmediate(old);
            var player = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

            var spawn = GameObject.Find("DR2_Spawn_T");
            if (spawn) player.transform.SetPositionAndRotation(spawn.transform.position + Vector3.up * 0.05f, spawn.transform.rotation);

            // 只能有一個主攝影機 / AudioListener：車輛攝影機平常關閉，上車才打開
            foreach (var chase in Object.FindObjectsByType<DSTChaseCamera>(FindObjectsInactive.Include))
            {
                chase.gameObject.tag = "Untagged";
                var al = chase.GetComponent<AudioListener>();
                if (al) Object.DestroyImmediate(al);
                chase.gameObject.SetActive(false);
            }
            Undo.RegisterCreatedObjectUndo(player, "Place Player");
            EditorSceneManager.MarkSceneDirty(player.scene);
            return player;
        }
    }
}
