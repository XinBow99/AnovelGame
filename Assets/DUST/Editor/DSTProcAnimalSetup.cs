using DUST.Animals;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace DUST.EditorTools
{
    // 程式生成物種：範本材質、連線 Prefab（外型執行時才組）、場景裡的生成器
    public static class DSTProcAnimalSetup
    {
        const string Dir = "Assets/DUST/Animals/Procedural";
        const string MaterialPath = Dir + "/M_DST_ProcCreature.mat";
        const string PrefabPath = Dir + "/PF_Animal_Procedural.prefab";

        [MenuItem("DUST/Animals/Setup Procedural Species")]
        public static void Setup()
        {
            if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets/DUST/Animals", "Procedural");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (!mat)
            {
                mat = new Material(Shader.Find("HDRP/Lit"));
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            mat.SetFloat("_Smoothness", 0.22f);
            mat.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(mat);

            var root = new GameObject("PF_Animal_Procedural");
            root.AddComponent<NetworkObject>();
            var nt = root.AddComponent<NetworkTransform>();
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Server;   // AI 只在主機跑
            nt.SyncRotAngleX = nt.SyncRotAngleZ = false;
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
            var col = root.AddComponent<CapsuleCollider>();               // 大小在 DSTProcAnimal 依基因設定
            col.direction = 2;
            var agent = root.AddComponent<NavMeshAgent>();
            agent.stoppingDistance = 0.15f;
            agent.autoBraking = true;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
            root.AddComponent<DSTAnimal>();
            root.AddComponent<DSTProcAnimal>().template = mat;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);

            var sp = Object.FindAnyObjectByType<DSTProcSpawner>();
            if (!sp)
            {
                var old = Object.FindAnyObjectByType<DSTAnimalSpawner>();
                sp = new GameObject("ProcAnimalSpawner").AddComponent<DSTProcSpawner>();
                if (old) sp.transform.SetParent(old.transform.parent, false);
            }
            sp.prefab = prefab;
            EditorUtility.SetDirty(sp);

            var nm = Object.FindAnyObjectByType<NetworkManager>();
            if (nm) DSTNetSetup.RegisterPrefabs(nm, new[] { prefab });
            EditorSceneManager.MarkSceneDirty(sp.gameObject.scene);
            EditorSceneManager.SaveScene(sp.gameObject.scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[DUST] Procedural species ready: " + PrefabPath);
        }
    }
}
