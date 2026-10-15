using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using DUST.Vehicles;

namespace DUST.EditorTools
{
    // 米格-25（issue #7）：Blender 匯出的 SM_DST_Vehicle_MiG25.fbx → HDRP 材質 + Prefab。
    // Blender 原始檔：DUST/blender/SM_DST_Vehicle_MiG25.blend
    // 重建 / 匯出腳本：Tools/Blender/mig25_build.py、mig25_export_unity.py
    // FBX 是實機尺寸（全長約 23 m），這裡用 importer 的 globalScale 0.5 縮成半尺寸世界，
    // 所以各活動零件的 transform 縮放都還是 1。
    // 階層：MiG25_Root > MiG25_Airframe（不動的部分）> 29 個活動零件，原點 = 樞紐，一律繞本地 X 軸轉；
    // 輪子是起落架的子物件。機頭朝 +Z，_L = 飛行員左邊（-X）。
    // Unity 裡繞本地 X 轉 +角度的效果（2026-10-15 匯入後實測；Blender 裡正負號相反）：
    //   Flap / Aileron* / Stabilator   + = 後緣往下（左右同號；副翼左右給相反角度）
    //   Rudder_L / _R                  + = 後緣往左（-X），左右同號 = 一起偏航
    //   CanopyHood                     + = 打開（鉸鏈在右側窗沿）
    //   NoseGear / MainGear_*          + = 往前收起
    //   MainDoor*_L / NoseDoor_L       + = 往外打開；_R 側相反（- 打開）
    //   IntakeRamp_*                   + = 楔形板往下壓
    //   Nozzle_*                       不轉，縮放本地 Y/Z（< 1 = 收口）
    //   *Wheel*                        繞本地 X 轉 = 滾動
    public static class DSTMiG25Setup
    {
        const string Dir = "Assets/DUST/Vehicles/MiG25";
        const string Fbx = Dir + "/SM_DST_Vehicle_MiG25.fbx";
        const string MatDir = Dir + "/Materials";
        const string TexDir = Dir + "/Textures";
        public const string PrefabPath = Dir + "/PF_Vehicle_MiG25.prefab";

        [MenuItem("DUST/Vehicles/Build MiG-25 Prefab")]
        public static GameObject Build()
        {
            SetupImporter();
            // FBX 的根節點帶著 Blender 的 90° X 軸向旋轉（bakeAxisConversion 只轉檔案座標系），
            // 直接把它當 Prefab 根、再設 rotation 會把整架飛機立起來。
            // 所以外面包一層乾淨的根，飛控 / 擺放只動這一層。
            var go = new GameObject("PF_Vehicle_MiG25");
            var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Fbx));
            model.name = "Model";
            model.transform.SetParent(go.transform, false);

            // ---- 物理：實機約 37 t，半尺寸世界質量 ×1/8
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.maxAngularVelocity = 4f;
            AddColliders(go.transform);

            // 重心：主輪前面一點（實機約在機身長度 57% 處）
            var com = Child(go.transform, "CenterOfMass", new Vector3(0f, 1.15f, -1.0f));
            var eye = Child(go.transform, "Seat_Driver_Eye", new Vector3(0f, 1.70f, 2.12f));   // 坐姿眼高：比遮光罩高 ~18 cm（1.59 時只高 7 cm，畫面被儀表板塞滿，實測）
            eye.localRotation = Quaternion.Euler(6f, 0f, 0f);   // 往下看 8°：儀表板在畫面下緣、還看得到機鼻

            var jet = go.AddComponent<DSTJetController>();
            jet.centerOfMass = com;
            var fx = go.AddComponent<DSTJetFX>();
            fx.flameMaterial = FlameMaterial();
            fx.hazeMaterial = HazeMaterial();
            fx.contrailMaterial = SoftMaterial("M_DST_MiG25_Contrail", "T_DST_MiG25_Contrail", 256, 16, (u2, v2) =>
                // 沿長度：剛噴出來的一小段透明（水氣還沒凝結），之後慢慢淡掉；橫向：兩邊軟邊
                Mathf.SmoothStep(0f, 1f, u2 / 0.04f) * Mathf.Pow(1f - u2, 1.4f) * Mathf.Sin(v2 * Mathf.PI), 0.85f);
            fx.vaporMaterial = SoftMaterial("M_DST_MiG25_Vapor", "T_DST_MiG25_Vapor", 64, 64, (u2, v2) =>
            {
                // 音障雲的霧粒子：圓形、邊緣很軟、中間有一點雲絮紋路（很多顆疊起來才是一團）
                float dx = u2 - 0.5f, dy = v2 - 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                float soft = Mathf.Pow(Mathf.Clamp01(1f - r), 1.6f);
                return soft * (0.75f + 0.25f * Mathf.PerlinNoise(u2 * 6f + 3f, v2 * 6f));
            }, 0.18f);
            fx.dustMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/DUST/Clockwork/M_DST_StormDust.mat");
            if (!fx.dustMaterial) Debug.LogWarning("[DUST] 找不到 M_DST_StormDust（先跑 Clockwork setup），跑道揚沙不會出現");
            go.AddComponent<DSTPhysicsClaimer>();

            // 上下機：左邊座艙旁、右邊、機頭前
            var entry = go.AddComponent<DSTVehicleEntry>();
            entry.vehicle = jet;
            entry.maxExitSpeedKmh = 8f;
            entry.chaseDistance = 17f;
            entry.chaseHeight = 4.5f;
            entry.exitPoints = new[]
            {
                Child(go.transform, "Exit_Left", new Vector3(-2.3f, 0f, 2.0f)),
                Child(go.transform, "Exit_Right", new Vector3(2.3f, 0f, 2.0f)),
                Child(go.transform, "Exit_Front", new Vector3(0f, 0f, 7.6f)),
            };

            PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            Object.DestroyImmediate(go);
            DSTNetSetup.SetupVehiclePrefab(PrefabPath);   // NetworkObject / NetworkTransform（誰開誰跑物理）
            Debug.Log("[DUST] built " + PrefabPath);
            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        const float Mass = 4600f;

        static Transform Child(Transform root, string name, Vector3 local)
        {
            var t = new GameObject(name).transform;
            t.SetParent(root, false);
            t.localPosition = local;
            return t;
        }

        // 簡化碰撞體（半尺寸、機頭 +Z）：機身膠囊、進氣道 / 發動機箱、左右機翼（順著後掠擺）、雙垂尾、水平尾翼。
        // 起落架不放碰撞體，地面接觸由 DSTJetController 的懸吊射線負責；機腹離地 0.72 m。
        static void AddColliders(Transform root)
        {
            var cols = new GameObject("Colliders").transform;
            cols.SetParent(root, false);
            var body = cols.gameObject.AddComponent<CapsuleCollider>();
            body.direction = 2;
            body.center = new Vector3(0f, 1.17f, 0.2f);
            body.radius = 0.45f;
            body.height = 12.0f;
            var ducts = cols.gameObject.AddComponent<BoxCollider>();
            ducts.center = new Vector3(0f, 1.18f, -2.15f);
            ducts.size = new Vector3(2.05f, 0.9f, 6.3f);
            foreach (int side in new[] { -1, 1 })
            {
                // 翼根弦中點 → 翼尖弦中點
                Box(cols, "Wing", new Vector3(side * 0.6f, 1.53f, -2.19f), new Vector3(side * 3.5f, 1.33f, -3.71f), 2.1f, 0.14f);
                Box(cols, "Stab", new Vector3(side * 0.8f, 1.06f, -5.1f), new Vector3(side * 2.2f, 0.96f, -5.95f), 1.2f, 0.08f);
                var fin = new GameObject("Fin").AddComponent<BoxCollider>();
                fin.transform.SetParent(cols, false);
                fin.transform.localPosition = new Vector3(side * 0.66f, 2.25f, -4.45f);
                fin.transform.localRotation = Quaternion.Euler(0f, 0f, -side * 8f);
                fin.size = new Vector3(0.1f, 1.5f, 1.6f);
            }
        }

        // 盒子的本地 X 沿著 a→b（翼展方向），Z = 弦長方向
        static void Box(Transform parent, string name, Vector3 a, Vector3 b, float width, float thick)
        {
            var c = new GameObject(name).AddComponent<BoxCollider>();
            c.transform.SetParent(parent, false);
            c.transform.localPosition = (a + b) * 0.5f;
            c.transform.localRotation = Quaternion.FromToRotation(Vector3.right * Mathf.Sign(b.x - a.x), (b - a).normalized);
            c.size = new Vector3((b - a).magnitude, thick, width);
        }

        // 後燃器：HDRP Unlit 加法混合；貼圖沿長度變暗，中間幾道震波環（亮度在執行期用 MaterialPropertyBlock 給）
        static Material FlameMaterial()
        {
            const int W = 128, H = 8;
            string texPath = TexDir + "/T_DST_MiG25_Flame.png";
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
            for (int x = 0; x < W; x++)
            {
                float u = x / (W - 1f);
                float body = Mathf.Exp(-2.6f * u) * (1f - Mathf.SmoothStep(0.75f, 1f, u));
                float diamonds = 0.65f + 0.35f * Mathf.Pow(Mathf.Cos(u * Mathf.PI * 9f), 2f) * (1f - u);
                float k = Mathf.Clamp01(body * diamonds);
                for (int y = 0; y < H; y++) t.SetPixel(x, y, new Color(k, k, k, k));
            }
            t.Apply();
            System.IO.File.WriteAllBytes(texPath, t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(texPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(texPath);
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.sRGBTexture = false;
            ti.mipmapEnabled = false;
            ti.SaveAndReimport();

            string path = MatDir + "/M_DST_MiG25_Flame.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m)
            {
                m = new Material(Shader.Find("HDRP/Unlit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetTexture("_UnlitColorMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            m.SetColor("_UnlitColor", Color.black);
            // 亮度由 DSTJetFX 依目前曝光算好塞進 _UnlitColor（自發光的 exposure weight 在 MaterialPropertyBlock 下沒作用）
            m.SetTexture("_EmissiveColorMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            m.SetColor("_EmissiveColor", Color.black);
            m.SetFloat("_UseEmissiveIntensity", 0f);
            m.SetFloat("_EmissiveExposureWeight", 0f);
            m.SetFloat("_SurfaceType", 1f);
            m.SetFloat("_BlendMode", 1f);                 // Additive
            m.SetFloat("_EnableFogOnTransparent", 1f);
            m.SetFloat("_DoubleSidedEnable", 1f);      // 雙面：噴口光盤從後面看才看得到（亮度已經壓低，兩層疊加沒關係）
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        // 場景裡的停放位置：遺跡二沒有跑道，米格-25 在那裡會整架藏起來（DSTJetController），
        // 進隨機世界時 DSTWorld.Relocate 把它停到跑道頭。這裡只找一塊放得下（13 × 19 m）的空地，
        // 免得藏起來之前那一兩幀卡進牆裡。已經有就換成新的（保留原位置）。
        [MenuItem("DUST/Vehicles/Place MiG-25 In Open Scene")]
        public static GameObject PlaceInScene()
        {
            var prefab = Build();
            Vector3 pos = Vector3.zero;
            float yaw = 0f;
            var old = GameObject.Find("PF_Vehicle_MiG25");
            if (old)
            {
                pos = old.transform.position;
                yaw = old.transform.eulerAngles.y;
                Undo.DestroyObjectImmediate(old);
            }
            else if (!FindOpenSpot(out pos, out yaw))
                Debug.LogWarning("[DUST] 場景裡找不到放得下米格-25 的空地，先放在原點");
            var jet = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            jet.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            Undo.RegisterCreatedObjectUndo(jet, "Place MiG-25");
            EditorSceneManager.MarkSceneDirty(jet.scene);
            return jet;
        }

        static bool FindOpenSpot(out Vector3 best, out float bestYaw)
        {
            best = Vector3.zero; bestYaw = 0f;
            var floors = GameObject.Find("DR2_Map_Floors");
            if (!floors) return false;
            var cols = floors.GetComponentsInChildren<Collider>();
            if (cols.Length == 0) return false;
            var b = cols[0].bounds;
            foreach (var c in cols) b.Encapsulate(c.bounds);
            float bestScore = -1f;
            for (float x = b.min.x + 10; x < b.max.x - 10; x += 4)
            for (float z = b.min.z + 10; z < b.max.z - 10; z += 4)
            {
                if (!Physics.Raycast(new Vector3(x, b.max.y + 30, z), Vector3.down, out var hit, 200f, ~0, QueryTriggerInteraction.Ignore)) continue;
                foreach (var yaw in new[] { 0f, 90f, 45f, 135f })
                {
                    if (Physics.CheckBox(hit.point + Vector3.up * 2.4f, new Vector3(6.5f, 2.0f, 9.5f), Quaternion.Euler(0, yaw, 0), ~0, QueryTriggerInteraction.Ignore)) continue;
                    float score = 1000f - new Vector2(x - b.center.x, z - b.center.z).magnitude;
                    if (score > bestScore) { bestScore = score; best = hit.point; bestYaw = yaw; }
                    break;
                }
            }
            return bestScore >= 0f;
        }

        static void SetupImporter()
        {
            var baseMap = Tex("T_DST_MiG25_Skin_BaseColor", srgb: true);
            var normal = Tex("T_DST_MiG25_Skin_Normal", normal: true);
            var mask = Tex("T_DST_MiG25_Skin_Mask", srgb: false);
            var insignia = Tex("T_DST_MiG25_Insignia", srgb: true, alpha: true, clamp: true);

            // Blender 材質名 → Unity 材質
            var remap = new (string src, Material dst)[]
            {
                ("M_MiG_Skin", TwoSided(Skin("M_DST_MiG25_Skin", Color.white, baseMap, normal, mask))),
                ("M_MiG_Frame", TwoSided(Skin("M_DST_MiG25_Frame", new Color(0.8f, 0.8f, 0.8f), baseMap, normal, mask))),
                ("M_MiG_Radome", DSTVehicleSetup.LitAt(MatDir, "M_DST_MiG25_Radome", new Color(0.16f, 0.17f, 0.16f), 0f, 0.45f)),
                ("M_MiG_DuctDark", TwoSided(DSTVehicleSetup.LitAt(MatDir, "M_DST_MiG25_DuctDark", new Color(0.02f, 0.02f, 0.02f), 0f, 0.1f))),
                ("M_MiG_Canopy", TwoSided(DSTVehicleSetup.LitAt(MatDir, "M_DST_MiG25_Canopy", new Color(0.05f, 0.07f, 0.08f, 0.3f), 0f, 0.95f, 0f, true))),
                // 座艙：蘇聯式的綠松石色、深色座椅 / 儀表、黃黑彈射拉環、儀表板貼圖
                ("M_MiG_Cockpit", TwoSided(DSTVehicleSetup.LitAt(MatDir, "M_DST_MiG25_Cockpit", new Color(0.20f, 0.42f, 0.42f), 0f, 0.35f))),
                ("M_MiG_CockpitDark", TwoSided(DSTVehicleSetup.LitAt(MatDir, "M_DST_MiG25_CockpitDark", new Color(0.05f, 0.06f, 0.06f), 0f, 0.3f))),
                ("M_MiG_EjectHandle", DSTVehicleSetup.LitAt(MatDir, "M_DST_MiG25_EjectHandle", new Color(0.8f, 0.6f, 0.05f), 0f, 0.5f)),
                ("M_MiG_Panel", PanelMaterial(Tex("T_DST_MiG25_Panel", srgb: true, clamp: true))),
                ("M_MiG_Nozzle", TwoSided(DSTVehicleSetup.LitAt(MatDir, "M_DST_MiG25_Nozzle", new Color(0.18f, 0.15f, 0.13f), 0.9f, 0.45f))),   // 噴口現在是開口的管子，裡外都要畫
                ("M_MiG_Tire", DSTVehicleSetup.LitAt(MatDir, "M_DST_MiG25_Tire", new Color(0.03f, 0.03f, 0.03f), 0f, 0.15f)),
                ("M_MiG_Gear", DSTVehicleSetup.LitAt(MatDir, "M_DST_MiG25_Gear", new Color(0.55f, 0.57f, 0.55f), 0.6f, 0.6f)),
                ("M_MiG_IntakeInner", DSTVehicleSetup.LitAt(MatDir, "M_DST_MiG25_IntakeInner", new Color(0.20f, 0.21f, 0.21f), 0.5f, 0.4f)),
                ("M_MiG_Insignia", Insignia("M_DST_MiG25_Insignia", insignia)),
            };

            var mi = (ModelImporter)AssetImporter.GetAtPath(Fbx);
            mi.globalScale = 0.5f;                 // 半尺寸世界
            mi.useFileScale = true;
            mi.bakeAxisConversion = true;          // 軸向轉換交給 Unity（Blender 的 bake space transform 會漏掉活動零件的旋轉）
            mi.addCollider = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.importTangents = ModelImporterTangents.CalculateMikk;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            foreach (var (src, dst) in remap)
                mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), src), dst);
            mi.SaveAndReimport();
            AssetDatabase.SaveAssets();
        }

        // 熱浪：只做畫面扭曲（HDRP distortion），扭曲向量用平滑的雜訊（R/G = 0.5 ± 偏移）
        static Material HazeMaterial()
        {
            const int N = 128;
            string texPath = TexDir + "/T_DST_MiG25_HeatHaze.png";
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float u = x / (float)N, v = y / (float)N;
                // 沿噴流方向（u）拉長的雜訊，尾端淡掉、靠中心最強
                float fade = Mathf.Sin(v * Mathf.PI) * (1f - u) * Mathf.SmoothStep(0f, 0.08f, u);
                float nx = Mathf.PerlinNoise(u * 6f, v * 14f) - 0.5f, ny = Mathf.PerlinNoise(u * 6f + 31f, v * 14f + 7f) - 0.5f;
                tex.SetPixel(x, y, new Color(0.5f + nx * fade, 0.5f + ny * fade, fade, 1f));
            }
            tex.Apply();
            System.IO.File.WriteAllBytes(texPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(texPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(texPath);
            ti.sRGBTexture = false;
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.SaveAndReimport();

            string path = MatDir + "/M_DST_MiG25_HeatHaze.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m)
            {
                m = new Material(Shader.Find("HDRP/Unlit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetFloat("_SurfaceType", 1f);
            m.SetFloat("_DistortionEnable", 1f);
            m.SetFloat("_DistortionOnly", 1f);
            m.SetFloat("_DistortionDepthTest", 1f);
            m.SetTexture("_DistortionVectorMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            m.SetFloat("_DistortionVectorScale", 2f);
            m.SetFloat("_DistortionVectorBias", -1f);
            m.SetFloat("_DistortionScale", 0.3f);
            m.SetFloat("_DistortionBlurScale", 0.5f);
            m.SetFloat("_DoubleSidedEnable", 1f);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material PanelMaterial(Texture2D tex)
        {
            var m = DSTVehicleSetup.LitAt(MatDir, "M_DST_MiG25_Panel", Color.white, 0f, 0.45f);
            m.SetTexture("_BaseColorMap", tex);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        // 白色半透明（alpha 混合）的 HDRP Unlit：貼圖 alpha 決定形狀，執行期用 _UnlitColor 的 alpha 控制濃淡
        static Material SoftMaterial(string name, string texName, int w, int h, System.Func<float, float, float> alpha, float baseAlpha)
        {
            string texPath = TexDir + "/" + texName + ".png";
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(alpha(x / (w - 1f), y / (h - 1f)))));
            t.Apply();
            System.IO.File.WriteAllBytes(texPath, t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(texPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(texPath);
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.alphaIsTransparency = true;
            ti.SaveAndReimport();
            string path = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m)
            {
                m = new Material(Shader.Find("HDRP/Unlit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetTexture("_UnlitColorMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            m.SetColor("_UnlitColor", new Color(0.95f, 0.96f, 0.98f, baseAlpha));
            m.SetFloat("_SurfaceType", 1f);
            m.SetFloat("_BlendMode", 0f);                 // Alpha
            m.SetFloat("_EnableFogOnTransparent", 1f);
            m.SetFloat("_DoubleSidedEnable", 1f);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material TwoSided(Material m)
        {
            m.SetFloat("_DoubleSidedEnable", 1f);   // 座艙裡看得到座艙罩框、後段金屬的內側
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Texture2D Tex(string name, bool srgb = true, bool normal = false, bool alpha = false, bool clamp = false)
        {
            string path = TexDir + "/" + name + ".png";
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            ti.sRGBTexture = !normal && srgb;
            ti.alphaIsTransparency = alpha;
            ti.wrapMode = clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            ti.mipmapEnabled = true;
            ti.anisoLevel = 8;                     // 機身大片蒙皮常常斜著看
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // 不鏽鋼蒙皮：UV 是 Blender 依實際公尺做的方塊投影（1 UV = 4 m 一格），貼圖直接重複
        static Material Skin(string name, Color tint, Texture2D baseMap, Texture2D normal, Texture2D mask)
        {
            var m = DSTVehicleSetup.LitAt(MatDir, name, tint, 0.55f, 0.5f);
            m.SetTexture("_BaseColorMap", baseMap);
            m.SetTexture("_NormalMap", normal);
            m.SetFloat("_NormalScale", 1f);
            m.SetTexture("_MaskMap", mask);
            m.SetFloat("_MetallicRemapMin", 0f);
            m.SetFloat("_MetallicRemapMax", 1f);
            m.SetFloat("_SmoothnessRemapMin", 0f);
            m.SetFloat("_SmoothnessRemapMax", 1f);
            m.SetFloat("_AORemapMin", 0f);
            m.SetFloat("_AORemapMax", 1f);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        // DUST 徽章：圓片貼在垂尾外側，掉漆的地方 alpha = 0 → 用 alpha clip 露出底下蒙皮
        static Material Insignia(string name, Texture2D tex)
        {
            var m = DSTVehicleSetup.LitAt(MatDir, name, Color.white, 0f, 0.3f);
            m.SetTexture("_BaseColorMap", tex);
            m.SetFloat("_AlphaCutoffEnable", 1f);
            m.SetFloat("_AlphaCutoff", 0.5f);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
