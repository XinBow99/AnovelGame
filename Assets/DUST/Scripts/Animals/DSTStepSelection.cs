using DUST.World;
using UnityEngine;
using UnityEngine.AI;

namespace DUST.Animals
{
    // 步伐選擇函數（Step Selection Function, SSF）：動物怎麼決定下一步走去哪。
    // 每一步：
    //   1. 從移動分布抽 N 個候選終點（步長 ~ Gamma、轉角 ~ von Mises，峰值在目前面向）
    //   2. 每個候選算 w = exp(β·x)，x 是這一步的環境特徵（坡度、遮蔽、水源、玩家指數……）
    //   3. 依 w 的比例隨機抽一個走過去
    // 來源：Fortin et al. 2005（黃石麋鹿與狼）、Fieberg et al. 2021（iSSF）。研究整理見 docs/animal_ecology_research.md。
    // 注意：麋鹿的 β 是用 5 小時一步估的，這裡一步只有幾秒，β 會隨步長時間改變（scale dependent），
    // 所以下面的數字只當「起始的數量級」，之後要靠 /ssf 除錯畫面調。
    // 只在主機上跑（DSTAnimal 的 AI 本來就只在主機）。
    public static class DSTStepSelection
    {
        // ------------------------------------------------------------------ 特徵
        public enum Term
        {
            Slope,          // 路上有陡坡的比例                    麋鹿 S_slope        −2.189【來源】
            FoodEnd,        // 終點的草量（綠洲最多）               麋鹿 Aspen_end      +0.338【來源；白楊林 → 草地是對應】；餓的時候再加【建議】
            CoverEnd,       // 終點在遮蔽處（岩區、石塊、牆邊）     麋鹿 Forest_end     −0.289【來源；針葉林 → 岩石是對應】
            CoverProp,      // 路上經過遮蔽處的比例                 麋鹿 Forest_prop    −0.770【來源】
            FoodEndW3,      // 水源 × 玩家指數³                     麋鹿 Aspen_end×W³   −0.885【來源；狼 → 玩家是對應】
            CoverEndW3,     // 遮蔽 × 玩家指數³                     麋鹿 Forest_end×W³  +0.313【來源】
            W3,             // 玩家指數³（沿路平均）                麋鹿 W³             +0.240【來源】
            Away,           // 離眼前威脅變遠多少（÷ 平均步長）     【建議】逃跑用，強度跟害怕程度走
            Hidden,         // 終點被擋住、威脅看不到               【建議】「躲到岩石後面」
            Home,           // 超出群的位置多遠（÷ 範圍半徑）       【建議】遷徙的群（DSTBand）拉住個體
            Herd,           // 超出同伴中心多遠                     【建議】沒有遷徙群時，用同伴中心
            Habitat,        // 終點的岩石程度 × 物種偏好             【建議】DSTGenome.terrain：Rocky 喜歡、Open 討厭
            Prey,           // 離聞到的獵物變近多少（÷ 平均步長）   【建議】肉食動物餓了才有
            Count
        }

        public static readonly string[] TermNames = { "陡坡", "草", "遮蔽", "穿越遮蔽", "草×W³", "遮蔽×W³", "W³", "遠離", "躲藏", "回群", "靠同伴", "棲地", "獵物" };

        // 麋鹿的係數（Fortin 2005 Table 1）
        const float B_Slope = -2.189f, B_FoodEnd = 0.338f, B_CoverEnd = -0.289f, B_CoverProp = -0.770f;
        const float B_FoodEndW3 = -0.885f, B_CoverEndW3 = 0.313f, B_W3 = 0.240f;

        // ------------------------------------------------------------------ 這一步的設定（DSTAnimal 依狀態填）
        public struct Kernel
        {
            public int candidates;
            public float meanStep;        // Gamma 平均步長（公尺）
            public float shape;           // Gamma 形狀 k（≥1）；scale = meanStep / k
            public float kappa;           // von Mises 集中度：0 = 任何方向都一樣，越大越直走
            public Vector3 heading;
        }

        public struct Context
        {
            public Transform threat;      // 眼前的威脅（沒有 = null）
            public float fear;            // 0 = 平靜、1 = 逃跑、>1 = 被打過的恐慌
            public bool hasHome; public Vector3 home; public float homeRadius;
            public bool hasHerd; public Vector3 herd; public float herdRadius; public float cohesion;
            public float habitat;         // +1 喜歡岩區、−1 討厭、0 不在乎
            public float hunger;          // 0 = 飽、1 = 餓扁
            public bool grazer;           // 草食：會被草吸引
            public bool hasPrey; public Vector3 prey;
            public Transform self;
        }

        // 最近一次的決定（給 /ssf 除錯畫面畫）
        public sealed class Decision
        {
            public const int Max = 32;
            public Vector3 origin;
            public readonly Vector3[] ends = new Vector3[Max];
            public readonly float[] weight = new float[Max];   // 已正規化，總和 = 1
            public readonly float[,] x = new float[Max, (int)Term.Count];
            public readonly float[] beta = new float[(int)Term.Count];
            public int count, chosen = -1;
            public float time;
        }

        // ------------------------------------------------------------------ 主流程
        /// <summary>抽候選、算權重、抽一個。回傳 false = 這次沒有可走的候選</summary>
        public static bool Choose(Vector3 from, in Kernel k, in Context c, Decision d, out Vector3 end)
        {
            end = from;
            int n = Mathf.Min(k.candidates, Decision.Max);
            Vector3 fwd = k.heading; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            // β 跟著當下的狀態：越害怕，「遠離」「躲藏」越重要，「回群」越不重要
            var b = d.beta;
            b[(int)Term.Slope] = B_Slope;
            b[(int)Term.FoodEnd] = c.grazer ? B_FoodEnd + 1.5f * c.hunger : 0f;
            b[(int)Term.CoverEnd] = B_CoverEnd;
            b[(int)Term.CoverProp] = B_CoverProp;
            b[(int)Term.FoodEndW3] = c.grazer ? B_FoodEndW3 : 0f;
            b[(int)Term.CoverEndW3] = B_CoverEndW3;
            b[(int)Term.W3] = B_W3;
            b[(int)Term.Away] = 3.0f * c.fear;
            b[(int)Term.Hidden] = 1.5f * c.fear;
            b[(int)Term.Home] = c.fear > 0f ? -0.6f : -2.0f;
            b[(int)Term.Herd] = -2.0f * c.cohesion * (c.fear > 0f ? 0.3f : 1f);
            b[(int)Term.Habitat] = 0.6f * c.habitat;
            b[(int)Term.Prey] = 2f * c.hunger;

            float threatStart = c.threat ? Flat(from, c.threat.position) : 0f;
            float scale = k.meanStep / Mathf.Max(1f, k.shape);
            float best = float.MinValue;
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                float len = Gamma(k.shape) * scale;
                float turn = VonMises(k.kappa);
                Vector3 dir = Quaternion.AngleAxis(turn * Mathf.Rad2Deg, Vector3.up) * fwd;
                Vector3 target = from + dir * len;
                // 撞到導航網格邊緣（懸崖、牆、水）就停在邊上：死巷的候選自然走不遠
                if (NavMesh.Raycast(from, target, out var hit, NavMesh.AllAreas)) target = hit.position;
                else if (NavMesh.SamplePosition(target, out var s, 1.5f, NavMesh.AllAreas)) target = s.position;
                else continue;
                if (Flat(from, target) < 0.3f) continue;

                Features(from, target, threatStart, k.meanStep, c, d.x, m);
                float u = 0f;
                for (int t = 0; t < (int)Term.Count; t++) u += b[t] * d.x[m, t];
                d.ends[m] = target;
                d.weight[m] = u;
                if (u > best) best = u;
                m++;
            }
            d.origin = from;
            d.count = m;
            d.time = Time.time;
            d.chosen = -1;
            if (m == 0) return false;

            // exp 之前先減掉最大值，數字才不會爆掉
            float sum = 0f;
            for (int i = 0; i < m; i++) sum += d.weight[i] = Mathf.Exp(d.weight[i] - best);
            float r = Random.value * sum;
            for (int i = 0; i < m; i++)
            {
                d.weight[i] /= sum;
                if (d.chosen < 0 && (r -= d.weight[i] * sum) <= 0f) d.chosen = i;
            }
            if (d.chosen < 0) d.chosen = m - 1;
            end = d.ends[d.chosen];
            return true;
        }

        // ------------------------------------------------------------------ 環境特徵
        const int PathSamples = 3;              // 沿路取幾個點（1/3、2/3、終點）
        const float SteepDeg = 25f;              // 超過這個角度算陡坡【建議；麋鹿是 10 m 內升 20 m，約 63°，沙丘落沙坡約 30°】

        static void Features(Vector3 from, Vector3 to, float threatStart, float meanStep, in Context c, float[,] x, int i)
        {
            for (int t = 0; t < (int)Term.Count; t++) x[i, t] = 0f;

            float steep = 0f, coverPath = 0f, wPath = 0f;
            for (int s = 1; s <= PathSamples; s++)
            {
                Vector3 p = Vector3.Lerp(from, to, s / (float)PathSamples);
                if (Vector3.Angle(GroundNormal(p), Vector3.up) > SteepDeg) steep += 1f;
                coverPath += Cover(p, c.self);
                wPath += Cube(DSTPlayerActivity.W(p));
            }
            steep /= PathSamples; coverPath /= PathSamples; wPath /= PathSamples;

            float food = Food(to);
            float cover = Cover(to, c.self);
            float w3 = Cube(DSTPlayerActivity.W(to));

            x[i, (int)Term.Slope] = steep;
            x[i, (int)Term.FoodEnd] = food;
            x[i, (int)Term.CoverEnd] = cover;
            x[i, (int)Term.CoverProp] = coverPath;
            x[i, (int)Term.FoodEndW3] = food * w3;
            x[i, (int)Term.CoverEndW3] = cover * w3;
            x[i, (int)Term.W3] = wPath;

            if (c.threat)
            {
                x[i, (int)Term.Away] = (Flat(to, c.threat.position) - threatStart) / Mathf.Max(1f, meanStep);
                if (c.fear > 0f) x[i, (int)Term.Hidden] = Hidden(c.threat, to, c.self) ? 1f : 0f;
            }
            if (c.hasHome) x[i, (int)Term.Home] = Mathf.Max(0f, Flat(to, c.home) - c.homeRadius) / Mathf.Max(1f, c.homeRadius);
            if (c.hasHerd) x[i, (int)Term.Herd] = Mathf.Max(0f, Flat(to, c.herd) - c.herdRadius) / Mathf.Max(1f, c.herdRadius);
            x[i, (int)Term.Habitat] = Rock(to);
            if (c.hasPrey) x[i, (int)Term.Prey] = (Flat(from, c.prey) - Flat(to, c.prey)) / Mathf.Max(1f, meanStep);
        }

        static DSTWorldGen Gen => DSTWorld.Instance ? DSTWorld.Instance.Gen : null;

        // 地面法線：隨機世界用地形函數（不用等網格碰撞），遺跡二之類的固定地圖往下打射線
        static Vector3 GroundNormal(Vector3 p)
        {
            var g = Gen;
            if (g != null) return g.Normal(p.x, p.z, 1f);
            return Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out var h, 6f, ~0, QueryTriggerInteraction.Ignore) ? h.normal : Vector3.up;
        }

        // 台地岩區 0..1
        static float Rock(Vector3 p)
        {
            var g = Gen;
            if (g == null) return 0f;
            g.Sample(p.x, p.z, out float rock);
            return rock;
        }

        // 草量：綠洲最多，被吃掉的格子要慢慢長回來（DSTMetabolism）
        static float Food(Vector3 p) => DSTMetabolism.Forage(p);

        // 遮蔽：岩區，或 1.5 m 內有石塊 / 牆 / 廢車（不算地面、動物、玩家、會滾的東西）
        static readonly Collider[] _hits = new Collider[16];
        static float Cover(Vector3 p, Transform self)
        {
            float rock = Rock(p);
            if (rock > 0.7f) return 1f;
            int n = Physics.OverlapSphereNonAlloc(p + Vector3.up * 0.7f, 1.5f, _hits, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (IsObstacle(_hits[i], self)) return 1f;
            return rock;
        }

        static bool IsObstacle(Collider col, Transform self)
        {
            if (!col || (self && col.transform.IsChildOf(self))) return false;
            if (col.GetComponent<DSTWorldChunk>()) return false;                       // 地面
            var rb = col.attachedRigidbody;
            if (rb && !rb.isKinematic) return false;                                   // 車、會被撞飛的箱子
            if (col is CharacterController) return false;                              // 玩家
            if (col.GetComponentInParent<DSTAnimal>()) return false;
            return col.bounds.extents.y > 0.25f;                                       // 小石子擋不住
        }

        // 從威脅的眼睛看過去，終點是不是被擋住
        static bool Hidden(Transform threat, Vector3 p, Transform self)
        {
            Vector3 eye = threat.position + Vector3.up * 1.0f;
            Vector3 body = p + Vector3.up * 0.4f;
            if (!Physics.Linecast(eye, body, out var hit, ~0, QueryTriggerInteraction.Ignore)) return false;
            if (hit.transform == threat || hit.transform.IsChildOf(threat)) return false;
            return !(self && hit.transform.IsChildOf(self));
        }

        static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }
        static float Cube(float v) => v * v * v;

        // ------------------------------------------------------------------ 分布抽樣
        // Gamma(k, 1)，k ≥ 1：Marsaglia & Tsang 2000
        static float Gamma(float k)
        {
            k = Mathf.Max(1f, k);
            float d = k - 1f / 3f, c = 1f / Mathf.Sqrt(9f * d);
            for (int guard = 0; guard < 32; guard++)
            {
                float z = Normal(), v = 1f + c * z;
                if (v <= 0f) continue;
                v = v * v * v;
                float u = Mathf.Max(1e-7f, Random.value);
                if (Mathf.Log(u) < 0.5f * z * z + d - d * v + d * Mathf.Log(v)) return d * v;
            }
            return k;
        }

        static float Normal()
        {
            float u1 = Mathf.Max(1e-7f, Random.value), u2 = Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }

        // von Mises(0, κ)，回傳弧度 −π..π：Best & Fisher 1979
        static float VonMises(float kappa)
        {
            if (kappa < 1e-3f) return Random.Range(-Mathf.PI, Mathf.PI);
            float a = 1f + Mathf.Sqrt(1f + 4f * kappa * kappa);
            float bb = (a - Mathf.Sqrt(2f * a)) / (2f * kappa);
            float r = (1f + bb * bb) / (2f * bb);
            for (int guard = 0; guard < 64; guard++)
            {
                float z = Mathf.Cos(Mathf.PI * Random.value);
                float f = (1f + r * z) / (r + z);
                float c = kappa * (r - f);
                float u2 = Random.value;
                if (c * (2f - c) - u2 > 0f || Mathf.Log(c / Mathf.Max(1e-7f, u2)) + 1f - c >= 0f)
                    return (Random.value > 0.5f ? 1f : -1f) * Mathf.Acos(Mathf.Clamp(f, -1f, 1f));
            }
            return 0f;
        }
    }
}
