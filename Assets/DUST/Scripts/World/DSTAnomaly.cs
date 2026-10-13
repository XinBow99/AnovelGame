using System.Collections.Generic;
using UnityEngine;

namespace DUST.World
{
    // 異象（發條沙漠，見 DSTClockwork）：像新聞裡「從沒聽過的極端天氣」——每一場都是從（種子, 天）雜湊出來的組合：
    // 一個主現象（乾雷、怪色的雨、降灰、鳴沙、幻日、晝暝、熱爆、萬籟寂）＋幾個附帶效果＋一層顏色，
    // 名稱也是雜湊拼出來的，像颱風一樣有個專名：「薩赫倫」赭色乾雷。
    // 每一場只籠罩一個區域（中心 + 半徑 700~1400 m，每 4 km 一格各自決定）：遠遠看得到那團異色的天、裡面的閃電，走得過去也躲得開。
    // 一樣不用同步：同一個種子的世界，第幾天幾點、在哪裡會出現哪一場、叫什麼名字都是固定的。
    public static class DSTAnomaly
    {
        public enum Core { DryLightning, StrangeRain, Ashfall, SingingSand, Parhelion, DayDark, HeatBurst, Hush }

        public const float CellSize = 4000f;
        public const float Edge = 400f;              // 區域邊緣的漸層寬度

        public struct Event
        {
            public int day, cx, cz;
            public Vector2 center;
            public float radius;
            public float start, duration;           // 鐘點（可跨過午夜）、小時
            public Core core;
            public string name, title;               // 「薩赫倫」、赭色乾雷
            public Color haze;                       // 霧／天空的顏色
            public float hazeAmount, hazeDistance;   // 0~1、霧最濃時的 mean free path
            public float sun;                        // 太陽亮度倍率
            public float lightning;                  // 0~1 每個時間格打雷的機率
            public int fall;                         // 0 無、1 雨（落地前就蒸發）、2 灰、3 閃光的細晶
            public Color fallColor;
            public float hum, humPitch;              // 鳴沙：音量 0~1、音高倍率
            public bool halo;                        // 幻日：太陽周圍 22° 的光環與兩個假太陽
            public bool shelter;                     // 動物躲回窩

            public string FullName => $"「{name}」{title}";
            public double StartAt => day * 24.0 + start;
            public double EndAt => StartAt + duration;

            public float TimeK(double t)
            {
                float h = (float)(t - StartAt);
                float k = Mathf.Clamp01(Mathf.Min(Mathf.InverseLerp(-Ramp, 0f, h), 1f - Mathf.InverseLerp(duration, duration + Ramp, h)));
                return k * k * (3f - 2f * k);
            }

            public float SpaceK(Vector2 p)
            {
                float k = Mathf.Clamp01((radius + Edge - (p - center).magnitude) / Edge);
                return k * k * (3f - 2f * k);
            }
        }

        public const float Ramp = 0.4f;

        static int Seed => DSTClockwork.Seed;

        /// <summary>第 4 或第 5 天，出生點附近一定有一場（讓玩家早點遇到）</summary>
        public static int ForcedDay => 3 + ((Seed >> 1) & 1);

        static readonly (string word, Color c)[] Hues =
        {
            ("赭色", new Color(0.80f, 0.46f, 0.26f)), ("鏽色", new Color(0.58f, 0.30f, 0.18f)), ("血色", new Color(0.72f, 0.14f, 0.10f)),
            ("靛色", new Color(0.26f, 0.32f, 0.62f)), ("蒼白", new Color(0.86f, 0.86f, 0.80f)), ("翠色", new Color(0.36f, 0.62f, 0.46f)),
            ("紫色", new Color(0.52f, 0.30f, 0.62f)), ("灰色", new Color(0.52f, 0.52f, 0.52f)), ("金色", new Color(0.92f, 0.76f, 0.36f)),
            ("墨色", new Color(0.16f, 0.15f, 0.19f)),
        };
        static readonly string[] Modifiers = { "無聲的", "倒懸的", "迴旋的", "低垂的", "游移的", "第二次", "反覆的", "細碎的", "逆行的", "沉睡的", "遲來的", "雙重" };
        static readonly string[] Syllables = { "薩", "赫", "倫", "奧", "卡", "瑪", "伊", "塔", "蘇", "拉", "德", "米", "諾", "茵", "法", "澤", "柯", "爾", "巴", "雅", "希", "圖", "露", "凡", "歐", "妲", "契", "姆" };

        // 算一次要拼字串，動物每幀都會問，所以記住最近幾天的結果
        static readonly Dictionary<(int, int, int, int), (bool on, Event e)> _cache = new Dictionary<(int, int, int, int), (bool, Event)>();

        public static bool On(int day, int cx, int cz, out Event e)
        {
            var key = (Seed, day, cx, cz);
            if (_cache.TryGetValue(key, out var hit)) { e = hit.e; return hit.on; }
            if (_cache.Count > 256) _cache.Clear();
            bool on = Make(day, cx, cz, out e);
            _cache[key] = (on, e);
            return on;
        }

        static bool Make(int day, int cx, int cz, out Event e)
        {
            e = default;
            if (day <= 0) return false;
            bool forced = day == ForcedDay && cx == 0 && cz == 0;
            if (!forced && DSTClockwork.H(day, cx, cz, 7500) > 0.3f) return false;

            int R(int i, int n) => (int)(DSTNoise.Hash(Seed ^ (day * 7919 + 13) ^ (i * 31337), cx, cz, 7510) % (uint)n);
            float F(int i) => DSTClockwork.H(day, cx, cz, 7600 + i);

            e.day = day; e.cx = cx; e.cz = cz;
            e.radius = 700f + F(30) * 700f;
            if (forced)
            {
                // 出生點附近：中心離出生點 600~1100 m，走得到，邊緣也看得到
                float a = F(31) * Mathf.PI * 2f, r = 600f + F(32) * 500f;
                e.center = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            }
            else e.center = new Vector2(cx + (F(31) - 0.5f) * 0.6f, cz + (F(32) - 0.5f) * 0.6f) * CellSize;
            e.core = (Core)R(0, 8);
            var (hueWord, hue) = Hues[R(1, Hues.Length)];
            e.haze = hue;
            e.hazeAmount = 0.5f + F(2) * 0.35f;
            e.hazeDistance = 35f + F(3) * 70f;
            e.sun = 1f;
            e.duration = 1.5f + F(4) * 2.5f;
            e.start = F(5) * 24f;
            e.fallColor = Color.Lerp(hue, Color.white, 0.25f);
            e.humPitch = 0.7f + F(6) * 0.7f;

            // 主現象：決定名稱的核心和最強的效果
            string noun;
            bool colorInName = true;
            switch (e.core)
            {
                case Core.DryLightning:
                    noun = "乾雷"; e.lightning = 0.10f + F(7) * 0.08f; e.shelter = true;
                    e.start = 17f + F(5) * 9f;               // 傍晚到半夜，閃電最好看
                    break;
                case Core.StrangeRain:
                    noun = "雨"; e.fall = 1; e.hazeAmount += 0.15f; e.fallColor = hue;
                    break;
                case Core.Ashfall:
                    noun = "降灰"; e.fall = 2; e.hazeAmount = 0.75f; e.hazeDistance = 35f + F(3) * 40f; e.sun = 0.35f;
                    e.fallColor = Color.Lerp(hue, new Color(0.35f, 0.33f, 0.32f), 0.6f);
                    break;
                case Core.SingingSand:
                    noun = "鳴沙"; e.hum = 0.8f; e.hazeAmount *= 0.5f; colorInName = F(8) < 0.4f;
                    break;
                case Core.Parhelion:
                    noun = "幻日"; e.halo = true; e.sun = 1.3f; e.hazeAmount *= 0.4f;
                    e.start = 8.5f + F(5) * 5f; e.duration = Mathf.Min(e.duration, 16.5f - e.start);   // 要有太陽
                    break;
                case Core.DayDark:
                    noun = "晝暝"; e.sun = 0.06f; e.hazeAmount = 0.55f; e.haze = Color.Lerp(hue, Color.black, 0.5f);
                    e.start = 9f + F(5) * 5f;
                    break;
                case Core.HeatBurst:
                    noun = "熱爆"; e.sun = 1.8f; e.shelter = true; e.haze = Color.Lerp(hue, Color.white, 0.6f); e.hazeAmount *= 0.6f;
                    e.duration = 0.8f + F(4) * 1.2f;
                    break;
                default:
                    noun = "寂靜"; e.shelter = true; e.hum = 0.25f; colorInName = false; e.hazeAmount *= 0.5f;
                    break;
            }

            // 附帶效果：每一場都有一點不一樣
            if (e.core != Core.DryLightning && F(9) < 0.25f) e.lightning = 0.02f + F(10) * 0.04f;
            if (e.hum <= 0f && F(11) < 0.25f) e.hum = 0.3f + F(12) * 0.3f;
            if (e.fall == 0 && F(13) < 0.1f) e.fall = 3;
            if (!e.halo && e.start > 8.5f && e.start < 14f && F(14) < 0.2f) e.halo = true;
            if (!e.shelter && F(15) < 0.3f) e.shelter = true;

            // 名稱：專名 +（修飾）+（顏色）+ 核心
            int n = 2 + R(16, 2);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++) sb.Append(Syllables[R(17 + i, Syllables.Length)]);
            e.name = sb.ToString();
            string mod = F(20) < 0.45f ? Modifiers[R(21, Modifiers.Length)] : "";
            e.title = mod + (colorInName ? hueWord : "") + noun;
            return true;
        }

        /// <summary>正在發生、而且離 p 不超過 range（從區域邊緣量）的異象</summary>
        public static void ActiveNear(double t, Vector2 p, float range, List<Event> into)
        {
            into.Clear();
            int day = DSTClockwork.DayOf(t);
            int r = Mathf.CeilToInt((range + 1400f + Edge) / CellSize) + 1;
            int px = DSTClockwork.Cell(p.x, CellSize), pz = DSTClockwork.Cell(p.y, CellSize);
            for (int d = day; d >= day - 1; d--)
            for (int x = px - r; x <= px + r; x++)
            for (int z = pz - r; z <= pz + r; z++)
            {
                if (!On(d, x, z, out var e) || e.TimeK(t) <= 0f) continue;
                if ((e.center - p).magnitude - e.radius > range) continue;
                into.Add(e);
            }
        }

        static readonly List<Event> _tmp = new List<Event>();

        /// <summary>這一點正在籠罩的異象與強度（0~1，時間 × 位置）；沒有就回傳 0</summary>
        public static float Current(double t, Vector2 p, out Event e)
        {
            ActiveNear(t, p, Edge, _tmp);
            float best = 0f; e = default;
            foreach (var a in _tmp)
            {
                float k = a.TimeK(t) * a.SpaceK(p);
                if (k > best) { best = k; e = a; }
            }
            return best;
        }

        /// <summary>這一點的動物要不要躲回窩（會讓動物躲起來的異象）</summary>
        public static bool Shelter(double t, Vector2 p) => Current(t, p, out var e) > 0.5f && e.shelter;

        /// <summary>從 from 起 days 天內，離 p 最近（中心距離 range 內）的下一場異象</summary>
        public static bool Upcoming(Vector2 p, double from, int days, float range, out Event e)
        {
            e = default;
            int day0 = DSTClockwork.DayOf(from);
            int r = Mathf.CeilToInt(range / CellSize) + 1;
            int px = DSTClockwork.Cell(p.x, CellSize), pz = DSTClockwork.Cell(p.y, CellSize);
            for (int d = day0; d < day0 + days; d++)
            {
                float bestDist = float.MaxValue; bool found = false;
                for (int x = px - r; x <= px + r; x++)
                for (int z = pz - r; z <= pz + r; z++)
                {
                    if (!On(d, x, z, out var a) || a.StartAt < from) continue;
                    float dist = (a.center - p).magnitude;
                    if (dist > range || dist >= bestDist) continue;
                    bestDist = dist; e = a; found = true;
                }
                if (found) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ 乾雷：每個時間格最多一道，位置、形狀都固定
        public const float BoltSlotHours = 1f / 120f;

        public struct Bolt
        {
            public long slot;
            public double at;
            public Vector2 where;            // 打在異象區域裡的哪裡（遠遠也看得到）
            public int shape;
        }

        /// <summary>這場異象在這個時間格有沒有打雷（每台電腦算出一樣的位置）</summary>
        public static bool BoltIn(long slot, in Event e, out Bolt b)
        {
            b = default;
            double t = slot * (double)BoltSlotHours;
            float k = e.TimeK(t);
            if (k <= 0f || e.lightning <= 0f) return false;
            int s = (int)slot, salt = (int)(slot >> 31) ^ (e.cx * 7919 + e.cz * 104729);
            float R(int i) => DSTNoise.Hash01(Seed, s, salt, 7520 + i);
            if (R(0) > e.lightning * k * 1.5f) return false;
            b.slot = slot;
            b.at = t + R(1) * BoltSlotHours;
            float a = R(2) * Mathf.PI * 2f, r = Mathf.Sqrt(R(3)) * e.radius;
            b.where = e.center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            b.shape = (int)DSTNoise.Hash(Seed, s, salt, 7530);
            return true;
        }
    }
}
