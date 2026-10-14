using DUST.World;
using UnityEngine;
using SpeciesPop = DUST.World.DSTSave.SpeciesPop;
using RegionFile = DUST.World.DSTSave.RegionFile;

namespace DUST.Animals
{
    // 演化（第 5 階段，只在房主）：每個地區格、每個物種的三個特徵平均值，每一代（5 遊戲天）更新一次。
    //   特徵：體型（大小）、跑速、警覺距離。都用對數記（0 = 跟發條物種一樣；0.1 ≈ 大 10%）。
    //
    // 一代的變化【見 docs/animal_ecology_research.md §3.11】：
    //   1. 天擇：近處死掉的個體各自記下「跟族群平均差多少」。死掉的偏哪邊，活下來的平均就往反方向移：
    //        選擇差 S = −Σ(死者偏差) / 存活數；反應 R = h² × S（育種者方程式，Lush；Falconer & Mackay）。
    //      所以玩家專挑某種個體打（或者跑得慢的比較常被抓到），族群就往相反方向演化（Coltman 2003 大角羊、Ciuti 2012 麋鹿）。
    //      遠處的捕食（看不到個體）當作隨機，不造成天擇。
    //   2. 漂變：小族群的平均會隨機飄，每代變異數 = h²σ² / N（Lande 1976）。用種子算的亂數，結果固定。
    //   3. 回復：沒有天擇時慢慢回到原本的樣子，每代回 1%【建議】。Coltman：停獵之後恢復比衰退慢得多。
    //   4. 分化：跟祖先比，用 Tobias et al. 2010 的計分（效果量 d = 平均差 / 個體標準差）：
    //        d 0.2–2 = 1 分、2–5 = 2 分、5–10 = 3 分、>10 = 4 分；型態特徵最多算 2 個；總分 ≥ 7 = 不同物種。
    //      這裡型態 = 體型 + 跑速，行為 = 警覺距離。到了就整個地區的族群改名成新物種（前進演化）。
    public static class DSTEvolution
    {
        // 遺傳率：行為 0.31（Stirling, Réale & Roff 2002 彙整）；型態比行為和生活史高（Mousseau & Roff 1987），
        // 常被引用的型態平均約 0.46【數字未核對原文】
        public static readonly Vector3 H2 = new Vector3(0.46f, 0.46f, 0.31f);
        // 個體之間的差異（變異係數 ≈ 對數標準差）【建議】：長度類型態約 5%、跑速 8%、警覺距離（行為）15%
        public static readonly Vector3 CV = new Vector3(0.05f, 0.08f, 0.15f);
        const float ReturnPerGeneration = 0.01f;    // 【建議】
        public const int SpeciesScore = 7;          // Tobias et al. 2010

        // ------------------------------------------------------------------ 個體
        /// <summary>新生成的個體跟族群平均差多少（常態分布，主機的亂數就好，不用固定）</summary>
        public static Vector3 RandomDev() => new Vector3(Normal() * CV.x, Normal() * CV.y, Normal() * CV.z);

        static float Normal()
        {
            float u1 = 1f - Random.value, u2 = Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }

        public static Vector3 Mean(SpeciesPop s) => s != null ? new Vector3(s.zSize, s.zSpeed, s.zNotice) : Vector3.zero;

        /// <summary>一隻死掉：記下偏差，等這一代結束一起算</summary>
        public static void RecordDeath(SpeciesPop s, Vector3 dev)
        {
            s.selSize += dev.x;
            s.selSpeed += dev.y;
            s.selNotice += dev.z;
            s.selDeaths++;
        }

        // ------------------------------------------------------------------ 一代
        public static void Generation(int seed, RegionFile r, SpeciesPop s, int generation)
        {
            if (s.extinct || s.n < 1f) { ClearSelection(s); return; }
            float n = Mathf.Max(1f, s.n);
            var z = Mean(s);
            var sel = new Vector3(s.selSize, s.selSpeed, s.selNotice);
            for (int t = 0; t < 3; t++)
            {
                float S = -sel[t] / n;                                         // 選擇差
                float drift = Mathf.Sqrt(H2[t] / n) * CV[t] * HashNormal(seed, r, s, generation, t);
                z[t] = (z[t] + H2[t] * S + drift) * (1f - ReturnPerGeneration);
            }
            s.zSize = z.x; s.zSpeed = z.y; s.zNotice = z.z;
            ClearSelection(s);
        }

        static void ClearSelection(SpeciesPop s) { s.selSize = s.selSpeed = s.selNotice = 0f; s.selDeaths = 0; }

        // 固定的常態亂數（同一個世界、地區、物種、第幾代 → 同一個值）
        static float HashNormal(int seed, RegionFile r, SpeciesPop s, int gen, int trait)
        {
            float u1 = Mathf.Max(1e-6f, DSTNoise.Hash01(seed ^ s.key, r.rx * 7919 + gen, r.rz, 8100 + trait * 2));
            float u2 = DSTNoise.Hash01(seed ^ s.key, r.rx * 7919 + gen, r.rz, 8101 + trait * 2);
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }

        // ------------------------------------------------------------------ 分化（Tobias et al. 2010 計分）
        public static int ScoreOf(float d)
        {
            d = Mathf.Abs(d);
            return d > 10f ? 4 : d >= 5f ? 3 : d >= 2f ? 2 : d >= 0.2f ? 1 : 0;
        }

        /// <summary>跟祖先差幾分：體型 + 跑速（型態，最多 2 個）+ 警覺距離（行為）</summary>
        public static int Score(SpeciesPop s, out Vector3 d)
        {
            d = new Vector3(s.zSize / CV.x, s.zSpeed / CV.y, s.zNotice / CV.z);
            return ScoreOf(d.x) + ScoreOf(d.y) + ScoreOf(d.z);
        }

        /// <summary>新物種的 key：雜湊值，但除以 7 的餘數保留地形種類（跟 DSTEcology.SpeciesKey 一樣）</summary>
        public static int NewKey(int seed, int parentKey, RegionFile r)
        {
            uint h = DSTNoise.Hash(seed ^ parentKey, r.rx, r.rz, 8200 + r.day);
            uint k = (h % (uint)(int.MaxValue / 7)) * 7u + (uint)((uint)parentKey % 7u);
            return (int)k;
        }
    }
}
