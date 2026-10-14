using System.Collections.Generic;
using DUST.World;
using UnityEngine;

namespace DUST.Animals
{
    // 發條遷徙：每一群動物有固定的窩和一條環狀路線。牠們在活動時段從窩出發、沿路線繞一圈、時段結束前回到窩。
    // 群在哪裡完全由（世界種子, 地區格, 第幾群, 累加時數）算出來：不用存、不用同步，每台電腦算的都一樣，
    // 每天同一個時間牠們也一定走到同一個地方（玩家記住了就找得到）。足跡（DSTTracks）也是用同一條路線往回推算的。
    // 發條沙漠（DSTClockwork）：集體遷徙那天改走「窩 → 聚集點 → 窩」；沙暴時躲在窩裡。
    public sealed class DSTBand
    {
        public long id;
        public int key;
        public DSTGenome genome;
        public Vector3 den;
        public int count;                 // 這群幾隻
        public float radius;              // 路線（含集體遷徙走得到的範圍）大概的半徑（找附近的群用）
        public Vector3 center;            // 路線中心
        internal int cellX, cellZ;
        internal Vector2 gatherNear;      // 這個物種在這格的窩的中心（集體遷徙的聚集點在附近）
        internal int gatherCount;         // 這格有幾群同物種（只有自己一群就不算「集體」）

        /// <summary>一條路線（折線、首尾相接）：第一次用到才取樣地形高度</summary>
        public sealed class Path
        {
            readonly Vector2[] _ctrl;
            readonly float _y;
            Vector3[] _pts;
            float[] _cum;
            float _length, _turn;
            readonly int _turnIndex;
            const int Sub = 10;

            internal Path(Vector2[] ctrl, float y, int turnIndex = -1) { _ctrl = ctrl; _y = y; _turnIndex = turnIndex; }

            public float Length { get { Build(); return _length; } }
            /// <summary>集體遷徙的路線：走到聚集點時的 s</summary>
            public float Turn { get { Build(); return _turn; } }

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
                        _pts[j * Sub + q] = new Vector3(v.x, gen != null ? gen.Height(v.x, v.y) : _y, v.y);
                    }
                }
                _cum = new float[_pts.Length + 1];
                for (int i = 0; i < _pts.Length; i++)
                    _cum[i + 1] = _cum[i] + Flat(_pts[(i + 1) % _pts.Length] - _pts[i]).magnitude;
                _length = _cum[_pts.Length];
                _turn = _turnIndex >= 0 ? _cum[_turnIndex * Sub] : _length;
            }

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
        }

        readonly Path _loop;
        readonly Dictionary<int, Path> _mass = new Dictionary<int, Path>();
        readonly float _loopGuess;        // 還沒取樣地形前估的路線長度
        public float length => _loop.Length;
        public Path Loop => _loop;
        readonly (float start, float dur)[] _trips;   // 每天的出門時段（小時，可能超過 24）
        readonly float[] _lateral;        // 每隻的左右位置（m）
        const float Lag = 0.9f;           // 一隻跟著一隻的前後距離（m）
        const float OutFrac = 0.4f, StayFrac = 0.2f;  // 集體遷徙：去程 40%、在聚集點待 20%、回程 40%

        internal DSTBand(long id, int key, Vector3 den, Vector2[] ctrl, (float, float)[] trips, int count, float spread, int salt)
        {
            this.id = id; this.key = key; this.den = den; _trips = trips; this.count = count;
            genome = DSTGenome.Get(key);
            _loop = new Path(ctrl, den.y);
            var c = Vector2.zero;
            foreach (var p in ctrl) c += p;
            c /= ctrl.Length;
            center = new Vector3(c.x, den.y, c.y);
            for (int i = 0; i < ctrl.Length; i++)
            {
                radius = Mathf.Max(radius, (ctrl[i] - c).magnitude * 1.1f);   // 曲線會稍微鼓出去
                _loopGuess += (ctrl[(i + 1) % ctrl.Length] - ctrl[i]).magnitude * 1.05f;
            }
            // 集體遷徙最遠走到離窩「半條路線」的地方
            radius = Mathf.Max(radius, (new Vector2(den.x, den.z) - c).magnitude + MaxOut);
            _lateral = new float[count];
            for (int m = 0; m < count; m++)
                _lateral[m] = m == 0 ? 0f : (DSTNoise.Hash01(salt, m, 0, 77) - 0.5f) * spread;
        }

        float MaxOut => _loopGuess * 0.7f;    // 集體遷徙那天走得比較趕：來回最多是平常路線的 1.4 倍

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        /// <summary>這天要不要集體遷徙（聚集點太遠就照常繞圈）：要的話回傳那天的路線</summary>
        public Path MassPath(int day)
        {
            if (_mass.TryGetValue(day, out var path)) return path;
            path = null;
            if (gatherCount >= 2 && DSTClockwork.MassDay(key, day))
            {
                var g = DSTClockwork.Gathering(key, day, cellX, cellZ, gatherNear, 40f);
                var d = new Vector2(den.x, den.z);
                var to = g - d;
                float dist = to.magnitude;
                if (dist > 6f && dist < MaxOut)
                {
                    // 去程、回程稍微錯開（腳印才不會疊在一起），在聚集點繞一小圈
                    var side = new Vector2(-to.y, to.x) / dist;
                    float w = Mathf.Min(8f, dist * 0.15f);
                    var ctrl = new[] { d, d + to * 0.5f + side * w, g, d + to * 0.5f - side * w };
                    path = new Path(ctrl, den.y, 2);
                }
            }
            _mass[day] = path;
            if (_mass.Count > 8) _mass.Clear();
            return path;
        }

        public bool IsMassDay(int day) => MassPath(day) != null;

        /// <summary>某個時間點在哪條路線的哪裡（s）；沒出門（或躲沙暴）就回傳 false（在窩裡）</summary>
        public bool RouteAt(double totalHours, out Path path, out float s)
        {
            path = _loop; s = 0f;
            var here = DSTClockwork.Flat(center);          // 沙暴、異象都是區域性的：看這群動物的地盤有沒有被罩住
            if (DSTClockwork.StormAt(totalHours, here) > 0.5f || DSTAnomaly.Shelter(totalHours, here)) return false;
            double day = System.Math.Floor(totalHours / 24.0);
            for (int d = -1; d <= 0; d++)
                foreach (var (start, dur) in _trips)
                {
                    double t0 = (day + d) * 24.0 + start;
                    double rel = totalHours - t0;
                    if (rel < 0.0 || rel >= dur) continue;
                    float f = (float)(rel / dur);
                    var mass = MassPath((int)(day + d));
                    if (mass == null) { s = f * _loop.Length; return true; }
                    path = mass;
                    s = f < OutFrac ? f / OutFrac * mass.Turn
                      : f < OutFrac + StayFrac ? mass.Turn
                      : mass.Turn + (f - OutFrac - StayFrac) / (1f - OutFrac - StayFrac) * (mass.Length - mass.Turn);
                    return true;
                }
            return false;
        }

        public bool RouteAt(double totalHours, out float s) => RouteAt(totalHours, out _, out s);

        /// <summary>第 m 隻現在該在的位置（窩裡就散在窩旁）</summary>
        public Vector3 MemberAt(double totalHours, int m, out bool moving)
        {
            moving = RouteAt(totalHours, out var path, out float s);
            m = Mathf.Clamp(m, 0, count - 1);
            if (!moving)
            {
                float a = m * 2.4f;
                return den + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (m == 0 ? 0f : 0.6f + 0.25f * m);
            }
            if (path != _loop && s == path.Turn)
            {
                // 在聚集點：各群在周圍佔一個方位，散開一點（不然幾群全擠在同一個點上）
                var g = path.PointAt(s, out _);
                float ang = DSTNoise.Hash01((int)id, 0, 0, 7403) * Mathf.PI * 2f + m * 0.7f;
                float rad = 4f + DSTNoise.Hash01((int)id, m, 0, 7404) * 5f;
                return g + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * rad;
            }
            var p = path.PointAt(s - m * Lag, out var dir);
            return p + new Vector3(dir.z, 0f, -dir.x) * _lateral[m];
        }

        public Vector3 PointAt(float s, out Vector3 dir) => _loop.PointAt(s, out dir);
        public float Lateral(int m) => _lateral[Mathf.Clamp(m, 0, count - 1)];
        public float LagOf(int m) => m * Lag;

        /// <summary>[from, to] 之間走過的路段（給足跡用）：回呼 (路線, 起點 s, 終點 s, 時間基準, 每公尺幾小時)；走到 s 的時間 = 時間基準 + s × 每公尺幾小時</summary>
        public void Walked(double from, double to, System.Action<Path, float, float, double, double> each)
        {
            double day = System.Math.Floor(to / 24.0);
            for (int d = -1; d <= 0; d++)
                foreach (var (start, dur) in _trips)
                {
                    double t0 = (day + d) * 24.0 + start;
                    var mass = MassPath((int)(day + d));
                    if (mass == null) Segment(_loop, 0f, _loop.Length, t0, dur);
                    else
                    {
                        Segment(mass, 0f, mass.Turn, t0, dur * OutFrac);
                        Segment(mass, mass.Turn, mass.Length, t0 + dur * (OutFrac + StayFrac), dur * (1f - OutFrac - StayFrac));
                    }
                }

            void Segment(Path path, float sa, float sb, double tStart, double dur)
            {
                if (sb - sa < 0.01f) return;
                double a = System.Math.Max(tStart, from), b = System.Math.Min(tStart + dur, to);
                if (b <= a) return;
                double hPerM = dur / (sb - sa);
                double tBase = tStart - sa * hPerM;
                each(path, (float)((a - tBase) / hPerM), (float)((b - tBase) / hPerM), tBase, hPerM);
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

        /// <summary>窩在這個地區格裡的所有群（遠處族群的起始數量用）</summary>
        public static IReadOnlyList<DSTBand> BandsIn(int rx, int rz) => Available ? Cell(rx, rz) : (IReadOnlyList<DSTBand>)System.Array.Empty<DSTBand>();

        const float MaxRadius = 240f;
        const float GatherCell = 600f;

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
                if (b != null)
                {
                    b.cellX = Mathf.FloorToInt(b.den.x / GatherCell); b.cellZ = Mathf.FloorToInt(b.den.z / GatherCell);
                    list.Add(b);
                }
            }
            // 集體遷徙的聚集點：同一個物種、附近（600 m 一小格）各窩的中心（各群才走得到）
            var sums = new Dictionary<(int, int, int), (Vector2 sum, int n)>();
            foreach (var b in list)
            {
                var k = (b.key, b.cellX, b.cellZ);
                sums.TryGetValue(k, out var s);
                sums[k] = (s.sum + new Vector2(b.den.x, b.den.z), s.n + 1);
            }
            foreach (var b in list) { var s = sums[(b.key, b.cellX, b.cellZ)]; b.gatherNear = s.sum / s.n; b.gatherCount = s.n; }
            _cells[id] = list;
            return list;
        }

        const float PredatorDenShare = 0.3f;

        static DSTBand Make(DSTWorldGen gen, int cx, int cz, int i)
        {
            int seed = gen.Seed;
            float H(int salt) => DSTNoise.Hash01(seed, cx * 131 + i, cz, 5100 + salt);
            float cell = DSTWorldGen.RegionCell;
            float x = (cx + H(0)) * cell, z = (cz + H(1)) * cell;
            if (x * x + z * z < 70f * 70f) return null;                // 營地正中間不放窩
            var den = new Vector3(x, gen.Height(x, z), z);

            // 窩那個點的名冊裡，挑一種喜歡這裡地形的
            // 肉食動物遠比獵物少（Carbone & Gittleman 2002：每 10,000 kg 獵物只養得起約 90 kg 的某種肉食動物），
            // 所以挑到肉食的窩，大部分改挑下一種【比例 0.3 是建議】
            var roster = DSTEcology.At(den);
            if (roster.keys == null || roster.keys.Length == 0) return null;
            int start = (int)(DSTNoise.Hash(seed, cx * 131 + i, cz, 5102) % (uint)roster.keys.Length);
            DSTGenome g = null;
            for (int j = 0; j < roster.keys.Length && g == null; j++)
            {
                var c = DSTGenome.Get(roster.keys[(start + j) % roster.keys.Length]);
                if (c.Carnivore && H(60 + j) > PredatorDenShare) continue;
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
