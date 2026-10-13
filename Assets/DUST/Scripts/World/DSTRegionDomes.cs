using System.Collections.Generic;
using UnityEngine;

namespace DUST.World
{
    // 遠遠看得到的區域天氣：沙暴是一道往前推進的沙牆（圓筒，底下濃、往上淡），異象是一團異色的霧罩。
    // 走進去之後圓筒就淡掉，換成 DSTWeather 的霧接手。位置、大小全部由時鐘算出來，每台電腦自己畫。
    public class DSTRegionDomes : MonoBehaviour
    {
        public Material domeMaterial;              // M_DST_Dome（HDRP/Unlit 透明、不受霧影響）
        public float viewRange = 4000f;
        public Color stormColor = new Color(0.74f, 0.6f, 0.46f, 1f);
        public float stormHeight = 380f;
        public float anomalyHeight = 420f;
        [Range(0f, 1f)] public float anomalyAlpha = 0.7f;

        readonly List<MeshRenderer> _pool = new List<MeshRenderer>();
        readonly List<DSTClockwork.Storm> _storms = new List<DSTClockwork.Storm>();
        readonly List<DSTAnomaly.Event> _anoms = new List<DSTAnomaly.Event>();
        MaterialPropertyBlock _mpb;
        Mesh _mesh;
        static readonly int ColorId = Shader.PropertyToID("_UnlitColor");

        void Update()
        {
            var cam = Camera.main;
            if (!cam || !domeMaterial) { Hide(0); return; }
            _mpb ??= new MaterialPropertyBlock();
            _mesh ??= Cylinder(48);
            double now = DSTClockwork.Now;
            var eye = cam.transform.position;
            var here = DSTClockwork.Flat(eye);
            float far = Mathf.Min(viewRange, cam.farClipPlane * 0.95f);
            int used = 0;
            // 沒有自己的光照：跟著天色變暗（黃昏、夜裡不會發亮）
            float daylight = 1f;
            var tod = DSTTimeOfDay.Instance;
            if (tod) { tod.SunAngles(tod.Hour, out float elev, out _); daylight = Mathf.Lerp(0.12f, 1f, Mathf.Clamp01((elev + 4f) / 24f)); }

            DSTClockwork.StormsNear(now, here, far, _storms);
            foreach (var s in _storms)
            {
                var c = s.CenterAt(now);
                float a = s.TimeK(now) * Fade(here, c, s.radius, far);
                // 沙暴的頂端隨風翻騰：高度跟著時間微微起伏
                float h = stormHeight * (0.8f + 0.2f * Mathf.PerlinNoise((float)now * 0.7f, s.cx * 0.37f + s.cz));
                used = Layers(used, c, s.radius, h, Shade(stormColor, daylight, stormColor.a * a), eye, (float)now * 4f, 0.95f);
            }

            DSTAnomaly.ActiveNear(now, here, far, _anoms);
            foreach (var e in _anoms)
            {
                float a = e.TimeK(now) * Fade(here, e.center, e.radius, far) * anomalyAlpha * Mathf.Lerp(0.6f, 1f, e.hazeAmount);
                var col = Color.Lerp(e.haze, Color.white, 0.15f);
                used = Layers(used, e.center, e.radius, anomalyHeight, Shade(col, daylight, a), eye, (float)now * 1.5f, 1f);
            }
            Hide(used);
        }

        static Color Shade(Color c, float daylight, float a) => new Color(c.r * daylight, c.g * daylight, c.b * daylight, a);

        // 三層：裡面最濃，外面兩層大一點、高一點、淡一點 → 邊緣不會像刀切的
        int Layers(int used, Vector2 c, float r, float h, Color col, Vector3 eye, float spin, float core)
        {
            Place(used++, c, r, h, new Color(col.r, col.g, col.b, col.a * core), eye, spin);
            Place(used++, c, r * 1.07f, h * 1.12f, new Color(col.r, col.g, col.b, col.a * 0.45f), eye, -spin * 0.7f + 120f);
            Place(used++, c, r * 1.16f, h * 1.25f, new Color(col.r, col.g, col.b, col.a * 0.22f), eye, spin * 0.5f + 240f);
            return used;
        }

        // 在外面看得到；靠近邊緣時淡掉（進去之後由霧接手），太遠也淡掉
        static float Fade(Vector2 here, Vector2 c, float r, float far)
        {
            float d = (c - here).magnitude;
            float inside = Mathf.Clamp01((d - r * 0.85f) / (r * 0.35f));
            float distant = Mathf.Clamp01((far - (d - r)) / (far * 0.12f));
            return inside * distant;
        }

        void Place(int i, Vector2 c, float r, float h, Color col, Vector3 eye, float spin)
        {
            while (_pool.Count <= i) _pool.Add(NewDome());
            var mr = _pool[i];
            if (col.a <= 0.003f) { mr.enabled = false; return; }
            var w = DSTWorld.Instance;
            float y = w && w.IsRandom && w.Gen != null ? w.Gen.Height(c.x, c.y) - 30f : eye.y - 30f;
            mr.transform.SetPositionAndRotation(new Vector3(c.x, y, c.y), Quaternion.Euler(0f, spin, 0f));
            mr.transform.localScale = new Vector3(r, h, r);
            _mpb.SetColor(ColorId, col);
            mr.SetPropertyBlock(_mpb);
            mr.enabled = true;
        }

        void Hide(int from)
        {
            for (int i = from; i < _pool.Count; i++) if (_pool[i]) _pool[i].enabled = false;
        }

        MeshRenderer NewDome()
        {
            var go = new GameObject("RegionDome");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = domeMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.enabled = false;
            return mr;
        }

        // 半徑 1、高 1 的開口圓筒（沒有頂和底），法線朝外；UV：u 繞一圈，v 往上
        static Mesh Cylinder(int seg)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                var d = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                v.Add(d); v.Add(d + Vector3.up);
                n.Add(d); n.Add(d);
                uv.Add(new Vector2(i / (float)seg, 0f)); uv.Add(new Vector2(i / (float)seg, 1f));
                if (i == seg) continue;
                int b = i * 2;
                tri.AddRange(new[] { b, b + 3, b + 1, b, b + 2, b + 3 });   // 從外面看是順時針（Unity 的正面）
            }
            var m = new Mesh { name = "RegionDome" };
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
            m.bounds = new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(2f, 1f, 2f));
            return m;
        }
    }
}
