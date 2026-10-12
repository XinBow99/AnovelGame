using DUST.Player;
using DUST.Vehicles;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.EditorTools
{
    // 建立第一人稱角色 Prefab，並放進目前開著的場景
    public static class DSTPlayerSetup
    {
        public const string PrefabPath = "Assets/DUST/Player/PF_Player.prefab";

        // 角色是正常成人（1.8 m）的 Scale 倍，跟載具同比例
        public const float Scale = 0.5f;
        public const float Height = 1.8f * Scale;
        public const float Radius = 0.35f * Scale;
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
            cc.stepOffset = 0.35f * Scale;
            cc.slopeLimit = 45f;
            cc.skinWidth = Mathf.Max(0.01f, 0.04f * Scale);
            cc.minMoveDistance = 0f;

            var head = new GameObject("Head").transform;
            head.SetParent(go.transform, false);
            head.localPosition = Vector3.up * (Height - 0.12f * Scale);
            var cam = head.gameObject.AddComponent<Camera>();
            head.gameObject.AddComponent<HDAdditionalCameraData>();
            head.gameObject.AddComponent<AudioListener>();
            head.gameObject.tag = "MainCamera";
            cam.fieldOfView = 75f;
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 3000f;

            var pc = go.AddComponent<DSTPlayerController>();
            pc.head = head;
            pc.standHeight = Height;
            pc.crouchHeight = 1.15f * Scale;
            pc.eyeBelowTop = 0.12f * Scale;
            pc.walkSpeed = 4.2f * SpeedScale;
            pc.sprintSpeed = 6.8f * SpeedScale;
            pc.crouchSpeed = 2.2f * SpeedScale;
            pc.jumpHeight = 1.0f * Scale;

            var ia = go.AddComponent<DSTPlayerInteractor>();
            ia.viewCamera = cam;
            ia.reach = 3.0f * Scale + 0.6f;
            ia.proximity = 1.6f * Scale;

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
