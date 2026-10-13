using System.Collections.Generic;
using DUST.World;
using UnityEngine;

namespace DUST.Animals
{
    // 生態：哪裡、什麼時候會出現哪些物種。全部由「世界種子 + 地區格 + 地形種類」算出來，不用存任何東西。
    // 地區格跟左下角「你來到了…」的地名一樣（1.2 km 一區）：同一個「某某沙海」裡永遠是同一批物種。
    public static class DSTEcology
    {
        public struct Roster
        {
            public string region;
            public DSTWorldGen.RegionKind kind;
            public int[] keys;
        }

        const int Dr2Seed = 0x5EED;   // 遺跡二地圖沒有種子：用固定的一組

        static readonly Dictionary<long, int[]> _rosters = new Dictionary<long, int[]>();

        public static Roster At(Vector3 p)
        {
            var world = DSTWorld.Instance;
            if (!world || !world.IsRandom || world.Gen == null)
                return new Roster { region = "遺跡二", kind = DSTWorldGen.RegionKind.Ruins, keys = Keys(Dr2Seed, 0, 0, DSTWorldGen.RegionKind.Settlement) };

            var gen = world.Gen;
            string name = gen.RegionAt(p.x, p.z, out var kind);
            int rx = Mathf.FloorToInt(p.x / DSTWorldGen.RegionCell), rz = Mathf.FloorToInt(p.z / DSTWorldGen.RegionCell);
            return new Roster { region = name, kind = kind, keys = Keys(gen.Seed, rx, rz, Group(kind)) };
        }

        // 營地、聚落、遺跡算同一類（人住的地方：小型、會在人附近活動）
        static DSTWorldGen.RegionKind Group(DSTWorldGen.RegionKind k) =>
            k == DSTWorldGen.RegionKind.Camp || k == DSTWorldGen.RegionKind.Ruins ? DSTWorldGen.RegionKind.Settlement : k;

        static int[] Keys(int seed, int rx, int rz, DSTWorldGen.RegionKind kind)
        {
            long id = ((long)seed * 73856093L) ^ ((long)rx * 19349663L) ^ ((long)rz * 83492791L) ^ (long)kind;
            if (_rosters.TryGetValue(id, out var keys)) return keys;
            int n = 3 + (int)(DSTNoise.Hash(seed, rx, rz, 900 + (int)kind) % 2u);
            keys = new int[n];
            for (int i = 0; i < n; i++) keys[i] = SpeciesKey(seed, rx, rz, kind, i);
            _rosters[id] = keys;
            return keys;
        }

        /// <summary>物種 key：雜湊值，但除以 7 的餘數固定是地形種類（DSTGenome 靠它決定地區特色）</summary>
        public static int SpeciesKey(int seed, int rx, int rz, DSTWorldGen.RegionKind kind, int slot)
        {
            uint h = DSTNoise.Hash(seed, rx * 31 + slot, rz, 4100 + (int)kind);
            uint k = (h % (uint)(int.MaxValue / 7)) * 7u + (uint)kind;
            return (int)k;
        }

        // ------------------------------------------------------------------ 習性
        public static bool ActiveAt(Activity a, float hour) => a switch
        {
            Activity.Diurnal => hour >= 6f && hour < 18.5f,
            Activity.Nocturnal => hour >= 19f || hour < 5.5f,
            Activity.Crepuscular => (hour >= 4.5f && hour < 9f) || (hour >= 16.5f && hour < 21f),
            _ => true,
        };

        public static string ActivityName(Activity a) => a switch
        {
            Activity.Diurnal => "日行",
            Activity.Nocturnal => "夜行",
            Activity.Crepuscular => "晨昏活動",
            _ => "不定時",
        };

        public static string TerrainName(Terrain t) => t switch { Terrain.Open => "開闊地", Terrain.Rocky => "岩石區", _ => "各種地形" };

        public static float Hour => DSTTimeOfDay.Instance ? DSTTimeOfDay.Instance.Hour : 12f;

        /// <summary>這個點適不適合這個物種（遺跡二沒有地形資料：都可以）</summary>
        public static bool TerrainOk(Terrain t, Vector3 p)
        {
            if (t == Terrain.Any) return true;
            var world = DSTWorld.Instance;
            if (!world || !world.IsRandom || world.Gen == null) return true;
            world.Gen.Sample(p.x, p.z, out float rock, out float road);
            float slope = Vector3.Angle(world.Gen.Normal(p.x, p.z), Vector3.up);
            return t == Terrain.Open ? rock < 0.3f && slope < 14f && road < 0.5f : rock > 0.35f || slope > 10f;
        }
    }
}
