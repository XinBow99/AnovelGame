using System.Collections.Generic;
using System.Threading.Tasks;
using DUST.Player;
using DUST.Vehicles;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.World
{
    // 地圖：右上角小地圖，按 M 展開成全螢幕地圖（滾輪縮放、拖曳移動）。
    // 隨機世界用地形公式直接畫（DSTMapTiles），遺跡二進場時從上往下打射線掃一張。
    // 標示：自己（箭頭＝面向）、其他玩家、載具、聚落名稱、出生點。
    public class DSTMap : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }
        public static int ClosedFrame { get; private set; } = -1;   // 同一幀的 Esc 不要再打開暫停選單

        const int MaxJobs = 2;
        const int MaxCachedTiles = 160;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            IsOpen = false;
            var go = new GameObject("DSTMap");
            DontDestroyOnLoad(go);
            go.AddComponent<DSTMap>();
        }

        // ------------------------------------------------------------------ 圖塊快取
        struct TileKey
        {
            public int level, x, z;
            public override int GetHashCode() => (level * 73856093) ^ (x * 19349663) ^ (z * 83492791);
            public override bool Equals(object o) => o is TileKey k && k.level == level && k.x == x && k.z == z;
        }

        class Tile
        {
            public Texture2D tex;
            public Task<Color32[]> job;
            public float lastUsed;
        }

        readonly Dictionary<TileKey, Tile> _tiles = new Dictionary<TileKey, Tile>();
        readonly List<TileKey> _tmp = new List<TileKey>();
        DSTWorldGen _gen;
        int _running;

        // 遺跡二：整張一次掃好
        Texture2D _dr2Tex;
        Rect _dr2Area;

        // 全螢幕地圖的視角
        Vector2 _center;
        float _mpp = 2f;            // 螢幕上每像素幾公尺
        bool _follow = true;

        Texture2D _arrow, _dot, _square, _white;
        GUIStyle _label, _small, _title, _button;

        // ------------------------------------------------------------------ 輸入
        void Update()
        {
            var world = DSTWorld.Instance;
            bool inGame = DSTNetPlayer.Local != null;
            if (!inGame)
            {
                if (IsOpen) Close();
                ResetSource();
                return;
            }

            // 世界換了（換種子、換地圖）→ 圖塊重來
            var gen = world && world.IsRandom ? world.Gen : null;
            if (gen != _gen) { ResetSource(); _gen = gen; }

            if (MPressed()) { if (IsOpen) Close(); else Open(); }
            else if (IsOpen && EscPressed()) Close();

            FinishJobs();
        }

        void Open()
        {
            IsOpen = true;
            _follow = true;
            // 遺跡二只有 120 m 見方，放大一點才看得清楚
            var world = DSTWorld.Instance;
            _mpp = world && world.IsRandom ? 2f : 0.12f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void Close()
        {
            IsOpen = false;
            ClosedFrame = Time.frameCount;
            if (DSTNetPlayer.Local != null)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        static bool MPressed()
        {
            if (DUST.UI.DSTChat.ConsumesKeys) return false;   // 聊天框在用鍵盤
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.M);
#endif
        }

        static bool EscPressed()
        {
            if (DUST.UI.DSTChat.ConsumesKeys) return false;   // 聊天框在用鍵盤
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Escape);
#endif
        }

        void ResetSource()
        {
            foreach (var t in _tiles.Values) if (t.tex) Destroy(t.tex);
            _tiles.Clear();
            _running = 0;
            _gen = null;
            if (_dr2Tex) Destroy(_dr2Tex);
            _dr2Tex = null;
        }

        // ------------------------------------------------------------------ 自己在哪
        static bool Viewer(out Vector3 pos, out float yaw)
        {
            foreach (var v in DSTVehicleEntry.All)
                if (v && v.IsLocalDriver) { pos = v.transform.position; yaw = v.transform.eulerAngles.y; return true; }
            var me = DSTNetPlayer.Local;
            pos = me ? me.transform.position : Vector3.zero;
            yaw = me ? me.transform.eulerAngles.y : 0f;
            return me;
        }

        // ------------------------------------------------------------------ 圖塊
        Texture2D GetTile(int level, int x, int z)
        {
            var key = new TileKey { level = level, x = x, z = z };
            if (_tiles.TryGetValue(key, out var t))
            {
                t.lastUsed = Time.time;
                return t.tex;
            }
            if (_running >= MaxJobs || _gen == null) return null;
            var gen = _gen;
            _tiles[key] = new Tile { job = Task.Run(() => DSTMapTiles.Build(gen, level, x, z)), lastUsed = Time.time };
            _running++;
            return null;
        }

        void FinishJobs()
        {
            foreach (var kv in _tiles)
            {
                var t = kv.Value;
                if (t.job == null || !t.job.IsCompleted) continue;
                _running--;
                if (t.job.IsFaulted) { Debug.LogException(t.job.Exception); t.job = null; continue; }
                t.tex = new Texture2D(DSTMapTiles.Size, DSTMapTiles.Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                t.tex.SetPixels32(t.job.Result);
                t.tex.Apply(false, true);
                t.job = null;
            }
            // 太多就丟掉最久沒看的
            if (_tiles.Count > MaxCachedTiles)
            {
                _tmp.Clear();
                foreach (var kv in _tiles) if (kv.Value.job == null) _tmp.Add(kv.Key);
                _tmp.Sort((a, b) => _tiles[a].lastUsed.CompareTo(_tiles[b].lastUsed));
                for (int i = 0; i < _tiles.Count - MaxCachedTiles && i < _tmp.Count; i++)
                {
                    var t = _tiles[_tmp[i]];
                    if (t.tex) Destroy(t.tex);
                    _tiles.Remove(_tmp[i]);
                }
            }
        }

        // 遺跡二：從上往下掃（每像素 0.5 m），牆頂和屋頂畫亮一點、外框深色
        void BuildDr2Map()
        {
            var world = DSTWorld.Instance;
            var root = world ? world.dr2Map : null;
            if (!root) return;
            var rs = root.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return;
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            const float mpp = 0.5f;
            int w = Mathf.CeilToInt(b.size.x / mpp), h = Mathf.CeilToInt(b.size.z / mpp);
            _dr2Area = new Rect(b.min.x, b.min.z, w * mpp, h * mpp);
            var heights = new float[w * h];
            float top = b.max.y + 5f;
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                var o = new Vector3(b.min.x + (i + 0.5f) * mpp, top, b.min.z + (j + 0.5f) * mpp);
                heights[j * w + i] = Physics.Raycast(o, Vector3.down, out var hit, top - b.min.y + 5f, ~0, QueryTriggerInteraction.Ignore) && hit.transform.IsChildOf(root.transform)
                    ? hit.point.y : float.NaN;
            }
            var px = new Color32[w * h];
            var sand = new Color32(214, 186, 142, 255);
            var roof = new Color32(232, 212, 178, 255);
            var edge = new Color32(105, 80, 58, 255);
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                float y = heights[j * w + i];
                if (float.IsNaN(y)) { px[j * w + i] = new Color32(40, 34, 28, 255); continue; }
                bool high = y > b.min.y + 2.5f;
                bool border = false;
                if (high)
                    for (int dj = -1; dj <= 1 && !border; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        int ii = Mathf.Clamp(i + di, 0, w - 1), jj = Mathf.Clamp(j + dj, 0, h - 1);
                        float yy = heights[jj * w + ii];
                        if (float.IsNaN(yy) || yy < y - 1.5f) { border = true; break; }
                    }
                var c = border ? edge : high ? roof : sand;
                float shade = Mathf.Lerp(0.85f, 1.05f, Mathf.InverseLerp(b.min.y, b.min.y + 3f, y));
                px[j * w + i] = new Color32((byte)Mathf.Min(255, c.r * shade), (byte)Mathf.Min(255, c.g * shade), (byte)Mathf.Min(255, c.b * shade), 255);
            }
            _dr2Tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            _dr2Tex.SetPixels32(px);
            _dr2Tex.Apply(false, true);
        }

        // ------------------------------------------------------------------ 畫圖
        void OnGUI()
        {
            if (DSTNetPlayer.Local == null || !Viewer(out var me, out float myYaw)) return;
            Styles();
            var world = DSTWorld.Instance;
            bool random = world && world.IsRandom && _gen != null;
            if (!random && _dr2Tex == null) BuildDr2Map();

            if (!IsOpen)
            {
                // 小地圖：時鐘下方，北朝上，自己在正中央
                var r = new Rect(Screen.width - 232, 48, 220, 220);
                Frame(r);
                DrawMap(r, new Vector2(me.x, me.z), random ? 1.1f : 0.35f, random, me, myYaw, small: true);
                GUI.Label(new Rect(r.x, r.yMax + 2, r.width, 22), "[M] 地圖", _small);
                return;
            }

            // 全螢幕地圖
            var area = new Rect(50, 70, Screen.width - 100, Screen.height - 120);
            HandleMapInput(area);
            if (_follow) _center = new Vector2(me.x, me.z);
            var bg = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _white);
            GUI.color = bg;
            Frame(area);
            DrawMap(area, _center, _mpp, random, me, myYaw, small: false);

            string title = random ? $"地圖　種子碼 {world.Seed}" : "地圖　遺跡二";
            GUI.Label(new Rect(50, 22, 600, 40), title, _title);
            GUI.Label(new Rect(Screen.width - 650, 30, 600, 30), "滾輪縮放　拖曳移動　[M] / [Esc] 關閉", _label);
            if (GUI.Button(new Rect(area.x + 12, area.yMax - 52, 170, 40), "回到我的位置", _button)) { _follow = true; }
            // 比例尺
            float meters = NiceMeters(_mpp * 160f);
            float barPx = meters / _mpp;
            var bar = new Rect(area.xMax - barPx - 24, area.yMax - 30, barPx, 4);
            GUI.DrawTexture(bar, _white);
            GUI.Label(new Rect(bar.x, bar.y - 26, Mathf.Max(80, barPx), 24), meters >= 1000 ? $"{meters / 1000f:0.#} km" : $"{meters:0} m", _small);
        }

        void HandleMapInput(Rect area)
        {
            var e = Event.current;
            if (!area.Contains(e.mousePosition)) return;
            if (e.type == EventType.ScrollWheel)
            {
                // 以滑鼠所在位置為中心縮放
                Vector2 before = ScreenToWorld(area, _center, _mpp, e.mousePosition);
                var world = DSTWorld.Instance;
                _mpp = Mathf.Clamp(_mpp * (e.delta.y > 0 ? 1.25f : 0.8f), world && world.IsRandom ? 0.4f : 0.06f, 24f);
                Vector2 after = ScreenToWorld(area, _center, _mpp, e.mousePosition);
                _center += before - after;
                _follow = false;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0)
            {
                _center += new Vector2(-e.delta.x, e.delta.y) * _mpp;
                _follow = false;
                e.Use();
            }
        }

        static Vector2 ScreenToWorld(Rect area, Vector2 center, float mpp, Vector2 sp) =>
            center + new Vector2(sp.x - area.center.x, area.center.y - sp.y) * mpp;

        static Vector2 WorldToScreen(Rect area, Vector2 center, float mpp, Vector2 wp) =>
            new Vector2(area.center.x + (wp.x - center.x) / mpp, area.center.y - (wp.y - center.y) / mpp);

        void DrawMap(Rect area, Vector2 center, float mpp, bool random, Vector3 me, float myYaw, bool small)
        {
            GUI.BeginGroup(area);
            var local = new Rect(0, 0, area.width, area.height);
            var bg = GUI.color;
            GUI.color = new Color(0.16f, 0.13f, 0.1f, 1f);
            GUI.DrawTexture(local, _white);
            GUI.color = bg;

            if (random) DrawTiles(local, center, mpp);
            else if (_dr2Tex)
            {
                var a = WorldToScreen(local, center, mpp, new Vector2(_dr2Area.xMin, _dr2Area.yMax));
                GUI.DrawTexture(new Rect(a.x, a.y, _dr2Area.width / mpp, _dr2Area.height / mpp), _dr2Tex);
            }

            // 聚落名稱
            if (random)
            {
                Vector2 halfW = new Vector2(local.width, local.height) * 0.5f * mpp;
                int x0 = Mathf.FloorToInt((center.x - halfW.x - 100f) / DSTWorldGen.SettleCell), x1 = Mathf.FloorToInt((center.x + halfW.x + 100f) / DSTWorldGen.SettleCell);
                int z0 = Mathf.FloorToInt((center.y - halfW.y - 100f) / DSTWorldGen.SettleCell), z1 = Mathf.FloorToInt((center.y + halfW.y + 100f) / DSTWorldGen.SettleCell);
                if ((x1 - x0) * (z1 - z0) < 400)
                    for (int i = x0; i <= x1; i++)
                    for (int j = z0; j <= z1; j++)
                    {
                        var s = _gen.SettlementAt(i, j);
                        if (s == null) continue;
                        var p = WorldToScreen(local, center, mpp, s.center);
                        if (!local.Contains(p)) continue;
                        Icon(_square, p, small ? 7 : 10, new Color(0.45f, 0.25f, 0.12f));
                        if (!small || Vector2.Distance(p, local.center) < 95f)
                            Shadowed(new Rect(p.x - 120, p.y + 6, 240, 24), s.Title, small ? _small : _label);
                    }
                // 出生點
                var sp = WorldToScreen(local, center, mpp, Vector2.zero);
                if (local.Contains(sp)) { Icon(_dot, sp, 10, Color.white); if (!small) Shadowed(new Rect(sp.x - 60, sp.y + 6, 120, 22), "出生點", _small); }
            }

            // MeBike 站
            foreach (var st in DSTMeBikeStation.All)
            {
                if (!st) continue;
                var p = WorldToScreen(local, center, mpp, new Vector2(st.transform.position.x, st.transform.position.z));
                if (!local.Contains(p)) continue;
                Icon(_dot, p, small ? 12 : 16, new Color(0.1f, 0.1f, 0.1f));
                Icon(_dot, p, small ? 8 : 11, new Color(1f, 0.55f, 0.1f));
                if (!small) Shadowed(new Rect(p.x - 80, p.y + 8, 160, 22), st.stationName, _small);
            }

            // 載具
            foreach (var v in DSTVehicleEntry.All)
            {
                if (!v || v.IsLocalDriver) continue;
                var p = WorldToScreen(local, center, mpp, new Vector2(v.transform.position.x, v.transform.position.z));
                if (!local.Contains(p)) continue;
                Icon(_square, p, small ? 8 : 11, new Color(0.15f, 0.15f, 0.15f));
                Color vc = v.Drivable is DSTDroneController ? new Color(0.55f, 0.8f, 1f) : v.Drivable is DSTBikeController ? new Color(1f, 0.55f, 0.1f) : new Color(0.95f, 0.8f, 0.3f);
                Icon(_square, p, small ? 5 : 7, vc);
            }

            // 其他玩家
            foreach (var pl in DSTNetPlayer.All)
            {
                if (!pl || pl == DSTNetPlayer.Local || pl.InVehicle) continue;
                var p = WorldToScreen(local, center, mpp, new Vector2(pl.transform.position.x, pl.transform.position.z));
                if (!local.Contains(p)) { if (!small) continue; p = ClampToEdge(local, p); }
                Arrow(p, pl.transform.eulerAngles.y, small ? 16 : 22, new Color(0.35f, 0.75f, 1f));
                if (!small) Shadowed(new Rect(p.x - 60, p.y + 10, 120, 22), $"玩家 {pl.OwnerClientId + 1}", _small);
            }

            // 自己
            var mp = WorldToScreen(local, center, mpp, new Vector2(me.x, me.z));
            if (local.Contains(mp)) Arrow(mp, myYaw, small ? 18 : 24, new Color(1f, 0.45f, 0.2f));

            if (small) Shadowed(new Rect(local.width - 26, 4, 22, 22), "N", _label);
            GUI.EndGroup();
        }

        void DrawTiles(Rect local, Vector2 center, float mpp)
        {
            int level = mpp > 4f ? 1 : 0;
            float tm = DSTMapTiles.TileMeters(level);
            Vector2 half = new Vector2(local.width, local.height) * 0.5f * mpp;
            int x0 = Mathf.FloorToInt((center.x - half.x) / tm), x1 = Mathf.FloorToInt((center.x + half.x) / tm);
            int z0 = Mathf.FloorToInt((center.y - half.y) / tm), z1 = Mathf.FloorToInt((center.y + half.y) / tm);
            // 先要近的（中央先出來）
            for (int ring = 0; ring <= Mathf.Max(x1 - x0, z1 - z0); ring++)
            for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                int cx = Mathf.FloorToInt(center.x / tm), cz = Mathf.FloorToInt(center.y / tm);
                if (Mathf.Max(Mathf.Abs(x - cx), Mathf.Abs(z - cz)) != ring) continue;
                var tex = GetTile(level, x, z);
                if (!tex && level == 1) continue;
                // 細的還沒好就先用粗的頂著
                if (!tex) { DrawCoarseFallback(local, center, mpp, x, z); continue; }
                var a = WorldToScreen(local, center, mpp, new Vector2(x * tm, (z + 1) * tm));
                GUI.DrawTexture(new Rect(a.x, a.y, tm / mpp + 0.5f, tm / mpp + 0.5f), tex);
            }
        }

        void DrawCoarseFallback(Rect local, Vector2 center, float mpp, int x, int z)
        {
            float fine = DSTMapTiles.TileMeters(0), coarse = DSTMapTiles.TileMeters(1);
            int cx = Mathf.FloorToInt(x * fine / coarse), cz = Mathf.FloorToInt(z * fine / coarse);
            var key = new TileKey { level = 1, x = cx, z = cz };
            if (!_tiles.TryGetValue(key, out var t) || !t.tex) return;
            var a = WorldToScreen(local, center, mpp, new Vector2(x * fine, (z + 1) * fine));
            float u = (x * fine - cx * coarse) / coarse, v = (z * fine - cz * coarse) / coarse, s = fine / coarse;
            GUI.DrawTextureWithTexCoords(new Rect(a.x, a.y, fine / mpp + 0.5f, fine / mpp + 0.5f), t.tex, new Rect(u, v, s, s));
        }

        static Vector2 ClampToEdge(Rect r, Vector2 p) => new Vector2(Mathf.Clamp(p.x, 8, r.width - 8), Mathf.Clamp(p.y, 8, r.height - 8));

        static float NiceMeters(float m)
        {
            float[] steps = { 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
            foreach (var s in steps) if (s >= m * 0.6f) return s;
            return 10000;
        }

        // ------------------------------------------------------------------ 圖示
        void Icon(Texture2D t, Vector2 p, float size, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x - size * 0.5f, p.y - size * 0.5f, size, size), t);
            GUI.color = old;
        }

        void Arrow(Vector2 p, float yaw, float size, Color c)
        {
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(yaw, p);
            Icon(_arrow, p, size + 4, new Color(0f, 0f, 0f, 0.8f));
            Icon(_arrow, p, size, c);
            GUI.matrix = m;
        }

        void Frame(Rect r)
        {
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6), _white);
            GUI.color = old;
        }

        static void Shadowed(Rect r, string text, GUIStyle s)
        {
            var c = s.normal.textColor;
            s.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, s);
            s.normal.textColor = c;
            GUI.Label(r, text, s);
        }

        void Styles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter };
            _label.normal.textColor = Color.white;
            _small = new GUIStyle(_label) { fontSize = 14 };
            _title = new GUIStyle(_label) { fontSize = 28, alignment = TextAnchor.UpperLeft };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 18 };
            _white = Texture2D.whiteTexture;
            _dot = MakeIcon(32, (x, y) => (x * x + y * y) < 0.8f);
            _square = MakeIcon(32, (x, y) => Mathf.Abs(x) < 0.85f && Mathf.Abs(y) < 0.85f);
            // 箭頭：尖端朝上（北），尾巴內凹
            _arrow = MakeIcon(64, (x, y) =>
            {
                float t = (y + 1f) * 0.5f;                 // 0 下 → 1 上（貼圖的 y 往上）
                if (t > 0.95f) return false;
                float half = (1f - t / 0.95f) * 0.75f;     // 越往上越窄
                bool notch = t < 0.25f && Mathf.Abs(x) < (0.25f - t) / 0.25f * 0.5f;
                return Mathf.Abs(x) < half && !notch;
            });
        }

        static Texture2D MakeIcon(int size, System.Func<float, float, bool> inside)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                // 4×4 超取樣，邊緣平滑
                int hits = 0;
                for (int sj = 0; sj < 4; sj++)
                for (int si = 0; si < 4; si++)
                {
                    float x = ((i + (si + 0.5f) / 4f) / size) * 2f - 1f;
                    float y = ((j + (sj + 0.5f) / 4f) / size) * 2f - 1f;
                    if (inside(x, y)) hits++;
                }
                px[j * size + i] = new Color32(255, 255, 255, (byte)(hits * 255 / 16));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }
    }
}
