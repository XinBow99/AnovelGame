using System;
using System.Collections.Generic;
using DUST.World;
using UnityEngine;

namespace DUST.Animals
{
    // 程式生成物種的「基因」：只由一個整數 key 決定（key = 世界種子 + 地區格 + 地形種類 + 第幾種）。
    // 每台電腦用同一個 key 算出一模一樣的結果，所以不用存檔、也不用連線同步，只要傳 key。
    // 外型 = 從 Blender 做的零件庫（SK_DST_CreatureKit）每個欄位挑一個 + 骨頭比例 + 毛色；拼裝在 DSTCreatureAssembler。
    // 尺寸：size = 相對零件庫原始大小（鬃狼骨架，半尺寸世界肩高約 0.37 m）的倍率。
    public enum Terrain { Any, Open, Rocky }
    public enum Pattern { Plain, Stripes, Spots, Saddle }
    public enum Diet { Herbivore, Carnivore }
    // 身體藍圖：四足（鬃狼骨架零件庫）或三輻射三足（沙陶翻盤獸，docs/tripod_A_spec.md）
    public enum BodyPlan { Quadruped, Triradial }

    [Serializable]
    public sealed class DSTGenome
    {
        public int key;
        public string name;
        public string scientificName;

        // ---- 零件（對應 KIT_<欄位>_<名稱>；空字串 = 沒有）
        public string body, head, ears, tail, horns;
        public bool eyes;

        // ---- 比例（骨頭）
        public float size;            // 整體大小倍率
        public float legLen, neckLen, bodyLen, tailLen, headSize;

        // ---- 由比例算出來的尺寸（碰撞、對話泡泡、生成器用）
        public float hipHeight, bodyLength, bodyRadius, neckLength, headRadius;

        // ---- 毛色
        public Color coat, belly, mark, hoof;
        public Pattern pattern; public float patternScale;

        // ---- 習性
        public Temperament temperament;
        public Activity activity;
        public Terrain terrain;
        public Vector2Int group;
        public float cohesion;        // 群體跟得多緊（0 獨居 ~ 1 緊密成群）
        public float grazeChance;     // 停下來低頭吃東西 / 嗅聞的機率
        public int maxAlive;
        public float walkSpeed, runSpeed, notice;
        public Diet diet;
        public BodyPlan bodyPlan;
        public bool Triradial => bodyPlan == BodyPlan.Triradial;

        // 體重（公斤）：零件庫原始大小當作鬃狼（約 23 kg），體重跟長度的三次方走【建議】
        public float Mass => 23f * size * size * size;
        public bool Carnivore => diet == Diet.Carnivore;

        static readonly Dictionary<int, DSTGenome> _cache = new Dictionary<int, DSTGenome>();

        public static DSTGenome Get(int key)
        {
            if (!_cache.TryGetValue(key, out var g)) _cache[key] = g = Generate(key);
            return g;
        }

        // ------------------------------------------------------------------ 演化出來的新物種（第 5 階段）
        // 新物種 = 發條物種（root）的基因 × 累積的特徵倍率（大小、跑速、警覺距離）；名字用新 key 重新取。
        // 只要知道 (key, root, 倍率) 每台電腦都算得出一樣的結果，所以連線只要傳這三個。
        public int root;                       // 外型來自哪個發條物種（自己就是發條物種 = key）
        public Vector3 lineage = Vector3.one;  // 相對 root 的累積倍率：x 大小、y 跑速、z 警覺距離
        public bool IsDerived => root != 0 && root != key;

        public static DSTGenome Derive(int key, int rootKey, Vector3 mul)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.IsDerived) return cached;
            var src = Get(rootKey);
            var g = (DSTGenome)src.MemberwiseClone();
            g.key = key;
            g.root = rootKey;
            g.lineage = mul;
            g.size = src.size * mul.x;
            g.hipHeight = src.hipHeight * mul.x;
            g.bodyLength = src.bodyLength * mul.x;
            g.bodyRadius = src.bodyRadius * mul.x;
            g.neckLength = src.neckLength * mul.x;
            g.headRadius = src.headRadius * mul.x;
            g.walkSpeed = src.walkSpeed * mul.y;
            g.runSpeed = src.runSpeed * mul.y;
            g.notice = src.notice * mul.z;
            // 新名字：外型零件一樣，顏色字、特徵字重抽；跟祖先撞名就加「新」
            MakeNames(g, new System.Random(key));
            if (g.name == src.name) g.name = "新" + g.name;
            _cache[key] = g;
            return g;
        }

        // ------------------------------------------------------------------ 零件目錄（跟 Blender 零件庫同名）
        // 身體粗細（碰撞用）
        static readonly Dictionary<string, float> Girth = new Dictionary<string, float>
        {
            { "Canid", 1f }, { "Ungulate", 1.2f }, { "Burrower", 1.45f }, { "Humped", 1.15f }, { "Stoneback", 1.35f }, { "Spined", 1.05f },
        };
        static readonly HashSet<string> EyelessHeads = new HashSet<string> { "Stone" };

        // 地形 → 身體的權重（台地多「岩石型」外星生物、沙海多大耳細腿、盆地多有蹄）
        static (string, float)[] BodiesFor(DSTWorldGen.RegionKind k) => k switch
        {
            DSTWorldGen.RegionKind.DuneSea => new[] { ("Canid", 4f), ("Humped", 3f), ("Ungulate", 1f), ("Spined", 1f), ("Burrower", 0.5f) },
            DSTWorldGen.RegionKind.Mesa => new[] { ("Stoneback", 4f), ("Burrower", 2f), ("Spined", 2f), ("Ungulate", 1f) },
            DSTWorldGen.RegionKind.Basin => new[] { ("Ungulate", 4f), ("Humped", 2f), ("Canid", 1f), ("Burrower", 1f) },
            DSTWorldGen.RegionKind.Settlement => new[] { ("Canid", 3f), ("Burrower", 2f), ("Spined", 1f) },
            _ => new[] { ("Canid", 3f), ("Ungulate", 3f), ("Humped", 1f), ("Spined", 1f), ("Burrower", 1f), ("Stoneback", 0.5f) },
        };

        static (string, float)[] HeadsFor(string body) => body switch
        {
            "Ungulate" or "Humped" => new[] { ("Ungulate", 4f), ("Probe", 1f) },
            "Burrower" => new[] { ("Probe", 1.5f), ("Canid", 1.5f), ("Stone", 1.5f) },
            "Stoneback" => new[] { ("Stone", 4f), ("Probe", 0.5f) },
            "Spined" => new[] { ("Canid", 2f), ("Stone", 2f), ("Probe", 1f) },
            _ => new[] { ("Canid", 4f), ("Probe", 0.4f), ("Stone", 0.6f) },
        };

        static (string, float)[] EarsFor(string head, DSTWorldGen.RegionKind k) => head switch
        {
            "Stone" => new[] { ("Frill", 3f), ("", 2f) },
            "Ungulate" => new[] { ("Pointy", 4f), ("", 1f) },
            "Probe" => new[] { ("Pointy", 2f), ("Frill", 1f), ("", 1f) },
            _ => new[] { ("Fennec", k == DSTWorldGen.RegionKind.DuneSea ? 4f : 1.5f), ("Pointy", 3f), ("Frill", 0.5f), ("", 0.5f) },
        };

        static (string, float)[] TailsFor(string body) => body switch
        {
            "Ungulate" or "Humped" => new[] { ("Whip", 4f), ("Bushy", 1f) },
            "Burrower" => new[] { ("Club", 2f), ("Whip", 2f) },
            "Stoneback" => new[] { ("Club", 4f), ("Whip", 1f) },
            "Spined" => new[] { ("Whip", 3f), ("Club", 1f), ("Bushy", 1f) },
            _ => new[] { ("Bushy", 4f), ("Whip", 1f) },
        };

        static string Pick(System.Random r, (string, float)[] opts)
        {
            float total = 0f;
            foreach (var o in opts) total += o.Item2;
            float x = (float)r.NextDouble() * total;
            foreach (var o in opts) { x -= o.Item2; if (x <= 0f) return o.Item1; }
            return opts[opts.Length - 1].Item1;
        }

        // ------------------------------------------------------------------ 生成
        static DSTGenome Generate(int key)
        {
            var r = new System.Random(key);
            float F(float a, float b) => a + (float)r.NextDouble() * (b - a);
            bool Chance(double p) => r.NextDouble() < p;
            var kind = (DSTWorldGen.RegionKind)((uint)key % 7u);   // key 的低位帶著地形種類（見 DSTEcology.SpeciesKey）

            var g = new DSTGenome { key = key };

            // ---- 零件
            g.body = Pick(r, BodiesFor(kind));
            g.head = Pick(r, HeadsFor(g.body));
            g.ears = Pick(r, EarsFor(g.head, kind));
            g.tail = Pick(r, TailsFor(g.body));
            double hornChance = g.body == "Ungulate" || g.body == "Humped" ? 0.55 : g.body == "Stoneback" ? 0.25 : 0.1;
            g.horns = Chance(hornChance) ? (Chance(0.55) ? "Straight" : "Curl") : "";
            g.eyes = !EyelessHeads.Contains(g.head);

            // ---- 比例：小動物比較多；人住的地方只有小型的
            float t = (float)Math.Pow(r.NextDouble(), 1.5);
            g.size = Mathf.Lerp(0.6f, 1.8f, t);
            if (kind == DSTWorldGen.RegionKind.Settlement) g.size = Mathf.Min(g.size, 0.95f);
            g.legLen = F(0.75f, 1.3f) * (kind == DSTWorldGen.RegionKind.DuneSea ? 1.12f : 1f) * (g.body == "Burrower" ? 0.8f : 1f);
            g.neckLen = F(0.7f, 1.5f) * (g.body == "Ungulate" || g.body == "Humped" ? 1.15f : 1f);
            g.bodyLen = F(0.85f, 1.25f);
            g.tailLen = F(0.6f, 1.4f);
            g.headSize = F(0.85f, 1.2f);

            g.hipHeight = 0.37f * g.size * g.legLen;
            g.bodyLength = 0.4f * g.size * g.bodyLen;
            g.bodyRadius = 0.05f * g.size * (Girth.TryGetValue(g.body, out var gi) ? gi : 1f);
            g.neckLength = 0.11f * g.size * g.neckLen;
            g.headRadius = 0.035f * g.size * g.headSize;

            Palette(g, r, kind);

            // ---- 習性：大型的比較不怕人、成群；小型的膽小、常獨居
            float big = Mathf.InverseLerp(0.6f, 1.8f, g.size);
            g.temperament = r.NextDouble() < 0.25 + big * 0.4 ? Temperament.Passive : Temperament.Shy;
            double a = r.NextDouble();
            g.activity = a < 0.35 ? Activity.Diurnal : a < 0.6 ? Activity.Crepuscular : a < 0.85 ? Activity.Nocturnal : Activity.Any;
            g.terrain = kind == DSTWorldGen.RegionKind.Mesa || g.body == "Stoneback" ? (Chance(0.7) ? Terrain.Rocky : Terrain.Any)
                      : (Terrain)r.Next(0, 3);
            bool herd = Chance(0.2 + big * 0.5);
            g.group = herd ? new Vector2Int(2, r.Next(3, 7)) : new Vector2Int(1, Chance(0.3) ? 2 : 1);
            g.cohesion = herd ? F(0.5f, 0.9f) : F(0f, 0.3f);
            g.grazeChance = F(0.2f, 0.6f);
            g.maxAlive = herd ? Mathf.Max(g.group.y + 1, 5) : r.Next(2, 5);

            g.walkSpeed = 0.55f * g.size * g.legLen * F(0.9f, 1.15f);
            g.runSpeed = Mathf.Clamp(3.0f * g.size * g.legLen * F(0.85f, 1.15f), 2.2f, 6f);
            g.notice = Mathf.Lerp(7f, 12f, (float)r.NextDouble());

            MakeNames(g, r);
            g.diet = DietOf(key, g);
            if (DSTNoise.Hash01(key, 0, 0, 6200) < TriradialChance(kind)) MakeTriradial(g, key);
            return g;
        }

        // ------------------------------------------------------------------ 三輻射三足（沙陶翻盤獸）
        // 用另一個雜湊決定（不動上面的亂數序列，其他物種完全不變）。台地、沙海多外星型的生物，比例高一點【建議：整體約 1 成】
        static float TriradialChance(DSTWorldGen.RegionKind k) => k switch
        {
            DSTWorldGen.RegionKind.Mesa => 0.22f,
            DSTWorldGen.RegionKind.DuneSea => 0.18f,
            DSTWorldGen.RegionKind.Settlement or DSTWorldGen.RegionKind.Camp or DSTWorldGen.RegionKind.Ruins => 0f,
            _ => 0.07f,
        };

        // 三足模型在 size 1 時寬約 1.2 m、身體中心離地 0.46 m；縮成 0.7 倍，跟同 size 的四足差不多佔地
        public const float TriradialModelScale = 0.7f;

        static void MakeTriradial(DSTGenome g, int key)
        {
            g.bodyPlan = BodyPlan.Triradial;
            g.body = "Triradial";
            g.head = ""; g.ears = ""; g.tail = ""; g.horns = "";
            g.eyes = true;
            float s = g.size * TriradialModelScale;
            g.hipHeight = 0.46f * s;          // 身體中心高
            g.bodyLength = 0.5f * s;          // 圓盤直徑
            g.bodyRadius = 0.22f * s;
            g.neckLength = 0f;
            g.headRadius = 0.06f * s;
            // 三腳的步伐：跑速上限是四足的 0.8 倍（三腳狗著地時間變長，Jarvis 2013；倍數是建議）
            g.walkSpeed *= 0.8f;
            g.runSpeed = Mathf.Max(1.8f, g.runSpeed * 0.8f);
            // 三點站著不花力氣（Thomson 2019）：適合站著等，成群的少
            g.group = new Vector2Int(1, g.group.y > 2 ? 2 : 1);
            g.cohesion = Mathf.Min(g.cohesion, 0.3f);
            g.terrain = Terrain.Any;
            // 埋伏型：一部分吃肉【建議 0.35】
            g.diet = DSTNoise.Hash01(key, 0, 0, 6201) < 0.35f ? Diet.Carnivore : Diet.Herbivore;
            MakeNames(g, new System.Random(key ^ 0x7417));
        }

        // 食性用另外一個雜湊決定（不動上面的亂數序列，現有物種的外型、習性都不會變）
        // 犬型、棘型的身體比較常是肉食；有蹄、駝、岩背都吃草【建議】
        static Diet DietOf(int key, DSTGenome g)
        {
            if (g.head == "Ungulate") return Diet.Herbivore;
            float p = g.body switch { "Canid" => 0.55f, "Spined" => 0.45f, "Burrower" => 0.2f, _ => 0f };
            return DSTNoise.Hash01(key, 0, 0, 6100) < p ? Diet.Carnivore : Diet.Herbivore;
        }

        /// <summary>
        /// 這個肉食物種會不會獵這種動物。依 Carbone et al. 1999（Nature）：約 21.5 kg 以下的肉食動物吃比自己輕一半以下的獵物，
        /// 以上的改吃大型獵物；Tucker et al. 2016：大型肉食動物的獵物 ≥ 自己體重的 45%。上限（成群 3 倍、獨居 1.5 倍）是【建議】。
        /// </summary>
        public static bool CanEat(DSTGenome predator, float predatorMass, int preyKey, float preyMass)
        {
            if (predator == null || !predator.Carnivore || preyKey == predator.key) return false;
            if (predatorMass < 21.5f) return preyMass <= predatorMass * 0.5f;
            return preyMass >= predatorMass * 0.45f && preyMass <= predatorMass * (predator.group.y > 2 ? 3f : 1.5f);
        }

        // 沙漠動物多是保護色：沙、赭、灰褐；偶爾有比較顯眼的斑紋
        static void Palette(DSTGenome g, System.Random r, DSTWorldGen.RegionKind kind)
        {
            float F(float a, float b) => a + (float)r.NextDouble() * (b - a);
            // 亮度（sRGB）：沙地約 0.65，動物大多跟環境差不多亮，HDRP 強光下才不會變成一團黑
            float hue = F(0.03f, 0.12f);
            float sat = F(0.25f, 0.55f);
            float val = F(0.5f, 0.85f);
            if (kind == DSTWorldGen.RegionKind.DuneSea) { val = F(0.7f, 0.9f); sat *= 0.75f; }
            if (kind == DSTWorldGen.RegionKind.Mesa) { hue = F(0.0f, 0.05f); val = F(0.45f, 0.7f); sat = F(0.35f, 0.6f); }
            double morph = r.NextDouble();
            if (morph < 0.2 || g.body == "Stoneback") { sat *= 0.15f; val = F(0.55f, 0.8f); }    // 灰色系（岩背一律灰）
            else if (morph < 0.25) { val *= 0.6f; sat *= 0.7f; }                                   // 少數深色型
            g.coat = Color.HSVToRGB(hue, sat, val);
            g.belly = Color.HSVToRGB(hue, sat * F(0.1f, 0.6f), Mathf.Min(0.95f, val + F(0.05f, 0.45f)));
            g.mark = Color.HSVToRGB(Mathf.Repeat(hue + F(-0.03f, 0.02f), 1f), Mathf.Min(1f, sat + 0.1f), val * F(0.15f, 0.4f));
            g.hoof = Color.HSVToRGB(hue, sat * 0.5f, val * 0.25f);
            double p = r.NextDouble();
            g.pattern = p < 0.35 ? Pattern.Plain : p < 0.6 ? Pattern.Stripes : p < 0.85 ? Pattern.Spots : Pattern.Saddle;
            g.patternScale = F(0.6f, 1.6f);
        }

        // ------------------------------------------------------------------ 名字：顏色 + 零件特徵 + 身體類型
        static readonly string[] ColorWords = { "沙", "赭", "灰", "褐", "淡", "黃", "棕" };
        static readonly Dictionary<string, string[]> Nouns = new Dictionary<string, string[]>
        {
            { "Canid", new[] { "狐", "豺", "犬" } }, { "Ungulate", new[] { "羚", "鹿", "麂" } }, { "Burrower", new[] { "獾", "甲獸", "穴獸" } },
            { "Humped", new[] { "駝", "駝羚" } }, { "Stoneback", new[] { "岩獸", "石背獸" } }, { "Spined", new[] { "棘犬", "刺獸" } },
            { "Triradial", new[] { "翻盤獸", "盤足獸" } },
        };
        static readonly string[] LatinA = { "Psam", "Xer", "Ere", "Dor", "Ammo", "Pyr", "Cal", "Thal", "Kor", "Sab", "Lith" };
        static readonly string[] LatinB = { "ocyon", "ictis", "odon", "ophus", "otherium", "amys", "elaphus", "ogale" };
        static readonly string[] LatinC = { "arenarius", "deserti", "pallidus", "nocturnus", "rupestris", "fulvus", "velox", "gregarius", "caecus" };

        static void MakeNames(DSTGenome g, System.Random r)
        {
            string color = g.coat.grayscale < 0.25f ? "暗" : ColorWords[r.Next(ColorWords.Length)];
            var traits = new List<string>();
            if (g.head == "Stone") traits.Add("盲");
            if (g.head == "Probe") traits.Add("長吻");
            if (g.ears == "Fennec") traits.Add("大耳");
            if (g.ears == "Frill") traits.Add("鰭耳");
            if (g.horns == "Curl") traits.Add("捲角");
            if (g.horns == "Straight") traits.Add("長角");
            if (g.tail == "Club") traits.Add("錘尾");
            string mark = g.pattern switch { Pattern.Stripes => "紋", Pattern.Spots => "斑", Pattern.Saddle => "鞍", _ => "" };
            string trait = traits.Count > 0 ? traits[r.Next(traits.Count)] : "";
            var nouns = Nouns.TryGetValue(g.body, out var ns) ? ns : new[] { "獸" };
            g.name = color + mark + trait + nouns[r.Next(nouns.Length)];
            g.scientificName = LatinA[r.Next(LatinA.Length)] + LatinB[r.Next(LatinB.Length)] + " " + (g.eyes ? LatinC[r.Next(LatinC.Length - 1)] : "caecus");
        }

        // ------------------------------------------------------------------ 給現有 AI（DSTAnimal）用的物種資料
        static readonly Dictionary<int, DSTSpeciesDefinition> _species = new Dictionary<int, DSTSpeciesDefinition>();

        public DSTSpeciesDefinition Species
        {
            get
            {
                if (_species.TryGetValue(key, out var s) && s) return s;
                s = ScriptableObject.CreateInstance<DSTSpeciesDefinition>();
                s.name = "SPC_Gen_" + key;
                s.hideFlags = HideFlags.DontSave;
                s.displayName = name;
                s.scientificName = scientificName;
                float big = Mathf.InverseLerp(0.6f, 1.8f, size);
                s.maxHealth = Mathf.Lerp(4f, 18f, big);
                s.walkSpeed = walkSpeed;
                s.runSpeed = runSpeed;
                s.turnSpeedDeg = Mathf.Lerp(420f, 220f, big);
                s.acceleration = runSpeed * 3f;
                s.temperament = temperament;
                s.activity = activity;
                s.noticeRadius = notice;
                s.fleeRadius = notice * 0.5f;
                s.calmRadius = notice * 1.8f;
                s.sniffChance = grazeChance;
                s.wanderRadius = 6f + size * 6f;
                s.fleeDistance = 12f + size * 6f;
                s.herdCohesion = cohesion;
                s.maxAlive = maxAlive;
                s.groupSize = group;
                _species[key] = s;
                return s;
            }
        }
    }
}
