using UnityEngine;

namespace DUST.World
{
    // 隨機世界的「地形公式」：給 (x, z) 算出地面高度與岩石程度。整個世界由種子決定，
    // 任何位置都可以直接算（不用先載入區塊）→ 出生點、物件擺放、背景執行緒建網格都用同一套。
    // 地貌：低頻起伏的盆地 + 順著風向排列的沙丘（緩坡迎風、陡峭落沙坡）+ 風蝕台地（砂岩崖）+ 遺跡用的平地。
    public sealed class DSTWorldGen
    {
        public const float SiteCell = 320f;          // 每個 320 m 格子最多一處遺跡
        public const float SpawnFlatRadius = 30f;

        public readonly int Seed;
        readonly DSTNoise _n;

        public DSTWorldGen(int seed)
        {
            Seed = seed;
            _n = new DSTNoise(seed);
        }

        public struct Site
        {
            public bool exists;
            public Vector2 center;
            public float radius;
            public float height;
            public int cellX, cellZ;
        }

        // ------------------------------------------------------------------ 高度
        public float Height(float x, float z) => Sample(x, z, out _, out _);

        public float Sample(float x, float z, out float rock) => Sample(x, z, out rock, out _);

        // rock：0 = 沙、1 = 台地岩石區；road：0..1，聚落的街道 / 廣場（夯實的土路）
        public float Sample(float x, float z, out float rock, out float road)
        {
            float h = Raw(x, z, out rock);
            road = 0f;

            // 出生點附近整平（車子、箱子放這裡）
            float d = Mathf.Sqrt(x * x + z * z);
            if (d < SpawnFlatRadius + 45f)
            {
                float w = Smooth(SpawnFlatRadius, SpawnFlatRadius + 45f, d);
                h = Mathf.Lerp(SpawnHeight, h, w);
                rock *= w;
            }

            // 遺跡平地（檢查附近 3×3 個格子）
            int cx = Mathf.FloorToInt(x / SiteCell), cz = Mathf.FloorToInt(z / SiteCell);
            for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                var s = SiteAt(cx + i, cz + j);
                if (!s.exists) continue;
                float sd = Vector2.Distance(new Vector2(x, z), s.center);
                if (sd > s.radius + 22f) continue;
                float w = Smooth(s.radius, s.radius + 22f, sd);
                h = Mathf.Lerp(s.height, h, w);
                rock *= w;
            }

            // 聚落：整片整平，畫出街道，綠洲挖出水池
            var p = new Vector2(x, z);
            int sx = Mathf.FloorToInt(x / SettleCell), sz = Mathf.FloorToInt(z / SettleCell);
            for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                var s = SettlementAt(sx + i, sz + j);
                if (s == null) continue;
                float sd = Vector2.Distance(p, s.center);
                if (sd > s.radius + SettleBlend) continue;
                float w = Smooth(s.radius + 4f, s.radius + SettleBlend, sd);
                h = Mathf.Lerp(s.height, h, w);
                rock *= w;
                if (w < 1f)
                {
                    float edge = _n.Noise(x / 3.1f, z / 3.1f) * 0.5f;
                    road = Mathf.Max(road, (1f - Smooth(-0.6f, 0.9f, s.RoadDistance(p) + edge)) * (1f - w));
                }
                if (s.pondRadius > 0f && sd < s.pondRadius)
                {
                    h -= 1.1f * Smooth(s.pondRadius, s.pondRadius - 3f, sd);
                    road = 0f;
                }
            }
            return h;
        }

        public const float SettleCell = 768f;      // 每個 768 m 格子最多一個聚落
        public const float SettleBlend = 34f;      // 整平範圍外圍的緩坡

        public float PondWaterHeight(DSTSettlement s) => s.height - 0.3f;

        float _spawnHeight = float.NaN;
        public float SpawnHeight
        {
            get
            {
                if (float.IsNaN(_spawnHeight)) _spawnHeight = Base(0f, 0f);
                return _spawnHeight;
            }
        }

        float Base(float x, float z) => _n.Fbm(x / 1100f, z / 1100f, 3) * 9f + _n.Fbm(x / 260f + 40f, z / 260f, 3) * 2.5f;

        // 不含整平的原始地形
        float Raw(float x, float z, out float rock)
        {
            float h = Base(x, z);

            // 離出生點越近，沙丘和台地越少（出生點周圍是平坦的沙地）
            float d = Mathf.Sqrt(x * x + z * z);
            float far = Smooth(40f, 160f, d);

            // 風蝕台地：遮罩超過門檻就抬升成平台，邊緣用細雜訊弄得崎嶇
            float m = _n.Fbm(x / 420f + 300f, z / 420f - 200f, 4) + _n.Fbm(x / 38f, z / 38f, 2) * 0.035f;
            m -= (1f - far) * 0.5f;
            float mesa = Smooth(0.2f, 0.26f, m);
            float terrace = Smooth(0.34f, 0.38f, m);
            float mesaTop = 9f + _n.Fbm(x / 180f - 70f, z / 180f, 2) * 4f;
            h += mesa * mesaTop + terrace * 5f;
            rock = Smooth(0.14f, 0.22f, m);

            // 沙丘：沿著風向的週期剖面（迎風坡緩、背風坡陡），用雜訊扭曲成彎曲的沙脊
            float field = Smooth(-0.3f, 0.05f, _n.Fbm(x / 800f + 50f, z / 800f - 80f, 3)) * (1f - Smooth(0.06f, 0.2f, m)) * far;
            if (field > 0.001f)
            {
                float a = 0.6f + _n.Fbm(x / 2000f, z / 2000f, 2) * 0.8f;
                float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                float along = x * ca + z * sa, across = -x * sa + z * ca;
                float u = along / 55f + _n.Fbm(x / 160f, z / 160f, 2) * 1.4f + _n.Noise(across / 90f, 3.3f) * 0.8f;
                float amp = 6.5f * (0.55f + 0.45f * _n.Noise(across / 70f, along / 200f + 11f));
                float small = DuneProfile(along / 13f + _n.Noise(x / 50f, z / 50f) * 0.7f) * 0.6f;
                h += (DuneProfile(u) * amp + small) * field;
            }

            // 細小起伏
            h += _n.Fbm(x / 55f + 9f, z / 55f, 2) * 0.5f;
            return h;
        }

        // ------------------------------------------------------------------ 地區（左下角「你來到了…」）
        public enum RegionKind { Camp, Settlement, Ruins, Mesa, DuneSea, Basin, Plain }
        public const float RegionCell = 1200f;   // 大地形的專有名稱以這個大小分區

        public string RegionAt(float x, float z, out RegionKind kind)
        {
            float d = Mathf.Sqrt(x * x + z * z);
            if (d < SpawnFlatRadius + 40f) { kind = RegionKind.Camp; return "營地"; }

            var s = SettlementContaining(x, z);
            if (s != null) { kind = RegionKind.Settlement; return s.Title; }

            int cx = Mathf.FloorToInt(x / SiteCell), cz = Mathf.FloorToInt(z / SiteCell);
            for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                var site = SiteAt(cx + i, cz + j);
                if (site.exists && Vector2.Distance(new Vector2(x, z), site.center) < site.radius + 6f) { kind = RegionKind.Ruins; return "古代遺跡"; }
            }

            // 跟 Raw 一樣的遮罩，判斷是哪種地形
            float far = Smooth(40f, 160f, d);
            float m = _n.Fbm(x / 420f + 300f, z / 420f - 200f, 4) - (1f - far) * 0.5f;
            float field = Smooth(-0.3f, 0.05f, _n.Fbm(x / 800f + 50f, z / 800f - 80f, 3)) * (1f - Smooth(0.06f, 0.2f, m)) * far;
            string suffix;
            if (m > 0.2f) { kind = RegionKind.Mesa; suffix = "台地"; }
            else if (field > 0.6f) { kind = RegionKind.DuneSea; suffix = "沙海"; }
            else if (Base(x, z) < -2.5f) { kind = RegionKind.Basin; suffix = "盆地"; }
            else { kind = RegionKind.Plain; suffix = "平原"; }

            // 大地形用專有名稱（每 1.2 km 一區，跟創世神的生態域一樣換區就換名字）
            int rx = Mathf.FloorToInt(x / RegionCell), rz = Mathf.FloorToInt(z / RegionCell);
            var rng = new System.Random((int)DSTNoise.Hash(Seed, rx, rz, 700 + (int)kind));
            return $"「{DSTSettlement.MakeName(rng)}」{suffix}";
        }

        // 0..1，波峰在 0.72（迎風坡長、落沙坡短）
        static float DuneProfile(float u)
        {
            float t = u - Mathf.Floor(u);
            if (t < 0.72f) return 0.5f - 0.5f * Mathf.Cos(Mathf.PI * t / 0.72f);
            return 0.5f + 0.5f * Mathf.Cos(Mathf.PI * (t - 0.72f) / 0.28f);
        }

        static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        public Vector3 Normal(float x, float z, float e = 0.5f)
        {
            float hx = Height(x + e, z) - Height(x - e, z);
            float hz = Height(x, z + e) - Height(x, z - e);
            return new Vector3(-hx, 2f * e, -hz).normalized;
        }

        // ------------------------------------------------------------------ 遺跡
        readonly System.Collections.Concurrent.ConcurrentDictionary<long, Site> _sites = new System.Collections.Concurrent.ConcurrentDictionary<long, Site>();

        public Site SiteAt(int cellX, int cellZ)
        {
            long key = ((long)cellX << 32) ^ (uint)cellZ;
            if (_sites.TryGetValue(key, out var cached)) return cached;
            var s = ComputeSite(cellX, cellZ);
            _sites[key] = s;
            return s;
        }

        Site ComputeSite(int cellX, int cellZ)
        {
            var s = new Site { cellX = cellX, cellZ = cellZ };
            if (DSTNoise.Hash01(Seed, cellX, cellZ, 1) > 0.5f) return s;
            float fx = 0.2f + 0.6f * DSTNoise.Hash01(Seed, cellX, cellZ, 2);
            float fz = 0.2f + 0.6f * DSTNoise.Hash01(Seed, cellX, cellZ, 3);
            var c = new Vector2((cellX + fx) * SiteCell, (cellZ + fz) * SiteCell);
            if (c.magnitude < 140f) return s;
            Raw(c.x, c.y, out float rock);
            if (rock > 0.15f) return s;   // 不蓋在台地上
            float radius = 16f + 10f * DSTNoise.Hash01(Seed, cellX, cellZ, 4);
            // 不跟聚落重疊
            int sx = Mathf.FloorToInt(c.x / SettleCell), sz = Mathf.FloorToInt(c.y / SettleCell);
            for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                var st = SettlementAt(sx + i, sz + j);
                if (st != null && Vector2.Distance(c, st.center) < st.radius + SettleBlend + radius + 30f) return s;
            }
            s.exists = true;
            s.center = c;
            s.radius = radius;
            s.height = Raw(c.x, c.y, out _);
            return s;
        }

        // ------------------------------------------------------------------ 聚落
        // 每個 768 m 格子約一半機率有聚落；出生點旁邊一定有一個（方向、種類、格局都由種子決定）
        readonly System.Collections.Concurrent.ConcurrentDictionary<long, DSTSettlement> _settlements = new System.Collections.Concurrent.ConcurrentDictionary<long, DSTSettlement>();

        public DSTSettlement SettlementAt(int cellX, int cellZ)
        {
            long key = ((long)cellX << 32) ^ (uint)cellZ;
            if (_settlements.TryGetValue(key, out var cached)) return cached;
            var s = ComputeSettlement(cellX, cellZ);
            _settlements[key] = s;
            return s;
        }

        DSTSettlement ComputeSettlement(int cellX, int cellZ)
        {
            bool home = cellX == 0 && cellZ == 0;
            Vector2 c;
            if (home)
            {
                float a = DSTNoise.Hash01(Seed, 0, 0, 601) * Mathf.PI * 2f;
                c = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 135f;
            }
            else
            {
                if (DSTNoise.Hash01(Seed, cellX, cellZ, 11) > 0.55f) return null;
                float fx = 0.2f + 0.6f * DSTNoise.Hash01(Seed, cellX, cellZ, 12);
                float fz = 0.2f + 0.6f * DSTNoise.Hash01(Seed, cellX, cellZ, 13);
                c = new Vector2((cellX + fx) * SettleCell, (cellZ + fz) * SettleCell);
                if (c.magnitude < 450f) return null;          // 出生點那個附近不要再擠一個
                Raw(c.x, c.y, out float rock);
                if (rock > 0.1f) return null;                 // 不蓋在台地上
            }
            float h = Raw(c.x, c.y, out _);
            if (home) h = Mathf.Lerp(SpawnHeight, h, 0.5f);   // 跟出生點的高低差不要太大
            return DSTSettlement.Generate(Seed, cellX, cellZ, c, h, home);
        }

        // 中心點落在這個區塊的聚落（由這個區塊負責蓋）
        public DSTSettlement SettlementCenteredIn(Vector2Int chunk)
        {
            float cxw = (chunk.x + 0.5f) * DSTWorldChunk.Size, czw = (chunk.y + 0.5f) * DSTWorldChunk.Size;
            int sx = Mathf.FloorToInt(cxw / SettleCell), sz = Mathf.FloorToInt(czw / SettleCell);
            for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                var s = SettlementAt(sx + i, sz + j);
                if (s != null && Mathf.FloorToInt(s.center.x / DSTWorldChunk.Size) == chunk.x && Mathf.FloorToInt(s.center.y / DSTWorldChunk.Size) == chunk.y) return s;
            }
            return null;
        }

        // 包含這個點的聚落（玩家進入聚落時顯示名字）
        public DSTSettlement SettlementContaining(float x, float z, float margin = 0f)
        {
            int sx = Mathf.FloorToInt(x / SettleCell), sz = Mathf.FloorToInt(z / SettleCell);
            for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                var s = SettlementAt(sx + i, sz + j);
                if (s != null && Vector2.Distance(new Vector2(x, z), s.center) < s.radius + margin) return s;
            }
            return null;
        }

        // ------------------------------------------------------------------ 種子碼
        // 跟創世神一樣：純數字就直接用，其他文字取雜湊；空白 = 隨機
        public static int ParseSeed(string text)
        {
            text = text?.Trim() ?? "";
            if (text.Length == 0) return Random.Range(int.MinValue, int.MaxValue);
            if (int.TryParse(text, out int n)) return n;
            unchecked
            {
                int h = 0;
                foreach (char ch in text) h = 31 * h + ch;   // 跟 Java String.hashCode 一樣
                return h;
            }
        }
    }
}
