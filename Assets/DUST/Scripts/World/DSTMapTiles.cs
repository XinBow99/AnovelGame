using System.Collections.Generic;
using UnityEngine;

namespace DUST.World
{
    // 地圖圖塊：隨機世界直接用地形公式算出顏色（不用攝影機、可以畫到還沒去過的地方），背景執行緒產生。
    // 兩種比例：0 = 每像素 2 m（一塊 512 m），1 = 每像素 8 m（一塊 2 km，縮小看全貌用）。
    public static class DSTMapTiles
    {
        public const int Size = 256;
        public static readonly float[] MetersPerPixel = { 2f, 8f };

        public static float TileMeters(int level) => Size * MetersPerPixel[level];

        static readonly Color32 Sand = new Color32(214, 186, 142, 255);
        static readonly Color32 SandLow = new Color32(196, 164, 120, 255);
        static readonly Color32 Rock = new Color32(160, 118, 86, 255);
        static readonly Color32 Road = new Color32(176, 142, 100, 255);
        static readonly Color32 Roof = new Color32(226, 204, 170, 255);
        static readonly Color32 Edge = new Color32(112, 86, 62, 255);
        static readonly Color32 Water = new Color32(64, 118, 134, 255);

        public static Color32[] Build(DSTWorldGen gen, int level, int tx, int tz)
        {
            float mpp = MetersPerPixel[level];
            float ox = tx * Size * mpp, oz = tz * Size * mpp;
            int n = Size + 1;
            var h = new float[n * n];
            var rock = new float[Size * Size];
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float x = ox + (i + 0.5f) * mpp, z = oz + (j + 0.5f) * mpp;
                h[j * n + i] = gen.Sample(x, z, out float r);
                if (i < Size && j < Size) rock[j * Size + i] = r;
            }

            // 這塊圖裡的聚落（整塊一起找，不要每個像素查）
            var settlements = new List<DSTSettlement>();
            float span = Size * mpp;
            int s0x = Mathf.FloorToInt((ox - 200f) / DSTWorldGen.SettleCell), s1x = Mathf.FloorToInt((ox + span + 200f) / DSTWorldGen.SettleCell);
            int s0z = Mathf.FloorToInt((oz - 200f) / DSTWorldGen.SettleCell), s1z = Mathf.FloorToInt((oz + span + 200f) / DSTWorldGen.SettleCell);
            for (int a = s0x; a <= s1x; a++)
            for (int b = s0z; b <= s1z; b++)
            {
                var s = gen.SettlementAt(a, b);
                if (s != null) settlements.Add(s);
            }

            // 坡度陰影：光從西北方照過來；縮小時地形起伏看起來比較平，加強一點
            float exaggerate = level == 0 ? 2.5f : 4f;
            var light = new Vector3(-0.55f, 0.65f, 0.52f).normalized;
            var px = new Color32[Size * Size];
            for (int j = 0; j < Size; j++)
            for (int i = 0; i < Size; i++)
            {
                float hc = h[j * n + i];
                float dx = (h[j * n + i + 1] - hc) / mpp * exaggerate;
                float dz = (h[(j + 1) * n + i] - hc) / mpp * exaggerate;
                var nrm = new Vector3(-dx, 1f, -dz).normalized;
                float shade = 0.62f + 0.55f * Mathf.Max(0f, Vector3.Dot(nrm, light));

                float r = rock[j * Size + i];
                Color32 c = Color32.Lerp(Color32.Lerp(SandLow, Sand, Mathf.InverseLerp(-6f, 6f, hc)), Rock, Mathf.Clamp01((r - 0.3f) * 2.5f));

                float x = ox + (i + 0.5f) * mpp, z = oz + (j + 0.5f) * mpp;
                var p = new Vector2(x, z);
                foreach (var s in settlements)
                {
                    float d = Vector2.Distance(p, s.center);
                    if (d > s.radius + 12f) continue;
                    if (s.pondRadius > 0f && d < s.pondRadius) { c = Water; shade = 1f; break; }
                    if (s.RoadDistance(p) < 0f) c = Road;
                    if (InBuilding(s, p, out bool edge)) { c = edge && level == 0 ? Edge : Roof; shade = 1f; }
                    foreach (var (wa, wb) in s.walls)
                        if (SegmentDistance(p, wa, wb) < Mathf.Max(0.8f, mpp * 0.6f)) { c = Edge; shade = 1f; }
                }

                px[j * Size + i] = new Color32((byte)Mathf.Clamp(c.r * shade, 0, 255), (byte)Mathf.Clamp(c.g * shade, 0, 255), (byte)Mathf.Clamp(c.b * shade, 0, 255), 255);
            }
            return px;
        }

        static bool InBuilding(DSTSettlement s, Vector2 p, out bool edge)
        {
            edge = false;
            foreach (var b in s.buildings)
            {
                Vector2 rel = p - b.origin;
                float lx = Vector2.Dot(rel, b.ux), lz = Vector2.Dot(rel, b.Uz);
                float W = b.w * 4f, D = b.d * 4f;
                if (lx < -0.5f || lx > W + 0.5f || lz < -0.5f || lz > D + 0.5f) continue;
                edge = lx < 0.6f || lx > W - 0.6f || lz < 0.6f || lz > D - 0.6f || b.decay > 0f;
                return true;
            }
            return false;
        }

        static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude));
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
