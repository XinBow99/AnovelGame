using System;
using System.Collections.Generic;
using DUST.World;
using UnityEngine;

namespace DUST.Animals
{
    // 程式生成物種的「基因」：只由一個整數 key 決定（key = 世界種子 + 地區格 + 地形種類 + 第幾種）。
    // 每台電腦用同一個 key 算出一模一樣的外型、習性、步態，所以不用存檔、也不用連線同步，只要傳 key。
    // 尺寸都是半尺寸世界的公尺（跟玩家、鬃狼一致：鬃狼肩高約 0.4 m）。
    public enum Terrain { Any, Open, Rocky }
    public enum EarShape { None, Round, Pointy, Long }
    public enum Pattern { Plain, Stripes, Spots, Saddle }

    [Serializable]
    public sealed class DSTGenome
    {
        public int key;
        public string name;
        public string scientificName;

        // ---- 外型
        public float size;            // 整體尺度（0.35 小型 ~ 1.3 大型）
        public float bodyLength, bodyRadius, bulk;
        public float hipHeight;       // 臀部離地高度（站姿）
        public float upperLeg, lowerLeg, legRadius;
        public float neckLength, neckRadius, neckPitch;    // neckPitch：脖子往上的角度（度）
        public float headLength, headRadius, snout;        // snout 0..1：嘴吻多長
        public EarShape ears; public float earSize;
        public float tailLength, tailRadius, tailLift;     // tailLift：尾巴往上翹的角度
        public bool horns; public float hornLength, hornCurl;
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

        // ---- 步態
        public float strideWalk, strideRun;   // 一步的長度（公尺）
        public float stepHeight;
        public bool bounds;                   // 跑的時候用跳躍步（前後腳成對），否則小跑步

        static readonly Dictionary<int, DSTGenome> _cache = new Dictionary<int, DSTGenome>();

        public static DSTGenome Get(int key)
        {
            if (!_cache.TryGetValue(key, out var g)) _cache[key] = g = Generate(key);
            return g;
        }

        // ------------------------------------------------------------------ 生成
        static DSTGenome Generate(int key)
        {
            var r = new System.Random(key);
            float F(float a, float b) => a + (float)r.NextDouble() * (b - a);
            bool Chance(double p) => r.NextDouble() < p;
            var kind = (DSTWorldGen.RegionKind)((uint)key % 7u);   // key 的低位帶著地形種類（見 DSTEcology.SpeciesKey）

            var g = new DSTGenome { key = key };

            // 體型：小動物比較多
            float t = (float)Math.Pow(r.NextDouble(), 1.6);
            g.size = Mathf.Lerp(0.35f, 1.3f, t);
            if (kind == DSTWorldGen.RegionKind.Camp || kind == DSTWorldGen.RegionKind.Settlement || kind == DSTWorldGen.RegionKind.Ruins)
                g.size = Mathf.Min(g.size, 0.6f);                 // 人住的地方只有小型的
            g.bulk = F(0.7f, 1.35f);
            g.bodyLength = g.size * F(0.5f, 0.7f);
            g.bodyRadius = g.bodyLength * 0.16f * g.bulk;

            // 腿：沙海的比較長（離熱沙遠一點），台地的比較短壯
            float legK = F(0.45f, 0.9f);
            if (kind == DSTWorldGen.RegionKind.DuneSea) legK *= 1.2f;
            if (kind == DSTWorldGen.RegionKind.Mesa) legK *= 0.85f;
            float leg = g.size * legK * 0.5f;
            g.upperLeg = leg * F(0.45f, 0.55f);
            g.lowerLeg = leg - g.upperLeg;
            g.legRadius = Mathf.Max(0.012f, g.bodyRadius * F(0.22f, 0.36f));
            g.hipHeight = leg * 0.92f + g.bodyRadius * 0.4f;

            g.neckLength = g.bodyLength * F(0.15f, 0.6f);
            g.neckRadius = g.bodyRadius * F(0.45f, 0.65f);
            g.neckPitch = F(15f, 55f);
            g.headLength = g.bodyLength * F(0.24f, 0.36f);
            g.headRadius = g.neckRadius * F(1.0f, 1.3f);
            g.snout = F(0.2f, 1f);

            double e = r.NextDouble();
            g.ears = e < 0.1 ? EarShape.None : e < 0.5 ? EarShape.Round : e < 0.88 ? EarShape.Pointy : EarShape.Long;
            if (kind == DSTWorldGen.RegionKind.DuneSea && g.ears != EarShape.None && Chance(0.4)) g.ears = EarShape.Long;   // 沙漠大耳散熱
            g.earSize = g.headLength * (g.ears == EarShape.Long ? F(0.8f, 1.3f) : F(0.35f, 0.6f));

            g.tailLength = Chance(0.15) ? g.bodyLength * F(0.05f, 0.15f) : g.bodyLength * F(0.3f, 1.2f);
            g.tailRadius = g.bodyRadius * F(0.15f, 0.4f);
            g.tailLift = F(-35f, 25f);

            g.horns = g.size > 0.6f && Chance(0.45);
            g.hornLength = g.headLength * F(0.5f, 1.4f);
            g.hornCurl = F(-60f, 80f);

            Palette(g, r, kind);

            // ---- 習性：大型的比較不怕人、成群；小型的膽小、常獨居
            float big = Mathf.InverseLerp(0.35f, 1.3f, g.size);
            g.temperament = r.NextDouble() < 0.25 + big * 0.4 ? Temperament.Passive : Temperament.Shy;
            double a = r.NextDouble();
            g.activity = a < 0.35 ? Activity.Diurnal : a < 0.6 ? Activity.Crepuscular : a < 0.85 ? Activity.Nocturnal : Activity.Any;
            g.terrain = kind == DSTWorldGen.RegionKind.Mesa ? (Chance(0.7) ? Terrain.Rocky : Terrain.Any)
                      : (Terrain)r.Next(0, 3);
            bool herd = Chance(0.2 + big * 0.5);
            g.group = herd ? new Vector2Int(2, r.Next(3, 7)) : new Vector2Int(1, Chance(0.3) ? 2 : 1);
            g.cohesion = herd ? F(0.5f, 0.9f) : F(0f, 0.3f);
            g.grazeChance = F(0.2f, 0.6f);
            g.maxAlive = herd ? Mathf.Max(g.group.y + 1, 5) : r.Next(2, 5);

            g.walkSpeed = (g.upperLeg + g.lowerLeg) * F(1.3f, 2.0f);
            g.runSpeed = Mathf.Clamp((g.upperLeg + g.lowerLeg) * F(9f, 14f), 2.2f, 5.5f);
            g.notice = Mathf.Lerp(7f, 12f, (float)r.NextDouble());

            float legTotal = g.upperLeg + g.lowerLeg;
            g.strideWalk = legTotal * F(0.9f, 1.2f);
            g.strideRun = legTotal * F(2.2f, 3.0f);
            g.stepHeight = legTotal * F(0.12f, 0.22f);
            g.bounds = g.size < 0.55f && Chance(0.6);

            MakeNames(g, r);
            return g;
        }

        // 沙漠動物多是保護色：沙、赭、灰褐；偶爾有比較顯眼的斑紋
        static void Palette(DSTGenome g, System.Random r, DSTWorldGen.RegionKind kind)
        {
            float F(float a, float b) => a + (float)r.NextDouble() * (b - a);
            float hue = F(0.03f, 0.12f);          // 橙褐到沙黃
            float sat = F(0.2f, 0.55f);
            float val = F(0.3f, 0.75f);
            if (kind == DSTWorldGen.RegionKind.DuneSea) { val = F(0.6f, 0.85f); sat *= 0.75f; }      // 淡沙色
            if (kind == DSTWorldGen.RegionKind.Mesa) { hue = F(0.0f, 0.05f); val = F(0.25f, 0.55f); sat = F(0.35f, 0.65f); }   // 紅赭
            double morph = r.NextDouble();
            if (morph < 0.22) { sat *= 0.18f; val = F(0.3f, 0.65f); }       // 灰色系
            else if (morph < 0.36) { val *= 0.45f; sat *= 0.6f; }           // 深褐、近黑
            g.coat = Color.HSVToRGB(hue, sat, val);
            // 肚子：有的幾乎白、有的跟身體差不多
            g.belly = Color.HSVToRGB(hue, sat * F(0.1f, 0.6f), Mathf.Min(0.95f, val + F(0.05f, 0.45f)));
            g.mark = Color.HSVToRGB(Mathf.Repeat(hue + F(-0.03f, 0.02f), 1f), Mathf.Min(1f, sat + 0.1f), val * F(0.15f, 0.4f));
            g.hoof = Color.HSVToRGB(hue, sat * 0.5f, val * 0.25f);
            double p = r.NextDouble();
            g.pattern = p < 0.35 ? Pattern.Plain : p < 0.6 ? Pattern.Stripes : p < 0.85 ? Pattern.Spots : Pattern.Saddle;
            g.patternScale = F(0.6f, 1.6f);
        }

        // ------------------------------------------------------------------ 名字
        static readonly string[] ColorWords = { "沙", "赭", "灰", "褐", "淡", "黃", "棕", "暗" };
        static readonly string[] SmallNouns = { "鼠", "鼬", "貂", "狐", "獴" };
        static readonly string[] MidNouns = { "狐", "犬", "貒", "貘", "獾" };
        static readonly string[] BigNouns = { "羚", "鹿", "駝", "獸", "麂" };
        static readonly string[] LatinA = { "Psam", "Xer", "Ere", "Dor", "Ammo", "Pyr", "Cal", "Thal", "Kor", "Sab" };
        static readonly string[] LatinB = { "ocyon", "ictis", "odon", "ophus", "otherium", "amys", "elaphus", "ogale" };
        static readonly string[] LatinC = { "arenarius", "deserti", "pallidus", "nocturnus", "rupestris", "fulvus", "velox", "gregarius" };

        static void MakeNames(DSTGenome g, System.Random r)
        {
            string color = g.coat.grayscale < 0.3f ? "暗" : ColorWords[r.Next(ColorWords.Length)];
            string mark = g.pattern switch { Pattern.Stripes => "紋", Pattern.Spots => "斑", Pattern.Saddle => "鞍", _ => "" };
            string trait = g.horns ? "角" : g.ears == EarShape.Long ? "長耳" : g.tailLength < g.bodyLength * 0.2f ? "短尾" : "";
            var nouns = g.size < 0.55f ? SmallNouns : g.size < 0.9f ? MidNouns : BigNouns;
            if (g.ears == EarShape.Long && g.size < 0.55f && r.NextDouble() < 0.5) nouns = new[] { "兔" };
            string noun = nouns[r.Next(nouns.Length)];
            g.name = color + mark + trait + noun;
            g.scientificName = LatinA[r.Next(LatinA.Length)] + LatinB[r.Next(LatinB.Length)] + " " + LatinC[r.Next(LatinC.Length)];
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
                s.maxHealth = Mathf.Lerp(3f, 16f, Mathf.InverseLerp(0.35f, 1.3f, size));
                s.walkSpeed = walkSpeed;
                s.runSpeed = runSpeed;
                s.turnSpeedDeg = Mathf.Lerp(420f, 220f, Mathf.InverseLerp(0.35f, 1.3f, size));
                s.acceleration = runSpeed * 3f;
                s.temperament = temperament;
                s.activity = activity;
                s.noticeRadius = notice;
                s.fleeRadius = notice * 0.5f;
                s.calmRadius = notice * 1.8f;
                s.sniffChance = grazeChance;
                s.wanderRadius = 6f + size * 8f;
                s.fleeDistance = 12f + size * 8f;
                s.herdCohesion = cohesion;
                s.maxAlive = maxAlive;
                s.groupSize = group;
                _species[key] = s;
                return s;
            }
        }
    }
}
