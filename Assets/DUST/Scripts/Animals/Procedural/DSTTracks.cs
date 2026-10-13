using System.Collections.Generic;
using DUST.Player;
using DUST.World;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.Animals
{
    // 足跡：玩家附近的地上，畫出動物群「過去幾小時」走過的腳印（用遷徙路線往回推算，不用存）。
    // 越舊越淡、沙海的風吹得快；活的動物走路也會留下腳印。每台電腦自己算、自己畫。
    // 用 HDRP Decal 投影在地面上（貼圖：4 格的腳印圖集，見 DSTProcAnimalSetup.BuildFootprints）。
    public class DSTTracks : MonoBehaviour
    {
        public Material material;          // M_DST_Footprint（HDRP/Decal）
        public int maxPrints = 400;        // HDRP 一個畫面最多 512 個 Decal，留一些給別的
        public float viewRange = 26f;      // 多遠以內畫
        public float lifeHours = 5f;       // 遊戲時數：多久之後被風吹平
        public float refresh = 0.3f;

        public static DSTTracks Instance { get; private set; }

        struct Print { public Vector3 pos; public Vector3 dir; public float size; public int cell; public float fade; public float d2; }

        readonly List<DecalProjector> _pool = new List<DecalProjector>();
        readonly List<Print> _prints = new List<Print>();
        readonly List<DSTBand> _bands = new List<DSTBand>();
        readonly List<(Print p, double t)> _live = new List<(Print, double)>();
        int _liveHead;
        const int MaxLive = 200;
        float _next;
        Transform _root;

        void OnEnable() => Instance = this;
        void OnDisable() { if (Instance == this) Instance = null; }

        // ------------------------------------------------------------------ 腳印形狀（圖集格）：0 肉墊、1 偶蹄、2 圓石掌、3 爪
        public static int CellOf(DSTGenome g) => g.body switch
        {
            "Ungulate" or "Humped" => 1,
            "Stoneback" => 2,
            "Burrower" => 3,
            _ => 0,
        };

        public static float SizeOf(DSTGenome g) => Mathf.Clamp(0.1f * g.size * (g.body == "Stoneback" || g.body == "Humped" ? 1.35f : 1f), 0.05f, 0.26f);
        public static float StrideOf(DSTGenome g) => Mathf.Max(0.12f, g.hipHeight * 1.05f);
        static float GaitWidth(DSTGenome g) => Mathf.Max(0.025f, g.bodyRadius * 1.1f);

        /// <summary>活的動物踩一步（任何電腦都可以呼叫，只畫在自己這台）</summary>
        public static void Step(Vector3 pos, Vector3 dir, DSTGenome g, bool left)
        {
            if (!Instance) return;
            var right = new Vector3(dir.z, 0f, -dir.x);
            var p = new Print { pos = pos + right * (left ? -1f : 1f) * GaitWidth(g), dir = dir, size = SizeOf(g), cell = CellOf(g) };
            var item = (p, DSTMigration.Now);
            if (Instance._live.Count < MaxLive) Instance._live.Add(item);
            else { Instance._live[Instance._liveHead] = item; Instance._liveHead = (Instance._liveHead + 1) % MaxLive; }
        }

        void Update()
        {
            if (!material || Time.time < _next) return;
            _next = Time.time + refresh;
            var viewer = Viewer();
            if (viewer == null) { Show(0); return; }
            Vector3 v = viewer.Value;
            double now = DSTMigration.Now;
            float r2 = viewRange * viewRange;
            _prints.Clear();

            // ---- 遷徙路線往回推算
            DSTMigration.Near(v, viewRange, _bands);
            var gen = DSTWorld.Instance && DSTWorld.Instance.Gen != null ? DSTWorld.Instance.Gen : null;
            foreach (var b in _bands)
            {
                var g = b.genome;
                var kind = (DSTWorldGen.RegionKind)((uint)g.key % 7u);
                float life = kind == DSTWorldGen.RegionKind.DuneSea ? lifeHours * 0.5f : lifeHours;
                float stride = StrideOf(g), size = SizeOf(g), gait = GaitWidth(g);
                int cell = CellOf(g);
                int members = Mathf.Min(b.count, 4);
                b.Walked(now - life, now, (s0, s1, t0, hPerM) =>
                {
                    for (int m = 0; m < members; m++)
                    {
                        float lag = b.LagOf(m), lat = b.Lateral(m);
                        // 第 m 隻在 s 的時候，走到路線上的 s - lag；腳印釘在固定的格子上（k × 步幅），才不會隨時間滑動
                        int k0 = Mathf.CeilToInt((s0 - lag) / stride), k1 = Mathf.FloorToInt((s1 - lag) / stride);
                        if (k1 - k0 > 4000) k0 = k1 - 4000;
                        for (int k = k0; k <= k1; k++)
                        {
                            float s = k * stride;
                            var c = b.PointAt(s, out var dir);
                            var right = new Vector3(dir.z, 0f, -dir.x);
                            // 每個腳印一點點不一樣（角度、大小、位置），用固定的雜湊，畫面才不會閃
                            int salt = (int)b.id * 31 + m;
                            float j0 = DSTNoise.Hash01(salt, k, 0, 91) - 0.5f, j1 = DSTNoise.Hash01(salt, k, 1, 91) - 0.5f;
                            var pos = c + right * (lat + ((k & 1) == 0 ? -gait : gait) + j1 * gait * 0.6f) + dir * j0 * stride * 0.15f;
                            dir = Quaternion.Euler(0f, j0 * 24f, 0f) * dir;
                            float dx = pos.x - v.x, dz = pos.z - v.z, d2 = dx * dx + dz * dz;
                            if (d2 > r2) continue;
                            double age = now - (t0 + (s + lag) * hPerM);
                            if (age < 0.0) continue;
                            float fade = 1f - (float)(age / life);
                            if (fade <= 0f) continue;
                            if (gen != null) pos.y = gen.Height(pos.x, pos.z);
                            _prints.Add(new Print { pos = pos, dir = dir, size = size * (1f + j1 * 0.18f), cell = cell, fade = fade, d2 = d2 });
                        }
                    }
                });
            }

            // ---- 活的動物剛踩的
            foreach (var (p, t) in _live)
            {
                float dx = p.pos.x - v.x, dz = p.pos.z - v.z, d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;
                float fade = 1f - (float)((now - t) / lifeHours);
                if (fade <= 0f) continue;
                var q = p; q.fade = fade; q.d2 = d2;
                _prints.Add(q);
            }

            if (_prints.Count > maxPrints) _prints.Sort((a, b) => a.d2.CompareTo(b.d2));
            int n = Mathf.Min(_prints.Count, maxPrints);
            Show(n);
        }

        static Vector3? Viewer()
        {
            var me = DSTNetPlayer.Local;
            if (me) return me.transform.position;
            var cam = Camera.main;
            return cam ? cam.transform.position : (Vector3?)null;
        }

        void Show(int n)
        {
            if (!_root)
            {
                _root = new GameObject("Tracks").transform;
                _root.SetParent(transform, false);
            }
            while (_pool.Count < n)
            {
                var go = new GameObject("Print");
                go.transform.SetParent(_root, false);
                var d = go.AddComponent<DecalProjector>();
                d.material = material;
                d.uvScale = new Vector2(0.25f, 1f);
                d.drawDistance = viewRange + 10f;
                d.fadeScale = 0.9f;
                _pool.Add(d);
            }
            for (int i = 0; i < _pool.Count; i++)
            {
                var d = _pool[i];
                if (i >= n) { if (d.enabled) d.enabled = false; continue; }
                var p = _prints[i];
                if (!d.enabled) d.enabled = true;
                // 從上往下投影：Decal 的 +Y（貼圖的上方）朝前進方向
                var dir = p.dir.sqrMagnitude > 1e-4f ? p.dir : Vector3.forward;
                d.transform.SetPositionAndRotation(p.pos + Vector3.up * 0.15f, Quaternion.LookRotation(Vector3.down, dir));
                d.size = new Vector3(p.size, p.size * 1.25f, 0.45f);
                d.pivot = new Vector3(0f, 0f, 0.225f);
                d.uvBias = new Vector2(p.cell * 0.25f, 0f);
                d.fadeFactor = Mathf.SmoothStep(0f, 1f, p.fade) * 0.9f;
            }
        }
    }
}
