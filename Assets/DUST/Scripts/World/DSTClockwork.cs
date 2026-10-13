using System.Collections.Generic;
using UnityEngine;

namespace DUST.World
{
    // 發條沙漠：所有世界事件都是（世界種子, 地點, 累加時數）的函數。不用存、不用同步，連線只要時鐘一樣，大家就同時看到同一件事。
    // 同一個種子的世界，第幾天幾點、在哪裡會發生什麼是固定的：玩家的進度就是「知道」——哪天哪裡有沙暴、隕石掉在哪。
    // 事件都是「區域性」的：走到別的地方，天氣就不一樣。
    //   沙暴：會移動的風暴團（半徑 600~1200 m），順著當天的風橫越地圖；遠遠看得到沙牆，跑得掉也躲得掉
    //   流星雨：整片天空都看得到；每個區域（2.5 km）當晚各有一顆會落地，隕石發光到隔天中午
    //   集體遷徙：每 600 m 一區，某些天某個物種的各群一起走到同一個聚集點（DSTMigration）
    //   遺跡刻文：每處遺跡的石碑只在固定時刻、陽光斜掃碑面時看得到（DSTInscription），講的是附近會發生的事
    //   異象：雜湊拼出來、有專名的怪天氣，只籠罩一個區域（DSTAnomaly）
    public static class DSTClockwork
    {
        const int Dr2Seed = 0x5EED;

        public static int Seed
        {
            get { var w = DSTWorld.Instance; return w && w.IsRandom ? w.Seed : Dr2Seed; }
        }

        public static double Now => DSTTimeOfDay.Instance ? DSTTimeOfDay.Instance.TotalHours : 12.0;
        public static int DayOf(double totalHours) => (int)System.Math.Floor(totalHours / 24.0);
        public static Vector2 Flat(Vector3 p) => new Vector2(p.x, p.z);

        static float H(int day, int salt) => DSTNoise.Hash01(Seed, day, 0, salt);
        /// <summary>（天, 區域格）的雜湊</summary>
        public static float H(int day, int cx, int cz, int salt) => DSTNoise.Hash01(Seed ^ (day * 7919 + 13), cx, cz, salt);

        /// <summary>以原點為中心的格子（出生點在第 (0,0) 格正中間）</summary>
        public static int Cell(float v, float size) => Mathf.RoundToInt(v / size);

        /// <summary>當天的風往哪吹（度）：飛沙和沙暴都順著它走</summary>
        public static float WindYaw(int day) => DSTNoise.Hash01(Seed, day, 0, 7110) * 360f;

        // ------------------------------------------------------------------ 沙暴：會移動的風暴團
        public const float StormRamp = 0.35f;          // 起風 / 平息各要多久（小時）
        public const float StormCell = 3000f;
        public static int ForcedStormDay => 1 + (Seed & 1);   // 第 2 或第 3 天，一定有一場從出生點經過
        const float MaxStormReach = 6500f;              // 風暴中心離它那一格中心最遠多少（半條路徑 + 半格 + 半徑）

        public struct Storm
        {
            public int day, cx, cz;
            public double start;             // 起風的時間（累加時數）
            public float duration;           // 小時
            public Vector2 origin;           // start 那一刻的中心
            public Vector2 velocity;         // m / 遊戲小時
            public float radius;

            public double End => start + duration;
            public Vector2 CenterAt(double t) => origin + velocity * (float)(System.Math.Min(System.Math.Max(t, start), End) - start);
            public float TimeK(double t)
            {
                float h = (float)(t - start);
                float k = Mathf.Clamp01(Mathf.Min(Mathf.InverseLerp(-StormRamp, 0f, h), 1f - Mathf.InverseLerp(duration, duration + StormRamp, h)));
                return k * k * (3f - 2f * k);
            }
            /// <summary>這一點有多深入風暴（邊緣 35% 是漸層）</summary>
            public float SpaceK(Vector2 p, double t)
            {
                float d = (p - CenterAt(t)).magnitude;
                float k = Mathf.Clamp01((radius * 1.1f - d) / (radius * 0.35f));
                return k * k * (3f - 2f * k);
            }
        }

        static readonly Dictionary<(int, int, int, int), (bool on, Storm s)> _storms = new Dictionary<(int, int, int, int), (bool, Storm)>();


        /// <summary>這一格這一天有沒有生出沙暴（白天 9~17 點起風）</summary>
        public static bool StormIn(int day, int cx, int cz, out Storm s)
        {
            var key = (Seed, day, cx, cz);
            if (_storms.TryGetValue(key, out var hit)) { s = hit.s; return hit.on; }
            if (_storms.Count > 512) _storms.Clear();
            bool on = MakeStorm(day, cx, cz, out s);
            _storms[key] = (on, s);
            return on;
        }

        static bool MakeStorm(int day, int cx, int cz, out Storm s)
        {
            s = default;
            if (day <= 0) return false;                               // 開局那天不吹，讓人先看看沙漠
            bool forced = day == ForcedStormDay && cx == 0 && cz == 0;
            if (!forced && (day == DSTAnomaly.ForcedDay && cx == 0 && cz == 0 || H(day, cx, cz, 7100) > 0.25f)) return false;
            float F(int i) => H(day, cx, cz, 7101 + i);
            s.day = day; s.cx = cx; s.cz = cz;
            // 在遠處成形、一路推進好幾公里：一個地方被罩住大約 1.2~4 小時，之前遠遠就看得到它過來
            s.start = day * 24.0 + 6f + F(0) * 6f;
            s.duration = 4f + F(1) * 3f;
            s.radius = 600f + F(2) * 600f;
            float speed = 600f + F(3) * 400f;
            float yaw = (WindYaw(day) + (F(4) - 0.5f) * 40f) * Mathf.Deg2Rad;
            s.velocity = new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw)) * speed;
            // 走到一半的時候在哪：格子裡隨便一點；一定要有的那一場正好經過出生點
            var mid = forced ? new Vector2(F(5) - 0.5f, F(6) - 0.5f) * 300f
                             : new Vector2(cx + F(5) - 0.5f, cz + F(6) - 0.5f) * StormCell;
            s.origin = mid - s.velocity * (s.duration * 0.5f);
            return true;
        }

        /// <summary>附近（含正在接近的）沙暴；range 是從 p 量的距離</summary>
        public static void StormsNear(double t, Vector2 p, float range, List<Storm> into, bool activeOnly = true)
        {
            into.Clear();
            int day = DayOf(t);
            int r = Mathf.CeilToInt((range + MaxStormReach) / StormCell);
            int px = Cell(p.x, StormCell), pz = Cell(p.y, StormCell);
            foreach (var s in Candidates(day, px, pz, r))
            {
                if (!activeOnly) { into.Add(s); continue; }           // 要算「之前經過」的：位置由呼叫的人判斷
                if (t < s.start - StormRamp || t > s.End + StormRamp) continue;
                if ((s.CenterAt(t) - p).magnitude - s.radius > range) continue;
                into.Add(s);
            }
        }

        // 動物每幀都會問：同一天、同一格附近有哪些沙暴，算一次記起來
        static readonly Dictionary<(int, int, int, int, int), List<Storm>> _cand = new Dictionary<(int, int, int, int, int), List<Storm>>();

        static List<Storm> Candidates(int day, int px, int pz, int r)
        {
            var key = (Seed, day, px, pz, r);
            if (_cand.TryGetValue(key, out var list)) return list;
            if (_cand.Count > 64) _cand.Clear();
            list = new List<Storm>();
            for (int d = day; d >= day - 1; d--)
            for (int x = px - r; x <= px + r; x++)
            for (int z = pz - r; z <= pz + r; z++)
                if (StormIn(d, x, z, out var s)) list.Add(s);
            _cand[key] = list;
            return list;
        }

        static readonly List<Storm> _tmp = new List<Storm>();

        /// <summary>這一點的沙暴強度 0~1</summary>
        public static float StormAt(double t, Vector2 p) => StormAt(t, p, out _);

        public static float StormAt(double t, Vector2 p, out Storm strongest)
        {
            StormsNear(t, p, 0f, _tmp);
            float k = 0f; strongest = default;
            foreach (var s in _tmp)
            {
                float v = s.TimeK(t) * s.SpaceK(p, t);
                if (v > k) { k = v; strongest = s; }
            }
            return k;
        }

        /// <summary>沙暴的中心經過 p 附近（半徑 0.8 內）的時間區間</summary>
        public static bool Pass(in Storm s, Vector2 p, out double tIn, out double tOut)
        {
            tIn = tOut = 0;
            // |origin - p + v τ|² = R²，τ = 起風後幾小時
            var q = s.origin - p;
            float R = s.radius * 0.8f;
            float a = Vector2.Dot(s.velocity, s.velocity), b = 2f * Vector2.Dot(q, s.velocity), c = Vector2.Dot(q, q) - R * R;
            float disc = b * b - 4f * a * c;
            if (a < 1e-4f || disc < 0f) return false;
            float sq = Mathf.Sqrt(disc);
            float t0 = Mathf.Max(0f, (-b - sq) / (2f * a)), t1 = Mathf.Min(s.duration, (-b + sq) / (2f * a));
            if (t1 <= t0) return false;
            tIn = s.start + t0; tOut = s.start + t1;
            return true;
        }

        /// <summary>最近一次沙暴吹過這裡（過了一半）之後結束的時間：之前的足跡都被吹平了；沒有就回傳很久以前</summary>
        public static double LastStormEnd(double t, Vector2 p)
        {
            StormsNear(t, p, 0f, _tmp, activeOnly: false);
            double best = double.MinValue;
            foreach (var s in _tmp)
                if (Pass(s, p, out double a, out double b) && (a + b) * 0.5 <= t)
                    best = System.Math.Max(best, System.Math.Min(t, b));
            return best;
        }

        /// <summary>下一場會經過 p 的沙暴（從 from 起算 days 天內）</summary>
        public static bool NextStormAt(Vector2 p, double from, int days, out Storm storm, out double arrive)
        {
            storm = default; arrive = double.MaxValue;
            int day0 = DayOf(from), px = Cell(p.x, StormCell), pz = Cell(p.y, StormCell);
            for (int d = day0; d < day0 + days; d++)
            for (int x = px - 3; x <= px + 3; x++)
            for (int z = pz - 3; z <= pz + 3; z++)
            {
                if (!StormIn(d, x, z, out var s) || !Pass(s, p, out double a, out _) || a < from || a >= arrive) continue;
                arrive = a; storm = s;
            }
            return arrive < double.MaxValue;
        }

        // ------------------------------------------------------------------ 流星雨（整片天空）
        public const float ShowerStart = 21f, ShowerLength = 6f;   // 21:00 到隔天 03:00
        public const float SlotHours = 1f / 60f;                   // 每遊戲分鐘最多一顆

        public static bool ShowerNight(int day) => day == 0 || H(day, 7200) < 0.25f;   // 第一個晚上一定有，之後隨機

        /// <summary>流星雨強度 0~1（中間最密）</summary>
        public static float Shower(double t)
        {
            int day = DayOf(t);
            for (int d = day; d >= day - 1; d--)
            {
                if (!ShowerNight(d)) continue;
                float h = (float)(t - (d * 24.0 + ShowerStart));
                if (h < 0f || h > ShowerLength) continue;
                return Mathf.Sin(h / ShowerLength * Mathf.PI);
            }
            return 0f;
        }

        public struct Meteor
        {
            public long slot;
            public double start;          // 出現的時間（累加時數）
            public float seconds;         // 劃過天空要幾秒（真實時間）
            public float azimuth, elevation, heading, length, brightness;
        }

        /// <summary>這個時間格有沒有流星（同一格每台電腦算出一樣的）</summary>
        public static bool MeteorIn(long slot, out Meteor m)
        {
            m = default;
            double t = slot * (double)SlotHours;
            float k = Shower(t);
            if (k <= 0f) return false;
            int s = (int)slot, salt = (int)(slot >> 31);
            float R(int i) => DSTNoise.Hash01(Seed, s, salt, 7300 + i);
            if (R(0) > k * 0.55f) return false;
            m.slot = slot;
            m.start = t + R(1) * SlotHours;
            m.seconds = 0.5f + R(2) * 0.9f;
            m.azimuth = R(3) * 360f;
            m.elevation = 25f + R(4) * 50f;
            m.heading = R(5) * 360f;
            m.length = 14f + R(6) * 30f;         // 劃過幾度
            m.brightness = 0.4f + R(7) * R(7) * 1.6f;
            return true;
        }

        // ------------------------------------------------------------------ 隕石：每個區域各自一顆
        public const float MeteorCell = 2500f;

        /// <summary>流星雨那晚，這個區域落地的隕石（大部分區域都有）：落地時間、位置，亮到隔天中午</summary>
        public static bool Meteorite(int day, int cx, int cz, out double landAt, out Vector2 where)
        {
            float F(int i) => H(day, cx, cz, 7210 + i);
            landAt = day * 24.0 + ShowerStart + 1f + F(0) * (ShowerLength - 2f);
            if (cx == 0 && cz == 0)
            {
                // 出生點這一區：離出生點 250~700 m，走得到
                float a = F(1) * Mathf.PI * 2f, r = 250f + F(2) * 450f;
                where = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            }
            else where = new Vector2(cx + (F(1) - 0.5f) * 0.9f, cz + (F(2) - 0.5f) * 0.9f) * MeteorCell;
            return ShowerNight(day) && (cx == 0 && cz == 0 || F(3) < 0.7f);
        }

        /// <summary>p 附近（這一區和隔壁幾區）的隕石</summary>
        public static void MeteoritesNear(int day, Vector2 p, List<(double land, Vector2 where)> into)
        {
            into.Clear();
            int px = Cell(p.x, MeteorCell), pz = Cell(p.y, MeteorCell);
            for (int x = px - 1; x <= px + 1; x++)
            for (int z = pz - 1; z <= pz + 1; z++)
                if (Meteorite(day, x, z, out double land, out var w)) into.Add((land, w));
        }

        static readonly List<(double, Vector2)> _mtmp = new List<(double, Vector2)>();

        /// <summary>離 p 最近、還在發熱的隕石：0 = 沒有，1 = 剛落地</summary>
        public static float MeteoriteHeat(double t, Vector2 p, out Vector2 where, out double landAt)
        {
            int day = DayOf(t);
            float best = 0f, bestDist = float.MaxValue; where = default; landAt = 0;
            for (int d = day; d >= day - 1; d--)
            {
                MeteoritesNear(d, p, _mtmp);
                foreach (var (land, w) in _mtmp)
                {
                    double cool = (d + 1) * 24.0 + 12.0;           // 隔天中午冷掉
                    if (t < land || t > cool) continue;
                    float dist = (w - p).magnitude;
                    if (dist > bestDist) continue;
                    bestDist = dist; where = w; landAt = land;
                    best = Mathf.Lerp(1f, 0.08f, Mathf.Pow((float)(t - land) / (float)(cool - land), 0.5f));
                }
            }
            return best;
        }

        // ------------------------------------------------------------------ 集體遷徙
        /// <summary>這天這個物種會不會集體遷徙（每個物種大約四天一次，日子各不相同）</summary>
        public static bool MassDay(int speciesKey, int day) => DSTNoise.Hash01(Seed, speciesKey, day, 7400) < 0.25f;

        /// <summary>這天這個物種在這個地區格的聚集點</summary>
        public static Vector2 Gathering(int speciesKey, int day, int cellX, int cellZ, Vector2 near, float spread)
        {
            float a = DSTNoise.Hash01(speciesKey, day, cellX * 7 + cellZ, 7401) * Mathf.PI * 2f;
            float r = DSTNoise.Hash01(speciesKey, day, cellX * 7 + cellZ, 7402) * spread;
            return near + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
        }

        // ------------------------------------------------------------------ 方位文字（刻文、/events、快報共用）
        public static string Compass(Vector2 to)
        {
            string[] n = { "北", "東北", "東", "東南", "南", "西南", "西", "西北" };
            float deg = Mathf.Atan2(to.x, to.y) * Mathf.Rad2Deg;
            return n[Mathf.RoundToInt(Mathf.Repeat(deg, 360f) / 45f) % 8];
        }

        public static string Distance(float m) => m >= 1000f ? $"{m / 1000f:0.0} 公里" : $"{Mathf.RoundToInt(m / 10f) * 10} 公尺";
    }
}
