using System.Collections.Generic;
using DUST.Player;
using DUST.World;
using UnityEngine;
using RegionFile = DUST.World.DSTSave.RegionFile;
using SpeciesPop = DUST.World.DSTSave.SpeciesPop;

namespace DUST.Animals
{
    // 遠處的族群（只在房主跑）：每個 1.2 km 地區格、每個物種一個數量，每過一個遊戲天推進一次。
    // 第一次有玩家走到附近時，從發條世界（DSTMigration 的群）數出起始數量；之後狀態存在 regions/<rx>_<rz>.json。
    // 沒去過的地區不推進，維持發條狀態。
    //
    // 每天的推進【來源 / 由來源推論 / 建議 見 docs/animal_ecology_research.md §3.10】：
    //   草食：Ricker 式成長 N·exp(r(1 − N/K))，K = 發條世界的原始數量；再扣掉被吃掉的。
    //   r：Dillingham et al. 2016 的統合分析，r_max × 世代時間 ≈ 1.0（鳥、哺乳類、軟骨魚都是）；
    //      已決定一代 = 5 遊戲天，所以 r = 0.2 / 天，跟體型無關。
    //   肉食：每天要吃的肉 = 近處個體的代謝模型（DSTMetabolism）推出來的量；
    //      吃到的量照 Holling 第二型功能反應（獵物越少越難抓，抓多了也吃不下）；
    //      數量變化照 Wolf-Sheep 的「能量盈餘 → 繁殖、不足 → 死亡」：N·exp(r(吃到 / 需要 − 1))。
    //      沒有獵物 → 每天 exp(−0.2)，大約 12 天從 10 隻剩不到 1 隻（已決定：沒有獵物就讓牠們餓死）。
    //   玩家打死的個體直接從數量扣掉（hunt 事件）；近處被吃、餓死的只記事件，不扣（遠處的推進已經算過）。
    public static class DSTPopulation
    {
        public const float GenerationDays = 5f;                 // 已決定
        const float RmaxTimesGeneration = 1f;                   // Dillingham et al. 2016
        public static float RPerDay => RmaxTimesGeneration / GenerationDays;
        const float MaxIntakeOverNeed = 1.5f;                   // 獵物多時最多吃到需要量的 1.5 倍【建議】
        const float HalfSaturation = 0.25f;                     // 獵物生物量到承載量的 25% 時，吃到最多量的一半【建議；≥ 0.2 才不會震盪（見文件）】
        const float CarcassMeat = 0.6f;                         // 跟 DSTAnimal 屍體的肉一樣
        const float ExtinctBelow = 1f;                          // 少於 1 隻 = 絕跡
        const int MaxCatchUpDays = 3650;

        static readonly Dictionary<long, RegionFile> _regions = new Dictionary<long, RegionFile>();
        static readonly List<Vector3> _players = new List<Vector3>();
        static int _seed = int.MinValue;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _regions.Clear(); _seed = int.MinValue; }

        public static bool Active => _seed != int.MinValue && DSTSave.Active && DSTMigration.Available;
        public static int Today => (int)System.Math.Floor(DSTMigration.Now / 24.0);
        public static IEnumerable<RegionFile> Regions() => _regions.Values;

        static long Id(int rx, int rz) => ((long)rx << 32) ^ (uint)rz;
        public static Vector2Int RegionOf(Vector3 p) => new Vector2Int(Mathf.FloorToInt(p.x / DSTWorldGen.RegionCell), Mathf.FloorToInt(p.z / DSTWorldGen.RegionCell));

        // ------------------------------------------------------------------ 載入
        public static void Load(int seed)
        {
            _regions.Clear();
            _seed = seed;
            foreach (var r in DSTSave.LoadAllRegions())
            {
                _regions[Id(r.rx, r.rz)] = r;
                DSTPlayerActivity.Import(seed, r.wx, r.wz, r.ws);
                // 演化出來的新物種：重新登記基因（key 是雜湊，不登記的話 Get 會算出一隻完全不同的）
                foreach (var s in r.species)
                    if (s.form != 0) DSTGenome.Derive(s.form, s.key, Lineage(s));
            }
            // 專案關掉了 Domain Reload：靜態事件會留到下一次 Play，先拿掉再加，才不會一隻死掉算好幾次
            DSTAnimal.Died -= OnDied;
            DSTAnimal.Died += OnDied;
            Debug.Log($"[DUST] Population: {_regions.Count} regions loaded");
        }

        /// <summary>這個地區格的族群（沒去過 = null）</summary>
        public static RegionFile Get(int rx, int rz) => _regions.TryGetValue(Id(rx, rz), out var r) ? r : null;

        static RegionFile Track(int rx, int rz, string visitor)
        {
            long id = Id(rx, rz);
            if (_regions.TryGetValue(id, out var r)) return r;
            var gen = DSTWorld.Instance.Gen;
            float cell = DSTWorldGen.RegionCell;
            r = new RegionFile { rx = rx, rz = rz, day = Today, name = gen.RegionAt((rx + 0.5f) * cell, (rz + 0.5f) * cell, out _) };
            // 起始數量 = 發條世界裡窩在這一格的群加起來
            foreach (var b in DSTMigration.BandsIn(rx, rz))
            {
                var s = Find(r, b.key);
                if (s == null) r.species.Add(s = new SpeciesPop { key = b.key, name = b.genome.name });
                s.n += b.count;
                s.n0 += b.count;
            }
            _regions[id] = r;
            if (visitor != null) DSTSave.Log("visit", visitor, r.name, new Vector3((rx + 0.5f) * cell, 0f, (rz + 0.5f) * cell));
            return r;
        }

        static SpeciesPop Find(RegionFile r, int key)
        {
            foreach (var s in r.species) if (s.key == key) return s;
            return null;
        }

        // ------------------------------------------------------------------ 每 2 秒（房主）
        public static void Tick()
        {
            if (!Active) return;
            // 玩家所在地區和周圍 8 格開始記錄（群的路線會跨到隔壁格）
            foreach (var p in DSTNetPlayer.All)
            {
                if (!p || !p.IsSpawned) continue;
                var c = RegionOf(p.transform.position);
                for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                    Track(c.x + i, c.y + j, i == 0 && j == 0 ? p.DisplayName : null);
            }
            int today = Today;
            foreach (var r in _regions.Values) Fold(r, today);
        }

        static void Fold(RegionFile r, int toDay)
        {
            if (r.day >= toDay) return;
            if (toDay - r.day > MaxCatchUpDays) r.day = toDay - MaxCatchUpDays;
            while (r.day < toDay)
            {
                Step(r);
                r.day++;
                if (r.day % (int)GenerationDays == 0) Generation(r);
            }
        }

        // ------------------------------------------------------------------ 一代（每 5 天）：演化、分化
        static void Generation(RegionFile r)
        {
            int gen = r.day / (int)GenerationDays;
            foreach (var s in r.species)
            {
                DSTEvolution.Generation(_seed, r, s, gen);
                if (!s.extinct && DSTEvolution.Score(s, out _) >= DSTEvolution.SpeciesScore) Speciate(r, s);
            }
        }

        static Vector3 Lineage(SpeciesPop s) => s.lineage == Vector3.zero ? Vector3.one : s.lineage;

        /// <summary>整個地區的族群變成新物種：新的 key、新名字，特徵平均併進基因、偏移歸零</summary>
        public static void Speciate(RegionFile r, SpeciesPop s)
        {
            var z = DSTEvolution.Mean(s);
            var lin = Vector3.Scale(Lineage(s), new Vector3(Mathf.Exp(z.x), Mathf.Exp(z.y), Mathf.Exp(z.z)));
            int key = DSTEvolution.NewKey(_seed, s.Form, r);
            var g = DSTGenome.Derive(key, s.key, lin);
            string old = s.name;
            s.form = key;
            s.lineage = lin;
            s.name = g.name;
            s.zSize = s.zSpeed = s.zNotice = 0f;
            float cell = DSTWorldGen.RegionCell;
            DSTSave.Log("speciate", $"{r.name}的{old}", g.name, new Vector3((r.rx + 0.5f) * cell, 0f, (r.rz + 0.5f) * cell), key);
        }

        /// <summary>族群現在的平均體重（物種基因 × 演化的體型偏移³）</summary>
        static float MassOf(SpeciesPop s) => DSTGenome.Get(s.Form).Mass * Mathf.Exp(3f * s.zSize);

        // ------------------------------------------------------------------ 一天
        static readonly List<float> _kills = new List<float>();

        static void Step(RegionFile r)
        {
            float rate = RPerDay;
            int n = r.species.Count;
            _kills.Clear();
            for (int i = 0; i < n; i++) _kills.Add(0f);

            // 肉食：先算每一種吃到多少（用今天開始時的數量），再一起更新
            var next = new float[n];
            for (int c = 0; c < n; c++)
            {
                var sc = r.species[c];
                var gc = DSTGenome.Get(sc.Form);
                next[c] = sc.n;
                if (!gc.Carnivore || sc.n <= 0f) continue;
                // 體重用演化後的：變大的獵物可能從「吃得下」變成「吃不下」（反過來也是）
                float mc = MassOf(sc);
                float biomass = 0f, capacity = 0f;
                for (int i = 0; i < n; i++)
                {
                    var si = r.species[i];
                    float mi = MassOf(si);
                    if (!DSTGenome.CanEat(gc, mc, si.Form, mi)) continue;
                    biomass += Catchable(si) * mi;
                    capacity += si.n0 * mi;
                }
                float need = DailyMeat(mc);
                float intake = 0f;      // 每隻每天吃到的肉（公斤）
                if (biomass > 0f)
                {
                    float half = HalfSaturation * capacity;
                    intake = need * MaxIntakeOverNeed * biomass / (biomass + half);
                    // 吃掉的量依生物量比例分給各種獵物（每隻獵物提供體重 × 0.6 的肉）
                    float eaten = intake * sc.n;
                    for (int i = 0; i < n; i++)
                    {
                        var si = r.species[i];
                        float mi = MassOf(si);
                        if (!DSTGenome.CanEat(gc, mc, si.Form, mi)) continue;
                        _kills[i] += eaten * (Catchable(si) * mi / biomass) / (CarcassMeat * mi);
                    }
                }
                float growth = Mathf.Clamp(rate * (Mathf.Min(intake, need * MaxIntakeOverNeed) / need - 1f), -rate, rate);
                next[c] = sc.n * Mathf.Exp(growth);
            }

            for (int i = 0; i < n; i++)
            {
                var s = r.species[i];
                if (s.extinct) continue;
                var g = DSTGenome.Get(s.Form);
                float v = g.Carnivore ? next[i] : s.n0 > 0f ? s.n * Mathf.Exp(rate * (1f - s.n / s.n0)) : 0f;
                v -= Mathf.Min(_kills[i], Catchable(s));
                s.n = Mathf.Max(0f, v);
                if (s.n < ExtinctBelow) Extinct(r, s);
            }
        }

        // 獵物避難所：原本數量的 10% 躲在抓不到的地方（岩縫、遮蔽處）。
        // Sih 1987（Theor. Popul. Biol. 31:1–12）等理論研究：避難所讓捕食者—獵物系統穩定，不會一起滅絕【比例是建議】
        // 至少留 1 隻：原本只有 1、2 隻的小族群，10% 不到一隻，會被吃到判定絕跡（試玩時 5 天絕跡了 20 種）【建議】
        const float Refuge = 0.1f;
        static float Catchable(SpeciesPop s) => Mathf.Max(0f, s.n - Mathf.Max(Refuge * s.n0, ExtinctBelow));

        /// <summary>肉食動物每天要吃多少肉（公斤）：照 DSTMetabolism，平均活動 1.5 倍，吃自己體重 20% 補滿</summary>
        public static float DailyMeat(float mass) =>
            DSTMetabolism.MealFraction * mass * 24f * 1.5f / DSTMetabolism.EnduranceHours(mass);

        static void Extinct(RegionFile r, SpeciesPop s)
        {
            s.n = 0f;
            if (s.extinct) return;
            s.extinct = true;
            float cell = DSTWorldGen.RegionCell;
            DSTSave.Log("extinct", r.name, s.name, new Vector3((r.rx + 0.5f) * cell, 0f, (r.rz + 0.5f) * cell), s.key);
        }

        // ------------------------------------------------------------------ 近處 ↔ 遠處
        /// <summary>這群現在放出來幾隻：族群少了，群就變小（或整群不見）；不會比發條世界原本的多</summary>
        public static int Visible(DSTBand b)
        {
            var s = Of(b);
            if (s == null || s.n0 <= 0f) return b.count;
            float f = Mathf.Clamp01(s.n / s.n0);
            // 每群用固定的亂數四捨五入（同一天看到的都一樣），整個地區加起來約等於族群數量
            float h = DSTNoise.Hash01((int)b.id, (int)(b.id >> 32), 0, 7700);
            return Mathf.Clamp(Mathf.FloorToInt(b.count * f + h), 0, b.count);
        }

        /// <summary>這群屬於的族群（地區沒在記錄 = null）</summary>
        public static SpeciesPop Of(DSTBand b)
        {
            if (!Active || b == null) return null;
            var c = RegionOf(b.den);
            var r = Get(c.x, c.y);
            return r != null ? Find(r, b.key) : null;
        }

        /// <summary>這群現在是哪個物種（演化成新物種的話是新的 key）</summary>
        public static int FormOf(DSTBand b) { var s = Of(b); return s != null ? s.Form : b.key; }

        /// <summary>這群的族群特徵平均（對數偏移：大小、跑速、警覺距離）</summary>
        public static Vector3 MeanOf(DSTBand b) => DSTEvolution.Mean(Of(b));

        static void OnDied(DSTAnimal a, Transform killer)
        {
            if (!Active || a.Genome == null) return;
            var g = a.Genome;
            var player = killer ? killer.GetComponentInParent<DSTNetPlayer>() : null;
            var predator = killer ? killer.GetComponentInParent<DSTAnimal>() : null;
            // 群裡的個體：不管怎麼死，都記下牠跟族群平均差多少（天擇）
            var s = Of(a.Band);
            if (s != null && !s.extinct) DSTEvolution.RecordDeath(s, a.TraitDev);

            if (predator)
            {
                DSTSave.Log("death", predator.Genome != null ? predator.Genome.name : predator.name, g.name, a.transform.position, g.key);
                return;
            }
            if (!killer)
            {
                DSTSave.Log("starve", "", g.name, a.transform.position, g.key);
                return;
            }
            // 玩家（徒手或開車）打死的：從族群扣掉
            DSTSave.Log("hunt", player ? player.DisplayName : killer.name, g.name, a.transform.position, g.key);
            if (s == null)
            {
                // 不是群裡的（/summon 叫出來的）：照所在地區找
                var c = RegionOf(a.transform.position);
                var rr = Get(c.x, c.y);
                if (rr != null) foreach (var x in rr.species) if (x.Form == g.key) s = x;
            }
            if (s == null || s.extinct) return;
            s.n = Mathf.Max(0f, s.n - 1f);
            if (s.n < ExtinctBelow)
            {
                var c = RegionOf(a.Band != null ? a.Band.den : a.transform.position);
                var rr = Get(c.x, c.y);
                if (rr != null) Extinct(rr, s);
            }
        }
    }
}
