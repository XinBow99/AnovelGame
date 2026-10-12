using System.Collections.Generic;
using UnityEngine;

namespace DUST.World
{
    // 沙漠聚落的「藍圖」：只由種子決定（純資料、純數學，可以在背景執行緒算）。
    // 地形用它整平地面、畫出街道；擺設用它蓋房子。跟創世神的村莊一樣：
    // 中央廣場（水井或綠洲水池）→ 往外長出幾條主街和支巷 → 沿街兩側蓋面向街道的房子 → 廣場擺攤、種椰棗樹。
    public sealed class DSTSettlement
    {
        public enum Kind { Oasis, Village, Walled, Trading, Abandoned }
        public enum FeatureKind { Well, Stall, Palm, Pot, Barrel, Crate, Drift, Wreck, Rubble }

        public struct Street
        {
            public Vector2 a, b;
            public float halfWidth;
        }

        // 房子：origin 是左前角，ux 是沿街方向（本地 +X），本地 +Z 往屋內（離開街道）。門在前牆（本地 z = 0）。
        public struct Building
        {
            public Vector2 origin;
            public Vector2 ux;
            public int w, d;               // 幾格（每格 4 m）
            public int doorIndex;
            public float decay;            // 0 = 完整、越大越破
            public bool roof;
            public int seed;

            public Vector2 Uz => new Vector2(-ux.y, ux.x);
            public float Yaw => Mathf.Atan2(-ux.y, ux.x) * Mathf.Rad2Deg;
            public Vector2 Corner(float lx, float lz) => origin + ux * lx + Uz * lz;
        }

        public struct Feature
        {
            public FeatureKind kind;
            public Vector2 pos;
            public float yaw;
            public float scale;
        }

        public int cellX, cellZ;
        public Kind kind;
        public string name;
        public Vector2 center;
        public float radius;
        public float height;
        public float plazaRadius;
        public float pondRadius;           // 0 = 沒有水池
        public readonly List<Street> streets = new List<Street>();
        public readonly List<Building> buildings = new List<Building>();
        public readonly List<Feature> features = new List<Feature>();
        public readonly List<(Vector2 a, Vector2 b)> walls = new List<(Vector2, Vector2)>();

        public string Title
        {
            get
            {
                switch (kind)
                {
                    case Kind.Oasis: return $"綠洲「{name}」";
                    case Kind.Walled: return $"「{name}」城寨";
                    case Kind.Trading: return $"「{name}」商隊驛站";
                    case Kind.Abandoned: return $"廢棄的「{name}」";
                    default: return $"「{name}」聚落";
                }
            }
        }

        // ------------------------------------------------------------------ 地形查詢
        // 到最近的街道 / 廣場邊緣的距離（< 0 = 在路上）
        public float RoadDistance(Vector2 p)
        {
            float best = Vector2.Distance(p, center) - plazaRadius;
            foreach (var s in streets)
            {
                Vector2 ab = s.b - s.a;
                float t = Mathf.Clamp01(Vector2.Dot(p - s.a, ab) / Mathf.Max(0.001f, ab.sqrMagnitude));
                float d = Vector2.Distance(p, s.a + ab * t) - s.halfWidth;
                if (d < best) best = d;
            }
            return best;
        }

        // ------------------------------------------------------------------ 生成
        static readonly string[] Syl1 = { "阿", "薩", "卡", "納", "哈", "塔", "法", "茲", "瑪", "拉", "賈", "奧", "伊", "蘇", "巴", "梅", "達", "凱" };
        static readonly string[] Syl2 = { "比", "德", "林", "茲", "姆", "汗", "拉", "瑞", "希", "爾", "洛", "斯", "米", "丹", "庫", "沙", "迪", "努" };
        static readonly string[] Syl3 = { "爾", "特", "亞", "恩", "斯", "", "", "", "拉", "德" };

        public static string MakeName(System.Random rng) => Syl1[rng.Next(Syl1.Length)] + Syl2[rng.Next(Syl2.Length)] + Syl3[rng.Next(Syl3.Length)];

        public static DSTSettlement Generate(int seed, int cellX, int cellZ, Vector2 center, float height, bool home)
        {
            var rng = new System.Random((int)DSTNoise.Hash(seed, cellX, cellZ, 501));
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            var s = new DSTSettlement { cellX = cellX, cellZ = cellZ, center = center, height = height };

            float k = R(0f, 1f);
            // 出生點旁邊那個不會是廢墟（第一眼要看到有人住的聚落）
            if (home) s.kind = k < 0.35f ? Kind.Oasis : k < 0.75f ? Kind.Village : Kind.Walled;
            else s.kind = k < 0.25f ? Kind.Oasis : k < 0.52f ? Kind.Village : k < 0.7f ? Kind.Walled : k < 0.83f ? Kind.Trading : Kind.Abandoned;
            s.name = MakeName(rng);

            switch (s.kind)
            {
                case Kind.Trading: s.radius = R(22f, 28f); s.plazaRadius = R(7f, 8f); break;
                case Kind.Oasis: s.radius = R(42f, 55f); s.plazaRadius = R(13f, 15f); s.pondRadius = s.plazaRadius - R(4.5f, 6f); break;
                case Kind.Abandoned: s.radius = R(30f, 46f); s.plazaRadius = R(7f, 9f); break;
                default: s.radius = R(42f, 60f); s.plazaRadius = R(7f, 9f); break;
            }

            // 主街：從廣場往外放射
            int mains = s.kind == Kind.Trading ? rng.Next(2, 4) : rng.Next(3, 6);
            float baseAngle = R(0f, Mathf.PI * 2f);
            var mainDirs = new List<Vector2>();
            for (int i = 0; i < mains; i++)
            {
                float a = baseAngle + i * Mathf.PI * 2f / mains + R(-0.25f, 0.25f);
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                mainDirs.Add(dir);
                float hw = s.kind == Kind.Trading ? 2f : 2.3f;
                // 城寨的街道穿過城牆一點，當作城門
                float len = s.radius - 1f + (s.kind == Kind.Walled ? 6f : 0f);
                s.streets.Add(new Street { a = center + dir * (s.plazaRadius - 1f), b = center + dir * len, halfWidth = hw });
            }
            // 支巷：從主街中段往旁邊岔出去
            if (s.kind != Kind.Trading)
            {
                int count = s.streets.Count;
                for (int i = 0; i < count; i++)
                {
                    if (R(0f, 1f) > 0.6f) continue;
                    var m = s.streets[i];
                    var dir = (m.b - m.a).normalized;
                    var side = new Vector2(-dir.y, dir.x) * (R(0f, 1f) < 0.5f ? -1f : 1f);
                    var from = Vector2.Lerp(m.a, m.b, R(0.45f, 0.65f));
                    var to = from + (side * 0.85f + dir * 0.15f).normalized * R(12f, 22f);
                    if (Vector2.Distance(to, center) > s.radius - 3f) to = center + (to - center).normalized * (s.radius - 3f);
                    s.streets.Add(new Street { a = from, b = to, halfWidth = 1.8f });
                }
            }

            // 房子：沿每條街兩側，一間間往外蓋
            int maxHouses = s.kind == Kind.Trading ? 6 : s.kind == Kind.Abandoned ? 12 : 22;
            foreach (var st in s.streets)
            {
                Vector2 u = (st.b - st.a).normalized;
                float len = Vector2.Distance(st.a, st.b);
                for (int side = -1; side <= 1; side += 2)
                {
                    float t = R(1f, 4f);
                    while (t < len - 3f && s.buildings.Count < maxHouses)
                    {
                        int w = Pick(rng, 1, 2, 2, 3);
                        int d = s.kind == Kind.Trading ? Pick(rng, 1, 2) : Pick(rng, 2, 2, 3);
                        Vector2 ux = side > 0 ? u : -u;
                        Vector2 uz = new Vector2(-ux.y, ux.x);           // 離開街道的方向
                        Vector2 front = st.a + u * (t + w * 2f) + uz * (st.halfWidth + R(1f, 2f));
                        var b = new Building
                        {
                            origin = front - ux * (w * 2f),
                            ux = ux, w = w, d = d,
                            doorIndex = rng.Next(w),
                            seed = rng.Next(),
                        };
                        if (s.kind == Kind.Abandoned) { b.decay = R(0.45f, 0.75f); b.roof = false; }
                        else { b.decay = R(0f, 1f) < 0.15f ? R(0.3f, 0.5f) : 0f; b.roof = b.decay == 0f; }
                        if (s.Fits(b)) { s.buildings.Add(b); t += w * 4f + R(1.5f, 4f); }
                        else t += R(2f, 4f);
                    }
                }
            }

            // 廣場中央：水井或綠洲水池
            if (s.pondRadius > 0f)
            {
                int palms = rng.Next(7, 12);
                for (int i = 0; i < palms; i++)
                {
                    float a = i * Mathf.PI * 2f / palms + R(-0.2f, 0.2f);
                    float r = R(s.pondRadius + 1.2f, s.plazaRadius + 1.5f);
                    s.features.Add(new Feature { kind = FeatureKind.Palm, pos = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r, yaw = R(0f, 360f), scale = R(0.8f, 1.25f) });
                }
            }
            else if (s.kind != Kind.Abandoned || R(0f, 1f) < 0.5f)
                s.features.Add(new Feature { kind = FeatureKind.Well, pos = center, yaw = R(0f, 360f), scale = 1f });

            // 市集攤位：圍著廣場、面向中央，避開街口
            int stalls = s.kind == Kind.Trading ? rng.Next(4, 7) : s.kind == Kind.Abandoned ? 0 : rng.Next(1, 4);
            for (int i = 0, tries = 0; i < stalls && tries < 30; tries++)
            {
                float a = R(0f, Mathf.PI * 2f);
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                bool blocked = false;
                foreach (var m in mainDirs) if (Vector2.Dot(m, dir) > 0.93f) blocked = true;
                if (blocked) continue;
                var p = center + dir * (s.plazaRadius - (s.pondRadius > 0f ? 1.5f : 2.5f));
                if (s.TooClose(p, 3.2f)) continue;
                float yaw = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg + 180f;   // 攤位正面朝廣場中央
                s.features.Add(new Feature { kind = FeatureKind.Stall, pos = p, yaw = yaw, scale = 1f });
                i++;
            }

            // 零星的椰棗樹
            int extraPalms = s.kind == Kind.Oasis ? rng.Next(2, 5) : s.kind == Kind.Village ? rng.Next(1, 4) : rng.Next(0, 2);
            for (int i = 0, tries = 0; i < extraPalms && tries < 40; tries++)
            {
                var p = center + Random2(rng) * R(s.plazaRadius + 4f, s.radius);
                if (s.RoadDistance(p) < 1.5f || s.InsideBuilding(p, 1.5f) || s.TooClose(p, 3f)) continue;
                s.features.Add(new Feature { kind = FeatureKind.Palm, pos = p, yaw = R(0f, 360f), scale = R(0.75f, 1.15f) });
                i++;
            }

            // 屋前雜物：陶罐、木桶、木箱、積沙
            foreach (var b in s.buildings)
            {
                int n = rng.Next(0, 3);
                for (int i = 0; i < n; i++)
                {
                    var p = b.Corner(R(0.6f, b.w * 4f - 0.6f), -R(0.5f, 0.9f));
                    float x = R(0f, 1f);
                    FeatureKind fk = s.kind == Kind.Abandoned
                        ? (x < 0.5f ? FeatureKind.Drift : x < 0.8f ? FeatureKind.Rubble : FeatureKind.Pot)
                        : (x < 0.45f ? FeatureKind.Pot : x < 0.7f ? FeatureKind.Barrel : x < 0.85f ? FeatureKind.Crate : FeatureKind.Drift);
                    s.features.Add(new Feature { kind = fk, pos = p, yaw = R(0f, 360f), scale = R(0.85f, 1.15f) });
                }
            }
            if (s.kind == Kind.Abandoned)
            {
                for (int i = 0; i < rng.Next(4, 9); i++)
                {
                    var p = center + Random2(rng) * R(0f, s.radius);
                    if (s.InsideBuilding(p, 1f)) continue;
                    s.features.Add(new Feature { kind = FeatureKind.Drift, pos = p, yaw = R(0f, 360f), scale = R(1f, 2f) });
                }
                if (R(0f, 1f) < 0.7f)
                {
                    var p = center + Random2(rng) * R(s.plazaRadius + 3f, s.radius - 4f);
                    if (!s.InsideBuilding(p, 3f)) s.features.Add(new Feature { kind = FeatureKind.Wreck, pos = p, yaw = R(0f, 360f), scale = 1f });
                }
            }

            // 城寨：外圍一圈城牆，主街經過的地方留城門
            if (s.kind == Kind.Walled)
            {
                float wr = s.radius + 2f;
                int segs = Mathf.CeilToInt(2f * Mathf.PI * wr / 4f);
                float step = Mathf.PI * 2f / segs;
                float chord = 2f * wr * Mathf.Sin(step * 0.5f);
                wr *= 4f / chord;   // 調整半徑讓每段剛好 4 m
                for (int i = 0; i < segs; i++)
                {
                    float a0 = i * step, a1 = (i + 1) * step;
                    var p0 = center + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * wr;
                    var p1 = center + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * wr;
                    var mid = ((p0 + p1) * 0.5f - center).normalized;
                    bool gate = false;
                    foreach (var m in mainDirs) if (Vector2.Dot(m, mid) > Mathf.Cos(3.2f / wr + step)) gate = true;
                    if (!gate) s.walls.Add((p0, p1));
                }
            }
            return s;
        }

        static int Pick(System.Random rng, params int[] options) => options[rng.Next(options.Length)];

        static Vector2 Random2(System.Random rng)
        {
            float a = (float)rng.NextDouble() * Mathf.PI * 2f;
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }

        // 房子放得下嗎：在聚落範圍內、不壓到街道 / 廣場 / 其他房子
        bool Fits(Building b)
        {
            float W = b.w * 4f, D = b.d * 4f;
            var corners = new[] { b.Corner(-0.5f, -0.5f), b.Corner(W + 0.5f, -0.5f), b.Corner(W + 0.5f, D + 0.5f), b.Corner(-0.5f, D + 0.5f) };
            foreach (var c in corners)
                if (Vector2.Distance(c, center) > radius) return false;
            // 每邊取幾個點檢查是否壓到路（含廣場）
            for (int i = 0; i < 4; i++)
                for (int j = 0; j <= 4; j++)
                    if (RoadDistance(Vector2.Lerp(corners[i], corners[(i + 1) % 4], j / 4f)) < 0.3f) return false;
            if (RoadDistance(b.Corner(W * 0.5f, D * 0.5f)) < 0.3f) return false;
            foreach (var o in buildings)
            {
                float OW = o.w * 4f, OD = o.d * 4f;
                var oc = new[] { o.Corner(-0.6f, -0.6f), o.Corner(OW + 0.6f, -0.6f), o.Corner(OW + 0.6f, OD + 0.6f), o.Corner(-0.6f, OD + 0.6f) };
                if (Overlap(corners, oc)) return false;
            }
            return true;
        }

        public bool InsideBuilding(Vector2 p, float margin)
        {
            foreach (var b in buildings)
            {
                Vector2 rel = p - b.origin;
                float lx = Vector2.Dot(rel, b.ux), lz = Vector2.Dot(rel, b.Uz);
                if (lx > -margin && lx < b.w * 4f + margin && lz > -margin && lz < b.d * 4f + margin) return true;
            }
            return false;
        }

        bool TooClose(Vector2 p, float r)
        {
            foreach (var f in features) if (Vector2.Distance(f.pos, p) < r) return true;
            return false;
        }

        // 兩個凸四邊形是否重疊（分離軸）
        static bool Overlap(Vector2[] a, Vector2[] b)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var poly = pass == 0 ? a : b;
                for (int i = 0; i < 4; i++)
                {
                    Vector2 e = poly[(i + 1) % 4] - poly[i];
                    Vector2 axis = new Vector2(-e.y, e.x);
                    float amin = float.MaxValue, amax = float.MinValue, bmin = float.MaxValue, bmax = float.MinValue;
                    foreach (var p in a) { float d = Vector2.Dot(p, axis); amin = Mathf.Min(amin, d); amax = Mathf.Max(amax, d); }
                    foreach (var p in b) { float d = Vector2.Dot(p, axis); bmin = Mathf.Min(bmin, d); bmax = Mathf.Max(bmax, d); }
                    if (amax < bmin || bmax < amin) return false;
                }
            }
            return true;
        }
    }
}
