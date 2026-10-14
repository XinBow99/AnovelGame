using System.Collections.Generic;
using DUST.World;
using UnityEngine;

namespace DUST.Animals
{
    // 能量和食物（只在主機用）。做法照 NetLogo Wolf-Sheep Predation 的「有草」版本：
    //   每個個體有能量，活著就消耗；草食動物吃草、肉食動物吃獵物得到能量；能量用完就餓死。
    //   草被吃掉之後要過一段時間才長回來（grass-regrowth-time）。
    // 能量以 0..1 表示（1 = 儲備全滿）。時間單位是遊戲小時（1 遊戲小時 = 50 秒）。
    public static class DSTMetabolism
    {
        // ------------------------------------------------------------------ 消耗
        // 23 kg 的動物，靜止時從全滿到用完要多久（遊戲小時）【建議：2 遊戲天 = 實際 40 分鐘】
        const float BaseEnduranceHours = 48f;
        // 消耗率 ∝ 體重^0.75（Kleiber 1932），儲備 ∝ 體重，所以撐多久 ∝ 體重^0.25【由來源推論】
        public static float EnduranceHours(float mass) => BaseEnduranceHours * Mathf.Pow(Mathf.Max(0.5f, mass) / 23f, 0.25f);

        // 活動的倍率：走路、跑步比站著耗能【建議】
        public static float ActivityMultiplier(float speed, float walkSpeed, float runSpeed)
        {
            if (speed < 0.1f) return 1f;
            if (speed <= walkSpeed * 1.3f) return 1.5f;
            return Mathf.Lerp(1.5f, 4f, Mathf.InverseLerp(walkSpeed, runSpeed, speed));
        }

        // 能量用完以後，多久會餓死（遊戲小時）【建議】
        public const float StarveHours = 6f;

        // 開始覓食 / 打獵的門檻【建議】
        public const float HungryBelow = 0.6f;

        // ------------------------------------------------------------------ 進食
        // 草食：在滿的草地上低頭吃 3 遊戲小時，能量從 0 補到 1【建議】
        public const float GrazeHoursToFull = 3f;
        // 肉食：吃下自己體重 20% 的肉，能量從 0 補到 1【建議；狼一餐可吃下約體重的 1/5】
        public const float MealFraction = 0.2f;
        // 肉食：吃的速度，1 遊戲小時吃完一整餐【建議】
        public const float MealHours = 1f;

        public static float SecondsPerHour => DSTTimeOfDay.Instance ? DSTTimeOfDay.Instance.dayLengthMinutes * 60f / 24f : 50f;
        public static float Hours(float seconds) => seconds / SecondsPerHour;

        // ------------------------------------------------------------------ 草（16 m 一格，被吃掉後慢慢長回來）
        public const float Cell = 16f;
        const float RegrowHours = 24f;           // 從吃光長回原本的量要 1 遊戲天【建議；對應 grass-regrowth-time】

        struct Patch { public float eaten; public float atHour; }
        static readonly Dictionary<long, Patch> _eaten = new Dictionary<long, Patch>();
        static readonly Dictionary<long, float> _base = new Dictionary<long, float>();
        static int _seed = int.MinValue;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _eaten.Clear(); _base.Clear(); _seed = int.MinValue; }

        static double Now => DSTTimeOfDay.Instance ? DSTTimeOfDay.Instance.TotalHours : Time.time / 50.0;

        static void CheckWorld()
        {
            int seed = DSTWorld.Instance ? DSTWorld.Instance.Seed : 0;
            if (seed == _seed) return;
            _seed = seed; _eaten.Clear(); _base.Clear();
        }

        /// <summary>這一格現在有多少草（0..1）</summary>
        public static float Forage(Vector3 p)
        {
            CheckWorld();
            int cx = Mathf.FloorToInt(p.x / Cell), cz = Mathf.FloorToInt(p.z / Cell);
            long k = Key(cx, cz);
            float b = Base(k, cx, cz);
            if (!_eaten.TryGetValue(k, out var e)) return b;
            float regrown = (float)(Now - e.atHour) / RegrowHours * b;
            return Mathf.Clamp(b - Mathf.Max(0f, e.eaten - regrown), 0f, b);
        }

        /// <summary>吃掉一些草，回傳實際吃到的量（0..1 的「格子草量」單位）</summary>
        public static float Eat(Vector3 p, float want)
        {
            float have = Forage(p);
            float got = Mathf.Min(have, want);
            if (got <= 0f) return 0f;
            int cx = Mathf.FloorToInt(p.x / Cell), cz = Mathf.FloorToInt(p.z / Cell);
            long k = Key(cx, cz);
            float b = Base(k, cx, cz);
            _eaten[k] = new Patch { eaten = b - (have - got), atHour = (float)Now };
            return got;
        }

        // 每一格原本的草量，由地形決定【建議】：綠洲水邊最多，沙海最少
        static float Base(long k, int cx, int cz)
        {
            if (_base.TryGetValue(k, out float v)) return v;
            var gen = DSTWorld.Instance ? DSTWorld.Instance.Gen : null;
            if (gen == null) v = 0.4f;
            else
            {
                float x = (cx + 0.5f) * Cell, z = (cz + 0.5f) * Cell;
                var s = gen.SettlementContaining(x, z, 25f);
                if (s != null && s.pondRadius > 0f && Vector2.Distance(new Vector2(x, z), s.center) < s.plazaRadius + 20f) v = 1f;
                else
                {
                    gen.RegionAt(x, z, out var kind);
                    v = kind switch
                    {
                        DSTWorldGen.RegionKind.Basin => 0.6f,
                        DSTWorldGen.RegionKind.Plain => 0.45f,
                        DSTWorldGen.RegionKind.Settlement => 0.5f,
                        DSTWorldGen.RegionKind.Ruins => 0.4f,
                        DSTWorldGen.RegionKind.Camp => 0.35f,
                        DSTWorldGen.RegionKind.Mesa => 0.3f,
                        _ => 0.15f,   // 沙海
                    };
                }
            }
            _base[k] = v;
            return v;
        }

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
