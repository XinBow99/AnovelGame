using System.Collections.Generic;
using DUST.World;
using UnityEngine;

namespace DUST.Animals
{
    // 發條遷徙：每一群動物有固定的窩和一條環狀路線。牠們在活動時段從窩出發、沿路線繞一圈、時段結束前回到窩。
    // 群在哪裡完全由（世界種子, 地區格, 第幾群, 累加時數）算出來：不用存、不用同步，每台電腦算的都一樣，
    // 每天同一個時間牠們也一定走到同一個地方（玩家記住了就找得到）。足跡（DSTTracks）也是用同一條路線往回推算的。
    public sealed class DSTBand
    {
        public long id;
        public int key;
        public DSTGenome genome;
        public Vector3 den;
        public int count;                 // 這群幾隻
        public float radius;              // 路線大概的半徑（找附近的群用）
        public Vector3 center;            // 路線中心

        // 路線折線（首尾相接）：第一次用到才算（要取樣地形高度，整格 60 群一次算完會卡一下）
        readonly Vector2[] _ctrl;
        Vector3[] _pts;
        float[] _cum;
        float _length;
        public float length { get { Build(); return _length; } }
        readonly (float start, float dur)[] _trips;   // 每天的出門時段（小時，可能超過 24）
        readonly float[] _lateral;        // 每隻的左右位置（m）
        const float Lag = 0.9f;           // 一隻跟著一隻的前後距離（m）
        const int Sub = 10;

        internal DSTBand(long id, int key, Vector3 den, Vector2[] ctrl, (float, float)[] trips, int count, float spread, int salt)
        {
            this.id = id; this.key = key; this.den = den; _trips = trips; this.count = count;
            genome = DSTGenome.Get(key);
            _ctrl = ctrl;
            var c = Vector2.zero;
            foreach (var p in ctrl) c += p;
            c /= ctrl.Length;
            center = new Vector3(c.x, den.y, c.y);
            foreach (var p in ctrl) radius = Mathf.Max(radius, (p - c).magnitude * 1.1f);   // 曲線會稍微鼓出去
            _lateral = new float[count];
            for (int m = 0; m < count; m++)
                _lateral[m] = m == 0 ? 0f : (DSTNoise.Hash01(salt, m, 0, 77) - 0.5f) * spread;
        }

        // Catmull-Rom 平滑成折線，貼地形
        void Build()
        {
            if (_pts != null) return;
            var gen = DSTWorld.Instance ? DSTWorld.Instance.Gen : null;
            int k = _ctrl.Length;
            _pts = new Vector3[k * Sub];
            for (int j = 0; j < k; j++)
            {
                Vector2 p0 = _ctrl[(j - 1 + k) % k], p1 = _ctrl[j], p2 = _ctrl[(j + 1) % k], p3 = _ctrl[(j + 2) % k];
                for (int q = 0; q < Sub; q++)
                {
                    float t = q / (float)Sub, t2 = t * t, t3 = t2 * t;
                    var v = 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
                    _pts[j * Sub + q] = new Vector3(v.x, gen != null ? gen.Height(v.x, v.y) : den.y, v.y);
                }
            }
            _cum = new float[_pts.Length + 1];
            for (int i = 0; i < _pts.Length; i++)
                _cum[i + 1] = _cum[i] + Flat(_pts[(i + 1) % _pts.Length] - _pts[i]).magnitude;
            _length = _cum[_pts.Length];
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        /// <summary>沿路線第 s 公尺的位置與前進方向</summary>
        public Vector3 PointAt(float s, out Vector3 dir)
        {
            Build();
            s = Mathf.Repeat(s, _length);
            int lo = 0, hi = _pts.Length - 1;
            while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (_cum[mid] <= s) lo = mid; else hi = mid - 1; }
            var a = _pts[lo]; var b = _pts[(lo + 1) % _pts.Length];
            float seg = _cum[lo + 1] - _cum[lo];
            float t = seg > 1e-4f ? (s - _cum[lo]) / seg : 0f;
            dir = Flat(b - a).normalized;
            return Vector3.Lerp(a, b, t);
        }

        /// <summary>某個時間點：在路線上的哪裡（s），沒出門就回傳 false（在窩裡）</summary>
        public bool RouteAt(double totalHours, out float s)
        {
            double day = System.Math.Floor(totalHours / 24.0);
            for (int d = -1; d <= 0; d++)
                foreach (var (start, dur) in _trips)
                {
                    double t0 = (day + d) * 24.0 + start;
                    double rel = totalHours - t0;
                    if (rel >= 0.0 && rel < dur) { s = (float)(rel / dur) * length; return true; }
                }
            s = 0f;
            return false;
        }

        /// <summary>第 m 隻現在該在的位置（窩裡就散在窩旁）</summary>
        public Vector3 MemberAt(double totalHours, int m, out bool moving)
        {
            moving = RouteAt(totalHours, out float s);
            m = Mathf.Clamp(m, 0, count - 1);
            if (!moving)
            {
                float a = m * 2.4f;
                return den + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (m == 0 ? 0f : 0.6f + 0.25f * m);
            }
            var p = PointAt(s - m * Lag, out var dir);
            return p + new Vector3(dir.z, 0f, -dir.x) * _lateral[m];
        }

        public float Lateral(int m) => _lateral[Mathf.Clamp(m, 0, count - 1)];
        public float LagOf(int m) => m * Lag;

        /// <summary>[from, to] 之間出門走過的路段（給足跡用）：回呼 (起點 s, 終點 s, 出發的時間, 每公尺幾小時)</summary>
        public void Walked(double from, double to, System.Action<float, float, double, double> each)
        {
            double day = System.Math.Floor(to / 24.0);
            for (int d = -1; d <= 0; d++)
                foreach (var (start, dur) in _trips)
                {
                    double t0 = (day + d) * 24.0 + start, t1 = t0 + dur;
                    double a = System.Math.Max(t0, from), b = System.Math.Min(t1, to);
                    if (b <= a) continue;
                    double hPerM = dur / length;
                    each((float)((a - t0) / hPerM), (float)((b - t0) / hPerM), t0, hPerM);
                }
        }

        /// <summary>下一次出門的鐘點（給 /herds 指令看）</summary>
        public float NextDeparture(double totalHours)
        {
            double best = double.MaxValue;
            double day = System.Math.Floor(totalHours / 24.0);
            for (int d = 0; d <= 1; d++)
                foreach (var (start, _) in _trips)
                {
                    double t0 = (day + d) * 24.0 + start;
                    if (t0 > totalHours && t0 < best) best = t0;
                }
            return (float)(best - System.Math.Floor(best / 24.0) * 24.0);
        }
    }

    public static class DSTMigration
    {
        const int DensPerCell = 60;       // 一個地區格（1.2 km）裡有幾群
        static readonly Dictionary<long, List<DSTBand>> _cells = new Dictionary<long, List<DSTBand>>();
        static int _seed = int.MinValue;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _cells.Clear(); _seed = int.MinValue; }

        /// <summary>只有隨機世界有遷徙（遺跡二沒有地形資料，照舊隨機生成）</summary>
        public static bool Available
        {
            get { var w = DSTWorld.Instance; return w && w.IsRandom && w.Gen != null; }
        }

        public static double Now => DSTTimeOfDay.Instance ? DSTTimeOfDay.Instance.TotalHours : 12.0;
        static float SecondsPerHour => DSTTimeOfDay.Instance ? DSTTimeOfDay.Instance.SecondsPerHour : 50f;

        /// <summary>中心在 p 附近 range 公尺內有經過的群</summary>
        public static void Near(Vector3 p, float range, List<DSTBand> into)
        {
            into.Clear();
            if (!Available) return;
            float cell = DSTWorldGen.RegionCell;
            int cx = Mathf.FloorToInt(p.x / cell), cz = Mathf.FloorToInt(p.z / cell);
            float reach = range + MaxRadius * 2.4f;      // 窩在隔壁格、路線伸過來的最遠距離（中心離窩 r、點再往外 1.3r）
            for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                // 隔壁格離得夠遠就不用算（平常只碰到 1~4 格）
                float nx = Mathf.Clamp(p.x, (cx + i) * cell, (cx + i + 1) * cell), nz = Mathf.Clamp(p.z, (cz + j) * cell, (cz + j + 1) * cell);
                if ((nx - p.x) * (nx - p.x) + (nz - p.z) * (nz - p.z) > reach * reach) continue;
                foreach (var b in Cell(cx + i, cz + j))
                {
                    float r = b.radius + range;
                    float dx = b.center.x - p.x, dz = b.center.z - p.z;
                    if (dx * dx + dz * dz < r * r) into.Add(b);
                }
            }
        }

        const float MaxRadius = 240f;

        static List<DSTBand> Cell(int cx, int cz)
        {
            var gen = DSTWorld.Instance.Gen;
            if (_seed != gen.Seed) { _cells.Clear(); _seed = gen.Seed; }
            long id = ((long)cx << 32) ^ (uint)cz;
            if (_cells.TryGetValue(id, out var list)) return list;
            list = new List<DSTBand>();
            for (int i = 0; i < DensPerCell; i++)
            {
                var b = Make(gen, cx, cz, i);
                if (b != null) list.Add(b);
            }
            _cells[id] = list;
            return list;
        }

        static DSTBand Make(DSTWorldGen gen, int cx, int cz, int i)
        {
            int seed = gen.Seed;
            float H(int salt) => DSTNoise.Hash01(seed, cx * 131 + i, cz, 5100 + salt);
            float cell = DSTWorldGen.RegionCell;
            float x = (cx + H(0)) * cell, z = (cz + H(1)) * cell;
            if (x * x + z * z < 70f * 70f) return null;                // 營地正中間不放窩
            var den = new Vector3(x, gen.Height(x, z), z);

            // 窩那個點的名冊裡，挑一種喜歡這裡地形的
            var roster = DSTEcology.At(den);
            if (roster.keys == null || roster.keys.Length == 0) return null;
            int start = (int)(DSTNoise.Hash(seed, cx * 131 + i, cz, 5102) % (uint)roster.keys.Length);
            DSTGenome g = null;
            for (int j = 0; j < roster.keys.Length && g == null; j++)
            {
                var c = DSTGenome.Get(roster.keys[(start + j) % roster.keys.Length]);
                if (DSTEcology.TerrainOk(c.terrain, den)) g = c;
            }
            if (g == null) return null;

            // 每天的出門時段：晚一點出門、早一點回窩，每群不一樣
            float dep = H(3) * 1.2f, ret = H(4) * 1.0f;
            (float, float)[] windows = g.activity switch
            {
                Activity.Diurnal => new[] { (6f, 18.5f) },
                Activity.Nocturnal => new[] { (19f, 29.5f) },
                Activity.Crepuscular => new[] { (4.5f, 9f), (16.5f, 21f) },
                _ => new[] { (0f, 6f), (6f, 12f), (12f, 18f), (18f, 24f) },
            };
            bool always = g.activity == Activity.Any;
            var trips = new (float, float)[windows.Length];
            float meanDur = 0f;
            for (int w = 0; w < windows.Length; w++)
            {
                var (a, b) = windows[w];
                float s = always ? a : a + dep, e = always ? b : b - ret;
                trips[w] = (s, e - s);
                meanDur += (e - s) / windows.Length;
            }

            // 路線長度 = 走路速度 × 出門時間（大隻、腿長的走比較遠）；停下來吃東西落後了會小跑追上
            float metersPerHour = g.walkSpeed * 1.1f * SecondsPerHour;
            float r = Mathf.Clamp(metersPerHour * meanDur / (2f * Mathf.PI * 1.1f), 14f, MaxRadius);

            // 環狀路線：窩在環上，其他點繞中心（半徑、角度都抖一點，看起來像順著地形走）
            float theta = H(5) * Mathf.PI * 2f;
            var ctr = new Vector2(x, z) + new Vector2(Mathf.Cos(theta), Mathf.Sin(theta)) * r;
            int k = 5 + (int)(H(6) * 3f);
            float sign = H(7) < 0.5f ? 1f : -1f;
            var ctrl = new Vector2[k];
            for (int j = 0; j < k; j++)
            {
                float ang = theta + Mathf.PI + sign * j * Mathf.PI * 2f / k;
                float rr = r;
                if (j > 0)
                {
                    ang += (H(10 + j) - 0.5f) * 0.5f * Mathf.PI * 2f / k;
                    rr *= Mathf.Lerp(0.7f, 1.3f, H(20 + j));
                }
                ctrl[j] = ctr + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * rr;
            }
            ctrl[0] = new Vector2(x, z);

            int salt = (int)DSTNoise.Hash(seed, cx * 131 + i, cz, 5103);
            int count = g.group.x + (int)(H(8) * (g.group.y - g.group.x + 1));
            count = Mathf.Clamp(count, 1, g.group.y);
            float spread = count > 1 ? Mathf.Lerp(3.5f, 1.2f, g.cohesion) : 0f;
            long id = ((long)cx * 73856093L) ^ ((long)cz * 19349663L) ^ i;
            return new DSTBand(id, g.key, den, ctrl, trips, count, spread, salt);
        }
    }
}
