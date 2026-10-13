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
            var tracks = sp.GetComponent<DSTTracks>();
            if (!tracks) tracks = sp.gameObject.AddComponent<DSTTracks>();
            tracks.material = BuildFootprints();
            EditorUtility.SetDirty(tracks);

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

        // ------------------------------------------------------------------ 足跡（DSTTracks）：4 格圖集（肉墊 / 偶蹄 / 圓石掌 / 爪），顏色 + 法線，HDRP Decal 材質
        const string PrintColorPath = KitDir + "/T_DST_Footprints.png";
        const string PrintNormalPath = KitDir + "/T_DST_Footprints_N.png";
        const string PrintMaterialPath = Dir + "/M_DST_Footprint.mat";

        [MenuItem("DUST/Animals/Setup Footprints")]
        public static void SetupFootprints()
        {
            var mat = BuildFootprints();
            var sp = Object.FindAnyObjectByType<DSTProcSpawner>();
            if (!sp) { Debug.LogWarning("場景裡沒有 DSTProcSpawner（先跑 Setup Procedural Species）"); return; }
            var tr = sp.GetComponent<DSTTracks>();
            if (!tr) tr = sp.gameObject.AddComponent<DSTTracks>();
            tr.material = mat;
            EditorUtility.SetDirty(tr);
            EditorSceneManager.MarkSceneDirty(sp.gameObject.scene);
            EditorSceneManager.SaveScene(sp.gameObject.scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[DUST] Footprints ready: " + PrintMaterialPath);
        }

        // 腳印深度（0 = 平、1 = 最深），u 左右、v 前後（+v 朝前進方向），範圍 -1..1
        static float PrintDepth(int cell, float u, float v)
        {
            float E(float cx, float cy, float rx, float ry) { float dx = (u - cx) / rx, dy = (v - cy) / ry; return dx * dx + dy * dy; }
            // GLSL 的 smoothstep（Mathf.SmoothStep 是在兩個值之間內插，不是這個）
            float In(float e) { float t = Mathf.Clamp01((e - 0.6f) / 0.4f); return 1f - t * t * (3f - 2f * t); }
            float d = 0f;
            switch (cell)
            {
                case 0: // 犬科肉墊：掌墊 + 四個趾
                    d = In(E(0f, -0.3f, 0.42f, 0.32f));
                    d = Mathf.Max(d, In(E(-0.44f, 0.22f, 0.15f, 0.19f)), In(E(-0.15f, 0.52f, 0.15f, 0.2f)));
                    d = Mathf.Max(d, In(E(0.15f, 0.52f, 0.15f, 0.2f)), In(E(0.44f, 0.22f, 0.15f, 0.19f)));
                    d = Mathf.Max(d, 0.7f * In(E(-0.17f, 0.82f, 0.05f, 0.08f)), 0.7f * In(E(0.17f, 0.82f, 0.05f, 0.08f)));
                    break;
                case 1: // 偶蹄：兩瓣，前端收尖
                    for (int s = -1; s <= 1; s += 2)
                    {
                        float w = 0.22f * (1f - 0.55f * Mathf.Clamp01(v));
                        d = Mathf.Max(d, In(E(s * 0.24f, 0f, Mathf.Max(0.05f, w), 0.72f)));
                    }
                    break;
                case 2: // 圓石掌：大圓墊 + 三個鈍趾
                    d = In(E(0f, -0.12f, 0.6f, 0.55f));
                    d = Mathf.Max(d, In(E(-0.42f, 0.55f, 0.2f, 0.2f)), In(E(0f, 0.7f, 0.22f, 0.22f)), In(E(0.42f, 0.55f, 0.2f, 0.2f)));
                    break;
                default: // 穴居的爪：窄掌墊 + 五道爪痕
                    d = In(E(0f, -0.3f, 0.45f, 0.3f));
                    for (int c = 0; c < 5; c++)
                    {
                        float cx = -0.5f + c * 0.25f;
                        float ry = 0.24f, cy = 0.42f - Mathf.Abs(cx) * 0.3f;
                        float du = u - cx - (v - cy) * cx * 0.5f;          // 往外斜
                        float e = (du / 0.055f) * (du / 0.055f) + ((v - cy) / ry) * ((v - cy) / ry);
                        d = Mathf.Max(d, In(e));
                    }
                    break;
            }
            return d;
        }

        static Material BuildFootprints()
        {
            const int C = 128, W = C * 4;
            var h = new float[W * C];
            for (int cell = 0; cell < 4; cell++)
            for (int y = 0; y < C; y++)
            for (int x = 0; x < C; x++)
            {
                float u = (x + 0.5f) / C * 2f - 1f, v = (y + 0.5f) / C * 2f - 1f;
                // 腳印只用中間 85%，邊緣留空（投影框外不會被切到）
                h[y * W + cell * C + x] = PrintDepth(cell, u / 0.85f, v / 0.85f);
            }
            // 被擠出來的沙：腳印外圍一圈微微隆起（深度圖模糊後減掉原本的）
            var blur = new float[W * C];
            const int R = 5;
            for (int y = 0; y < C; y++)
            for (int x = 0; x < W; x++)
            {
                float s = 0f; int n = 0;
                int cell = x / C;
                for (int j = -R; j <= R; j++)
                for (int i = -R; i <= R; i++)
                {
                    int xx = x + i, yy = y + j;
                    if (yy < 0 || yy >= C || xx < cell * C || xx >= (cell + 1) * C) continue;
                    s += h[yy * W + xx]; n++;
                }
                blur[y * W + x] = s / n;
            }
            var col = new Color[W * C];
            var nrm = new Color[W * C];
            var height = new float[W * C];
            for (int i = 0; i < W * C; i++)
            {
                float rim = Mathf.Clamp01((blur[i] - h[i]) * 2.2f);
                height[i] = -h[i] + rim * 0.35f;
                // 坑底陰影要夠深才看得出來（Decal 是蓋上去的顏色，不是相乘）
                float a = Mathf.Clamp01(h[i] * 0.62f + rim * 0.3f);
                var c = Color.Lerp(new Color(0.62f, 0.51f, 0.38f), new Color(0.3f, 0.23f, 0.16f), Mathf.Sqrt(h[i]));
                col[i] = new Color(c.r, c.g, c.b, a);
            }
            for (int y = 0; y < C; y++)
            for (int x = 0; x < W; x++)
            {
                int cell = x / C;
                float Hh(int xx, int yy) => height[Mathf.Clamp(yy, 0, C - 1) * W + Mathf.Clamp(xx, cell * C, cell * C + C - 1)];
                float dx = Hh(x + 1, y) - Hh(x - 1, y), dy = Hh(x, y + 1) - Hh(x, y - 1);
                var n = new Vector3(-dx * 10f, -dy * 10f, 1f).normalized;
                nrm[y * W + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
            }
            Texture2D Save(string path, Color[] px, bool normal)
            {
                var t = new Texture2D(W, C, TextureFormat.RGBA32, false);
                t.SetPixels(px); t.Apply();
                File.WriteAllBytes(path, t.EncodeToPNG());
                Object.DestroyImmediate(t);
                AssetDatabase.ImportAsset(path);
                var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = !normal;
                ti.alphaIsTransparency = !normal;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.mipmapEnabled = true;
                ti.SaveAndReimport();
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            var colTex = Save(PrintColorPath, col, false);
            var nrmTex = Save(PrintNormalPath, nrm, true);

            var mat = AssetDatabase.LoadAssetAtPath<Material>(PrintMaterialPath);
            if (!mat)
            {
                mat = new Material(Shader.Find("HDRP/Decal"));
                AssetDatabase.CreateAsset(mat, PrintMaterialPath);
            }
            mat.SetTexture("_BaseColorMap", colTex);
            mat.SetTexture("_NormalMap", nrmTex);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_DecalBlend", 1f);
            mat.SetFloat("_NormalBlendSrc", 0f);       // 法線的不透明度跟著顏色貼圖的 alpha
            mat.SetFloat("_AffectAlbedo", 1f);
            mat.SetFloat("_AffectNormal", 1f);
            mat.SetFloat("_AffectMetal", 0f);
            mat.SetFloat("_AffectSmoothness", 0f);
            mat.SetFloat("_AffectAO", 0f);
            mat.enableInstancing = true;
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
