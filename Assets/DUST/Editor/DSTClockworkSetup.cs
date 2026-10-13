using System.IO;
using DUST.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.EditorTools
{
    // 發條沙漠（DSTClockwork）：沙暴 / 流星雨 / 隕石 / 遺跡刻文用到的貼圖與材質，和場景裡的 Clockwork 物件
    public static class DSTClockworkSetup
    {
        const string Dir = "Assets/DUST/Clockwork";

        [MenuItem("DUST/World/Setup Clockwork Desert")]
        public static void Setup()
        {
            if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets/DUST", "Clockwork");

            var dust = Unlit("M_DST_StormDust", Save("T_DST_Dust", 64, 64, Dot, false), new Color(0.82f, 0.62f, 0.42f, 0.32f), additive: false, fog: true);
            var meteor = Unlit("M_DST_Meteor", Save("T_DST_Streak", 128, 32, Streak, false), Color.white, additive: true, fog: false);
            var glow = Unlit("M_DST_Glow", Save("T_DST_Glow", 8, 32, (u, v) => new Color(1f, 1f, 1f, Mathf.Exp(-Mathf.Pow((v - 0.5f) * 5f, 2f))), false), Color.white, additive: true, fog: false);
            var dome = Unlit("M_DST_Dome", Save("T_DST_Dome", 256, 128, Dome, false), Color.white, additive: false, fog: false);
            var rock = MeteoriteMaterial();
            var scorch = Decal("M_DST_Scorch", Save("T_DST_Scorch", 256, 256, Scorch, false), null);
            var runes = Runes(out var runesN);
            var inscription = Decal("M_DST_Inscription", runes, runesN);

            var world = Object.FindAnyObjectByType<DSTWorld>();
            if (world && world.palette)
            {
                world.palette.inscription = inscription;
                EditorUtility.SetDirty(world.palette);
            }
            else Debug.LogWarning("[DUST] 找不到 DSTWorld 的 palette，刻文材質沒有指定");

            var go = GameObject.Find("Clockwork");
            if (!go) go = new GameObject("Clockwork");
            var weather = go.GetComponent<DSTWeather>();
            if (!weather) weather = go.AddComponent<DSTWeather>();
            weather.dustMaterial = dust;
            var sky = go.GetComponent<DSTSkyEvents>();
            if (!sky) sky = go.AddComponent<DSTSkyEvents>();
            sky.streakMaterial = meteor;
            sky.meteoriteMaterial = rock;
            sky.scorchMaterial = scorch;
            var anomalies = go.GetComponent<DSTAnomalies>();
            if (!anomalies) anomalies = go.AddComponent<DSTAnomalies>();
            anomalies.glowMaterial = glow;
            anomalies.dustMaterial = dust;
            EditorUtility.SetDirty(anomalies);
            var domes = go.GetComponent<DSTRegionDomes>();
            if (!domes) domes = go.AddComponent<DSTRegionDomes>();
            domes.domeMaterial = dome;
            EditorUtility.SetDirty(domes);
            EditorUtility.SetDirty(weather);
            EditorUtility.SetDirty(sky);
            EditorSceneManager.MarkSceneDirty(go.scene);
            EditorSceneManager.SaveScene(go.scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[DUST] Clockwork desert ready");
        }

        // ------------------------------------------------------------------ 貼圖
        delegate Color Pixel(float u, float v);

        static Texture2D Save(string name, int w, int h, Pixel f, bool normal, bool mips = true)
        {
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = f((x + 0.5f) / w, (y + 0.5f) / h);
            return Write(name, w, h, px, normal, mips);
        }

        static Texture2D Write(string name, int w, int h, Color[] px, bool normal, bool mips = true)
        {
            string path = Dir + "/" + name + ".png";
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.SetPixels(px); t.Apply();
            File.WriteAllBytes(path, t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            ti.sRGBTexture = !normal;
            ti.alphaIsTransparency = !normal;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.mipmapEnabled = mips;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }

        static Color Dot(float u, float v)
        {
            float r = new Vector2(u - 0.5f, v - 0.5f).magnitude * 2f;
            return new Color(1f, 1f, 1f, 1f - Smooth(0.2f, 1f, r));
        }

        // 流星：u 沿著長度（0 尾 → 1 頭），v 橫向
        // 遠方的沙牆 / 異象霧罩：底下濃、往上淡，頂端是翻騰的雲頭（u 方向首尾相接）
        static Color Dome(float u, float v)
        {
            float tau = Mathf.PI * 2f;
            float n = 0.5f + 0.22f * Mathf.Sin(tau * 3f * u + v * 4f) + 0.16f * Mathf.Sin(tau * 7f * u - v * 9f) + 0.12f * Mathf.Sin(tau * 13f * u + v * 15f);
            float top = 0.55f + 0.35f * n;                                  // 雲頭的高度
            float a = Mathf.Pow(Mathf.Clamp01(1f - v / Mathf.Max(0.05f, top)), 0.35f);
            a *= 0.78f + 0.22f * n;                                         // 一條條的濃淡
            float shade = 0.82f + 0.18f * n;                                // 亮暗不一，看起來有體積
            return new Color(shade, shade, shade, a);
        }

        static Color Streak(float u, float v)
        {
            float across = Mathf.Exp(-Mathf.Pow((v - 0.5f) * 5f, 2f));
            float along = Mathf.Pow(u, 1.8f) * (1f - Smooth(0.93f, 1f, u) * 0.6f);
            float k = across * along;
            return new Color(1f, 1f, 1f, k);
        }

        // 焦痕：中心最黑、邊緣有放射狀的噴濺
        static Color Scorch(float u, float v)
        {
            var d = new Vector2(u - 0.5f, v - 0.5f);
            float r = d.magnitude * 2f, a = Mathf.Atan2(d.y, d.x);
            float rays = 0.75f + 0.25f * Mathf.PerlinNoise(a * 3f + 10f, r * 2f);
            float k = 1f - Smooth(0.25f * rays, 0.95f * rays, r);
            k *= 0.6f + 0.4f * Mathf.PerlinNoise(u * 9f, v * 9f);
            return new Color(0.06f, 0.045f, 0.035f, Mathf.Clamp01(k * 0.95f));
        }

        // 刻文：8 塊碑文（4×2），每塊 3 欄 × 6 行符文；每個符文是 3×3 格點上隨機連 2~4 筆
        static Texture2D Runes(out Texture2D normal)
        {
            const int W = 1024, H = 1024, PW = 256, PH = 512;
            var h = new float[W * H];
            var rng = new System.Random(4242);
            for (int panel = 0; panel < 8; panel++)
            {
                int ox = panel % 4 * PW, oy = panel / 4 * PH;
                for (int col = 0; col < 3; col++)
                for (int row = 0; row < 6; row++)
                {
                    float cx = ox + 48 + col * 80, cy = oy + 52 + row * 80;
                    var pts = new Vector2[9];
                    for (int i = 0; i < 9; i++) pts[i] = new Vector2(cx + (i % 3 - 1) * 22, cy + (i / 3 - 1) * 26);
                    int strokes = 2 + rng.Next(3);
                    int at = rng.Next(9);
                    for (int s = 0; s < strokes; s++)
                    {
                        int to = rng.Next(9);
                        if (to == at) to = (to + 4) % 9;
                        Line(h, W, H, pts[at], pts[to], 4.5f);
                        at = rng.Next(2) == 0 ? to : rng.Next(9);
                    }
                    if (rng.NextDouble() < 0.3) Line(h, W, H, pts[rng.Next(9)], pts[rng.Next(9)] + Vector2.one * 0.01f, 6f);   // 點
                }
            }
            var col2 = new Color[W * H];
            var nrm = new Color[W * H];
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float d = h[y * W + x];
                col2[y * W + x] = new Color(0.2f, 0.15f, 0.11f, d * 0.85f);
                float dx = h[y * W + Mathf.Min(x + 1, W - 1)] - h[y * W + Mathf.Max(x - 1, 0)];
                float dy = h[Mathf.Min(y + 1, H - 1) * W + x] - h[Mathf.Max(y - 1, 0) * W + x];
                var n = new Vector3(dx * 2.5f, dy * 2.5f, 1f).normalized;     // 刻進去（深度 = -h）
                nrm[y * W + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
            }
            normal = Write("T_DST_Runes_N", W, H, nrm, true);
            return Write("T_DST_Runes", W, H, col2, false);
        }

        static void Line(float[] h, int W, int H, Vector2 a, Vector2 b, float width)
        {
            int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) - width - 1), x1 = Mathf.CeilToInt(Mathf.Max(a.x, b.x) + width + 1);
            int y0 = Mathf.FloorToInt(Mathf.Min(a.y, b.y) - width - 1), y1 = Mathf.CeilToInt(Mathf.Max(a.y, b.y) + width + 1);
            var ab = b - a; float len2 = Mathf.Max(1e-4f, ab.sqrMagnitude);
            for (int y = Mathf.Max(0, y0); y <= Mathf.Min(H - 1, y1); y++)
            for (int x = Mathf.Max(0, x0); x <= Mathf.Min(W - 1, x1); x++)
            {
                var p = new Vector2(x, y);
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                float d = Vector2.Distance(p, a + ab * t);
                float k = 1f - Smooth(width * 0.45f, width, d);        // V 形的刻痕：中間最深
                h[y * W + x] = Mathf.Max(h[y * W + x], k);
            }
        }

        // ------------------------------------------------------------------ 材質
        static Material Get(string name, string shader)
        {
            string path = Dir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m) { m = new Material(Shader.Find(shader)); AssetDatabase.CreateAsset(m, path); }
            m.shader = Shader.Find(shader);
            return m;
        }

        static Material Unlit(string name, Texture2D tex, Color color, bool additive, bool fog)
        {
            var m = Get(name, "HDRP/Unlit");
            m.SetTexture("_UnlitColorMap", tex);
            m.SetColor("_UnlitColor", color);
            m.SetFloat("_SurfaceType", 1f);                       // 透明
            m.SetFloat("_BlendMode", additive ? 1f : 0f);         // 1 = Additive
            m.SetFloat("_EnableFogOnTransparent", fog ? 1f : 0f); // 流星在天上，不要被霧吃掉
            m.SetFloat("_DoubleSidedEnable", 1f);
            m.enableInstancing = true;
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material Decal(string name, Texture2D col, Texture2D nrm)
        {
            var m = Get(name, "HDRP/Decal");
            m.SetTexture("_BaseColorMap", col);
            m.SetTexture("_NormalMap", nrm);
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_DecalBlend", 1f);
            m.SetFloat("_AffectAlbedo", 1f);
            m.SetFloat("_AffectNormal", nrm ? 1f : 0f);
            m.SetFloat("_AffectMetal", 0f);
            m.SetFloat("_AffectSmoothness", 0f);
            m.SetFloat("_AffectAO", 0f);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material MeteoriteMaterial()
        {
            var m = Get("M_DST_Meteorite", "HDRP/Lit");
            m.SetColor("_BaseColor", new Color(0.07f, 0.06f, 0.055f));
            m.SetFloat("_Metallic", 0.35f);
            m.SetFloat("_Smoothness", 0.35f);
            m.SetFloat("_UseEmissiveIntensity", 0f);
            m.SetColor("_EmissiveColor", new Color(1f, 0.35f, 0.08f) * 500f);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
