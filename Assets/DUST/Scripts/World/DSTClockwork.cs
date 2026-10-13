using UnityEngine;

namespace DUST.World
{
    // 發條沙漠：所有世界事件都是（世界種子, 累加時數）的函數。不用存、不用同步，連線只要時鐘一樣，大家就同時看到同一件事。
    // 同一個種子的世界，第幾天幾點會發生什麼是固定的：玩家的進度就是「知道」——哪天有沙暴、哪晚有流星、隕石掉在哪。
    //   沙暴：某些天的白天，霧變濃、天變暗、動物躲回窩、地上的足跡被吹平
    //   流星雨：某些夜晚，每一顆流星的時間、方向都固定；當晚有一顆會落地，隕石發光到隔天中午
    //   集體遷徙：某些天某個物種的各群一起走到同一個聚集點（DSTMigration）
    //   遺跡刻文：每處遺跡的石碑只在固定時刻、陽光斜掃碑面時看得到（DSTInscription）
    //   異象：雜湊拼出來、有專名的怪天氣（DSTAnomaly）
    public static class DSTClockwork
    {
        const int Dr2Seed = 0x5EED;

        public static int Seed
        {
            get { var w = DSTWorld.Instance; return w && w.IsRandom ? w.Seed : Dr2Seed; }
        }

        public static double Now => DSTTimeOfDay.Instance ? DSTTimeOfDay.Instance.TotalHours : 12.0;
        public static int DayOf(double totalHours) => (int)System.Math.Floor(totalHours / 24.0);

        static float H(int day, int salt) => DSTNoise.Hash01(Seed, day, 0, salt);

        // ------------------------------------------------------------------ 沙暴
        public const float StormRamp = 0.35f;          // 起風 / 平息各要多久（小時）

        /// <summary>這天有沒有沙暴、幾點開始、持續幾小時（白天 9~17 點之間起風）</summary>
        public static bool StormOn(int day, out float start, out float duration)
        {
            start = 9f + H(day, 7101) * 8f;
            duration = 1.2f + H(day, 7102) * 1.8f;
            if (day <= 0 || day == DSTAnomaly.ForcedDay) return false;   // 開局那天不吹，讓人先看看沙漠；那天留給異象
            return day == 1 + (Seed & 1) || H(day, 7100) < 0.28f;   // 第 2 或第 3 天一定有一場，之後隨機
        }

        /// <summary>沙暴強度 0~1</summary>
        public static float Storm(double t)
        {
            int day = DayOf(t);
            if (!StormOn(day, out float start, out float dur)) return 0f;
            float h = (float)(t - day * 24.0);
            float up = Mathf.InverseLerp(start - StormRamp, start, h);
            float down = 1f - Mathf.InverseLerp(start + dur, start + dur + StormRamp, h);
            float k = Mathf.Clamp01(Mathf.Min(up, down));
            return k * k * (3f - 2f * k);
        }

        /// <summary>最近一次沙暴吹完的時間（之前的足跡都被吹平了）；沒有就回傳很久以前</summary>
        public static double LastStormEnd(double t)
        {
            int day = DayOf(t);
            for (int d = day; d >= day - 1; d--)
            {
                if (!StormOn(d, out float start, out float dur)) continue;
                double mid = d * 24.0 + start + dur * 0.5;     // 吹到一半，舊的就沒了
                if (mid <= t) return System.Math.Min(t, d * 24.0 + start + dur);
            }
            return double.MinValue;
        }

        // ------------------------------------------------------------------ 流星雨
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

        /// <summary>流星雨那晚落地的隕石：落地時間、位置（出生點附近 250~700 m），亮到隔天中午</summary>
        public static bool Meteorite(int day, out double landAt, out Vector2 where)
        {
            landAt = day * 24.0 + ShowerStart + 1f + H(day, 7210) * (ShowerLength - 2f);
            float a = H(day, 7211) * Mathf.PI * 2f, r = 250f + H(day, 7212) * 450f;
            where = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            return ShowerNight(day);
        }

        /// <summary>現在看得到的隕石（還在發熱）：0 = 沒有，1 = 剛落地</summary>
        public static float MeteoriteHeat(double t, out Vector2 where, out double landAt)
        {
            int day = DayOf(t);
            for (int d = day; d >= day - 1; d--)
            {
                if (!Meteorite(d, out landAt, out where)) continue;
                double cool = (d + 1) * 24.0 + 12.0;           // 隔天中午冷掉
                if (t < landAt || t > cool) continue;
                float age = (float)(t - landAt);
                return Mathf.Lerp(1f, 0.08f, Mathf.Pow(age / (float)(cool - landAt), 0.5f));
            }
            where = default; landAt = 0;
            return 0f;
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
    }
}
