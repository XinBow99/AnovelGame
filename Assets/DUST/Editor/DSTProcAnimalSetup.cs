using System.IO;
using System.Linq;
using DUST.Animals;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.EditorTools
{
    // 程式生成物種：零件庫（Blender 匯出的 SK_DST_CreatureKit）匯入設定、只留旋轉的動畫、Animator、範本材質、連線 Prefab、場景生成器
    public static class DSTProcAnimalSetup
    {
        const string Dir = "Assets/DUST/Animals/Procedural";
        const string KitDir = "Assets/DUST/Animals/Kit";
        const string KitPath = KitDir + "/SK_DST_CreatureKit.fbx";
        const string ClipDir = KitDir + "/Clips";
        const string ControllerPath = KitDir + "/AC_CreatureKit.controller";
        const string FurDetailPath = KitDir + "/T_DST_FurDetail.png";
        const string MaterialPath = Dir + "/M_DST_ProcCreature.mat";
        const string SkinGraphPath = KitDir + "/SG_DST_AlienSkin.shadergraph";
        const string SkinMaterialPath = Dir + "/M_DST_AlienSkin.mat";
        const string PrefabPath = Dir + "/PF_Animal_Procedural.prefab";
        const string LocoFbx = "Assets/DUST/Player/ManedWolf/SK_DST_Player_ManedWolf_Locomotion.fbx";

        [MenuItem("DUST/Animals/Setup Procedural Species")]
        public static void Setup()
        {
            if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets/DUST/Animals", "Procedural");
            if (!AssetDatabase.IsValidFolder(ClipDir)) AssetDatabase.CreateFolder(KitDir, "Clips");

            SetupKitImporter();
            var controller = BuildController();
            var mat = BuildMaterial();
            var skin = BuildSkinMaterial();

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
            var pa = root.AddComponent<DSTProcAnimal>();
            pa.template = mat;
            pa.skin = skin;
            pa.kit = AssetDatabase.LoadAssetAtPath<GameObject>(KitPath);
            pa.controller = controller;
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

        static void SetupKitImporter()
        {
            var imp = (ModelImporter)AssetImporter.GetAtPath(KitPath);
            if (!imp) { Debug.LogError("找不到零件庫 " + KitPath + "（先在 Blender 匯出 DST_Creature_Kit）"); return; }
            imp.animationType = ModelImporterAnimationType.Generic;
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.bakeAxisConversion = true;
            imp.preserveHierarchy = true;
            imp.importAnimation = false;
            imp.materialImportMode = ModelImporterMaterialImportMode.None;
            imp.importCameras = false; imp.importLights = false;
            // 外皮 shader 讀 UV1 / UV2（原始座標、零件欄位）和頂點色：不能壓縮，也不能讓 Unity 拿 UV1 去產生光照貼圖 UV
            imp.meshCompression = ModelImporterMeshCompression.Off;
            imp.generateSecondaryUV = false;
            imp.SaveAndReimport();
        }

        // 鬃狼的移動動畫 → 只留旋轉（加上 Root / Hips 的位置）：執行時用骨頭位置調比例，不能被動畫蓋掉
        static AnimationClip RotationOnly(string take, bool loop)
        {
            var src = AssetDatabase.LoadAllAssetsAtPath(LocoFbx).OfType<AnimationClip>().First(c => c.name == take);
            var dst = new AnimationClip { name = take.Replace("MW2_", "CK_"), frameRate = src.frameRate };
            foreach (var b in AnimationUtility.GetCurveBindings(src))
            {
                bool rot = b.propertyName.StartsWith("m_LocalRotation");
                bool rootPos = b.propertyName.StartsWith("m_LocalPosition") && (b.path.EndsWith("/Root") || b.path.EndsWith("/Hips"));
                if (!rot && !rootPos) continue;
                AnimationUtility.SetEditorCurve(dst, b, AnimationUtility.GetEditorCurve(src, b));
            }
            var st = AnimationUtility.GetAnimationClipSettings(dst);
            st.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(dst, st);
            string path = ClipDir + "/" + dst.name + ".anim";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(dst, path);
            return dst;
        }

        static AnimatorController BuildController()
        {
            var idle = RotationOnly("MW2_Idle", true);
            var walk = RotationOnly("MW2_Walk", true);
            var run = RotationOnly("MW2_Run", true);
            var sniff = RotationOnly("MW2_Sniff", true);
            var alert = RotationOnly("MW2_Alert", true);

            AssetDatabase.DeleteAsset(ControllerPath);
            var ac = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            ac.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ac.AddParameter("Graze", AnimatorControllerParameterType.Bool);
            ac.AddParameter("Alert", AnimatorControllerParameterType.Bool);
            var sm = ac.layers[0].stateMachine;
            var loco = ac.CreateBlendTreeInController("Locomotion", out var tree, 0);
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.AddChild(idle, 0f);
            tree.AddChild(walk, 0.9f);
            tree.AddChild(run, 3f);
            tree.AddChild(run, 4.8f);
            sm.defaultState = loco;
            var graze = sm.AddState("Graze"); graze.motion = sniff;
            var watch = sm.AddState("Alert"); watch.motion = alert;
            void Link(AnimatorState a, AnimatorState b, string param, bool value)
            {
                var t = a.AddTransition(b);
                t.hasExitTime = false; t.duration = 0.25f;
                t.AddCondition(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f, param);
            }
            Link(loco, graze, "Graze", true); Link(graze, loco, "Graze", false);
            Link(loco, watch, "Alert", true); Link(watch, loco, "Alert", false);
            EditorUtility.SetDirty(ac);
            return ac;
        }

        // 毛流細紋：HDRP 的 Detail Map（R = 顏色調整、G = 法線 Y、B = 光滑度、A = 法線 X），沿 v 方向拉長的細條
        static Texture2D BuildFurDetail()
        {
            const int N = 256;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true);
            var h = new float[N * N];
            var r = new System.Random(5);
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float u = x / (float)N, v = y / (float)N;
                // 細毛紋理：只帶一點點方向性（圓柱投影到腿上時才不會變成一圈圈的環紋）
                float a = 0f;
                for (int o = 0; o < 3; o++)
                {
                    float f = 16 << o;
                    a += (Mathf.PerlinNoise(u * f + o * 31.1f, v * f * 0.55f + o * 13.7f) - 0.5f) / (o + 1);
                }
                h[y * N + x] = a;
            }
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = h[y * N + (x + 1) % N] - h[y * N + (x + N - 1) % N];
                float dy = h[((y + 1) % N) * N + x] - h[((y + N - 1) % N) * N + x];
                var n = new Vector3(-dx * 3f, -dy * 3f, 1f).normalized;
                float shade = 0.5f + h[y * N + x] * 0.35f;
                px[y * N + x] = new Color(shade, n.y * 0.5f + 0.5f, 0.5f, n.x * 0.5f + 0.5f);
            }
            tex.SetPixels(px); tex.Apply();
            File.WriteAllBytes(FurDetailPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(FurDetailPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(FurDetailPath);
            ti.sRGBTexture = false;
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(FurDetailPath);
        }

        // 程序外皮（SG_DST_AlienSkin：DSTAlienSkin.hlsl 接 HDRP Lit）：所有零件共用，物種 key 在執行時寫進 _SeedLo / _SeedHi
        static Material BuildSkinMaterial()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(SkinGraphPath);
            if (!shader) { Debug.LogWarning("找不到 " + SkinGraphPath + "，沿用舊的毛色貼圖"); return null; }
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkinMaterialPath);
            if (!mat)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, SkinMaterialPath);
            }
            mat.shader = shader;
            mat.SetFloat("_Detail", 1f);
            HDMaterial.ValidateMaterial(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material BuildMaterial()
        {
            var detail = BuildFurDetail();
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (!mat)
            {
                mat = new Material(Shader.Find("HDRP/Lit"));
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Smoothness", 0.22f);
            mat.SetTexture("_DetailMap", detail);
            mat.SetTextureScale("_DetailMap", new Vector2(16f, 12f));
            mat.SetFloat("_DetailAlbedoScale", 0.35f);
            mat.SetFloat("_DetailNormalScale", 0.6f);
            mat.SetFloat("_DetailSmoothnessScale", 0f);
            HDMaterial.ValidateMaterial(mat);     // 開啟 _DETAIL_MAP 關鍵字（打包時才會留下這個變體）
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
