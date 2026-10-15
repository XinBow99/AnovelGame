using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.World
{
    // 遠景地形（issue #7 第三步，米格-25 高空超音速用）：近處 ±400 m 還是 64 m 區塊；外面再兩層粗網格，
    // 同一套地形公式（DSTWorldGen.Sample），在背景執行緒算好再換上：
    //   第 1 層：每 24 m 一點，4.2 km 見方，視點離中心 512 m 就重算
    //   第 2 層：每 160 m 一點，25.6 km 見方，挖掉第 1 層蓋住的部分
    //   第 3 層：每 1 km 一點，128 km 見方，挖掉第 2 層（5 km 高空時 12.8 km 外的地面只在水平線下 21°，
    //            沒有這層會直接看到天空貼圖下半部的岩石照片，實測）
    // 粗網格在山谷會比真的地面高（兩點之間拉直線），所以每個點往下沉：
    // 固定 1.5 / 4 m，再加上相鄰格子「四角平均 − 格子中心真實高度」的最大值 → 格子中心一定不會蓋過近處的區塊。
    // 不做碰撞（只有看的；碰撞還是近處區塊 + 前方預載）。
    public class DSTFarTerrain : MonoBehaviour
    {
        const int Cells1 = 176;
        const float Step1 = 24f, Snap1 = 512f, Sink1 = 1.5f;
        const int Cells2 = 160;
        const float Step2 = 160f, Snap2 = 3200f, Sink2 = 4f;
        const int Cells3 = 128;
        const float Step3 = 1000f, Snap3 = 16000f, Sink3 = 12f;

        DSTWorldGen _gen;
        Level _l1, _l2, _l3;

        sealed class Level
        {
            public int cells;
            public float step, snap, sink;
            public Vector2 center = new Vector2(float.NaN, float.NaN);
            public Task<MeshData> job;
            public Vector2 jobCenter;
            public Mesh mesh;
            public MeshRenderer renderer;
        }

        sealed class MeshData
        {
            public Vector3[] verts;
            public Vector3[] norms;
            public int[] sand, rock;
            public Bounds bounds;
        }

        public static DSTFarTerrain Create(Transform parent, DSTWorldGen gen, DSTWorldPalette palette)
        {
            var go = new GameObject("FarTerrain");
            go.transform.SetParent(parent, false);
            var ft = go.AddComponent<DSTFarTerrain>();
            ft._gen = gen;
            ft._l1 = ft.MakeLevel("Far1", Cells1, Step1, Snap1, Sink1, palette);
            ft._l2 = ft.MakeLevel("Far2", Cells2, Step2, Snap2, Sink2, palette);
            ft._l3 = ft.MakeLevel("Far3", Cells3, Step3, Snap3, Sink3, palette);
            ft.BuildDome();
            return ft;
        }

        Level MakeLevel(string name, int cells, float step, float snap, float sink, DSTWorldPalette palette)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var lv = new Level { cells = cells, step = step, snap = snap, sink = sink };
            lv.mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            lv.mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = lv.mesh;
            lv.renderer = go.AddComponent<MeshRenderer>();
            lv.renderer.sharedMaterials = new[] { palette.sand, palette.rock };
            lv.renderer.shadowCastingMode = ShadowCastingMode.Off;
            lv.renderer.enabled = false;
            DSTOwnedMesh.OptOutGpuDriven(lv.renderer);
            return lv;
        }

        /// <summary>每幀由 DSTWorld 呼叫：視點走遠了就在背景重算，算好就換上</summary>
        public void Tick(Vector3 viewer)
        {
            var v = new Vector2(viewer.x, viewer.z);
            Step(_l1, v, null);
            Step(_l2, v, _l1);
            Step(_l3, v, _l2);
            UpdateDome(viewer);
        }

        void Step(Level lv, Vector2 viewer, Level hole)
        {
            if (lv.job != null && lv.job.IsCompleted)
            {
                if (!lv.job.IsFaulted) Apply(lv, lv.job.Result);
                else Debug.LogException(lv.job.Exception);
                lv.center = lv.jobCenter;
                lv.job = null;
            }
            if (lv.job != null) return;
            var want = new Vector2(Mathf.Round(viewer.x / lv.snap) * lv.snap, Mathf.Round(viewer.y / lv.snap) * lv.snap);
            if (want == lv.center) return;
            // 第 2 層要挖洞的位置 = 第 1 層「將要」的中心（第 1 層也會跟著換）
            Vector2 holeCenter = hole != null ? new Vector2(Mathf.Round(viewer.x / hole.snap) * hole.snap, Mathf.Round(viewer.y / hole.snap) * hole.snap) : Vector2.zero;
            float holeHalf = hole != null ? (hole.cells * hole.step * 0.5f - hole.step * 3f) : 0f;
            var gen = _gen;
            int cells = lv.cells; float step = lv.step, sink = lv.sink;
            lv.jobCenter = want;
            lv.job = Task.Run(() => Build(gen, want, cells, step, sink, holeCenter, holeHalf));
        }

        static MeshData Build(DSTWorldGen gen, Vector2 center, int cells, float step, float sinkBase, Vector2 holeCenter, float holeHalf)
        {
            int n = cells + 1;
            float half = cells * step * 0.5f;
            float ox = center.x - half, oz = center.y - half;
            var h = new float[n * n];
            var rk = new float[n * n];
            for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                h[z * n + x] = gen.Sample(ox + x * step, oz + z * step, out rk[z * n + x], out _);

            // 每格：四角平均比格子中心的真實地面高多少（粗網格在凹處浮起來的量）
            var over = new float[cells * cells];
            for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                float avg = (h[z * n + x] + h[z * n + x + 1] + h[(z + 1) * n + x] + h[(z + 1) * n + x + 1]) * 0.25f;
                float mid = gen.Height(ox + (x + 0.5f) * step, oz + (z + 0.5f) * step);
                over[z * cells + x] = Mathf.Max(0f, avg - mid);
            }

            var verts = new Vector3[n * n];
            var norms = new Vector3[n * n];
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                float s = 0f;
                for (int dz = -1; dz <= 0; dz++)
                for (int dx = -1; dx <= 0; dx++)
                {
                    int cx = x + dx, cz = z + dz;
                    if (cx >= 0 && cz >= 0 && cx < cells && cz < cells) s = Mathf.Max(s, over[cz * cells + cx]);
                }
                float y = h[z * n + x] - sinkBase - s;
                verts[z * n + x] = new Vector3(ox + x * step, y, oz + z * step);
                float hx = h[z * n + Mathf.Min(x + 1, n - 1)] - h[z * n + Mathf.Max(x - 1, 0)];
                float hz = h[Mathf.Min(z + 1, n - 1) * n + x] - h[Mathf.Max(z - 1, 0) * n + x];
                norms[z * n + x] = new Vector3(-hx, 2f * step, -hz).normalized;
                minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
            }

            var sand = new List<int>(cells * cells * 6);
            var rock = new List<int>(cells * cells);
            for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                if (holeHalf > 0f)
                {
                    float cx = ox + (x + 0.5f) * step - holeCenter.x, cz = oz + (z + 0.5f) * step - holeCenter.y;
                    if (Mathf.Abs(cx) + step * 0.5f < holeHalf && Mathf.Abs(cz) + step * 0.5f < holeHalf) continue;
                }
                int i0 = z * n + x, i1 = i0 + 1, i2 = i0 + n, i3 = i2 + 1;
                float r = (rk[i0] + rk[i1] + rk[i2] + rk[i3]) * 0.25f;
                float ny = (norms[i0].y + norms[i1].y + norms[i2].y + norms[i3].y) * 0.25f;
                var list = (r > 0.45f && ny < 0.9f) || (r > 0.12f && ny < 0.7f) ? rock : sand;
                list.Add(i0); list.Add(i2); list.Add(i1);
                list.Add(i1); list.Add(i2); list.Add(i3);
            }
            var b = new Bounds(new Vector3(center.x, (minY + maxY) * 0.5f, center.y), new Vector3(cells * step, maxY - minY + 1f, cells * step));
            return new MeshData { verts = verts, norms = norms, sand = sand.ToArray(), rock = rock.ToArray(), bounds = b };
        }

        static void Apply(Level lv, MeshData d)
        {
            var m = lv.mesh;
            m.Clear();
            m.SetVertices(d.verts);
            m.SetNormals(d.norms);
            m.subMeshCount = 2;
            m.SetTriangles(d.sand, 0, false);
            m.SetTriangles(d.rock, 1, false);
            m.bounds = d.bounds;
            lv.renderer.enabled = true;
        }

        // ------------------------------------------------------------------ 高空天空罩
        // 天空貼圖（HDRI）是在地面拍的沙漠照片：水平線附近有一圈岩石和山（近的岩石高到水平線上 20° 以上）。
        // 在地上它就是遠山背景；飛到幾公里高時，這圈岩石會變成立在水平線上的一道牆（實測截圖）。
        // 所以高空時在視點外 150 km 罩一顆漸層天空球：水平線附近是淡白的地平線霧、往上漸漸變藍，
        // 離地 500 m 以下完全透明（地面還是看原本的遠山），2.5 km 以上完全蓋住照片。
        // 光照（環境光、太陽）還是來自原本的天空，不受影響。
        const float DomeRadius = 150000f;
        const int DomeSegments = 64;
        static readonly float[] DomeElev = { -20f, -2f, 0f, 3f, 8f, 16f, 30f, 50f, 70f, 90f };
        Mesh _domeMesh;
        MeshRenderer _domeR;
        Material _domeMat;
        static readonly int UnlitColorId = Shader.PropertyToID("_UnlitColor");

        void BuildDome()
        {
            var shader = Shader.Find("HDRP/Unlit");
            if (!shader) return;
            // 貼圖只有直的漸層：v = 仰角（-20° .. 90°）
            const int H = 128;
            var tex = new Texture2D(2, H, TextureFormat.RGBAHalf, false, true) { wrapMode = TextureWrapMode.Clamp, name = "HighSky" };
            var haze = new Color(0.80f, 0.84f, 0.90f);
            var mid = new Color(0.55f, 0.70f, 0.90f);
            var zen = new Color(0.34f, 0.50f, 0.82f);
            for (int y = 0; y < H; y++)
            {
                float elev = Mathf.Lerp(-20f, 90f, y / (H - 1f));
                Color c = elev <= 1f ? haze
                        : elev <= 18f ? Color.Lerp(haze, mid, Mathf.SmoothStep(0f, 1f, (elev - 1f) / 17f))
                        : Color.Lerp(mid, zen, Mathf.SmoothStep(0f, 1f, (elev - 18f) / 72f));
                c.a = 1f;
                tex.SetPixel(0, y, c); tex.SetPixel(1, y, c);
            }
            tex.Apply();
            _domeMat = new Material(shader) { name = "HighSky" };
            _domeMat.SetTexture("_UnlitColorMap", tex);
            _domeMat.SetColor("_UnlitColor", Color.white);
            _domeMat.SetFloat("_SurfaceType", 1f);
            _domeMat.SetFloat("_BlendMode", 0f);
            _domeMat.SetFloat("_EnableFogOnTransparent", 0f);
            _domeMat.SetFloat("_DoubleSidedEnable", 1f);
            HDMaterial.ValidateMaterial(_domeMat);

            // 球面（以視點為中心）：方位角一圈 × 仰角幾圈；uv.y = 仰角對應到貼圖
            int n = DomeSegments + 1, rings = DomeElev.Length;
            var v = new Vector3[n * rings];
            var uv = new Vector2[n * rings];
            for (int r = 0; r < rings; r++)
            {
                float e = DomeElev[r] * Mathf.Deg2Rad;
                for (int s = 0; s < n; s++)
                {
                    float a = s / (float)DomeSegments * Mathf.PI * 2f;
                    v[r * n + s] = new Vector3(Mathf.Cos(e) * Mathf.Cos(a), Mathf.Sin(e), Mathf.Cos(e) * Mathf.Sin(a)) * DomeRadius;
                    uv[r * n + s] = new Vector2(s / (float)DomeSegments, Mathf.InverseLerp(-20f, 90f, DomeElev[r]));
                }
            }
            var tri = new int[(rings - 1) * DomeSegments * 6];
            int ti = 0;
            for (int r = 0; r < rings - 1; r++)
            for (int s = 0; s < DomeSegments; s++)
            {
                int i0 = r * n + s, i1 = i0 + 1, i2 = i0 + n, i3 = i2 + 1;
                tri[ti++] = i0; tri[ti++] = i2; tri[ti++] = i1;
                tri[ti++] = i1; tri[ti++] = i2; tri[ti++] = i3;
            }
            _domeMesh = new Mesh { name = "HighSky", vertices = v, uv = uv, triangles = tri };
            _domeMesh.bounds = new Bounds(Vector3.zero, Vector3.one * DomeRadius * 2.1f);

            var go = new GameObject("HighSky");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = _domeMesh;
            _domeR = go.AddComponent<MeshRenderer>();
            _domeR.sharedMaterial = _domeMat;
            _domeR.shadowCastingMode = ShadowCastingMode.Off;
            _domeR.receiveShadows = false;
            _domeR.enabled = false;
            DSTOwnedMesh.OptOutGpuDriven(_domeR);
        }

        void UpdateDome(Vector3 viewer)
        {
            if (!_domeR) return;
            float agl = viewer.y - _gen.Height(viewer.x, viewer.z);
            float k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(500f, 2500f, agl));
            _domeR.enabled = k > 0.01f;
            if (!_domeR.enabled) return;
            _domeR.transform.position = viewer;
            // 跟著太陽變暗：白天原色，晚上幾乎黑
            var tod = DSTTimeOfDay.Instance;
            float day = tod ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-8f, 8f, tod.SunElevation)) : 1f;
            float b = Mathf.Lerp(0.03f, 1f, day);
            _domeMat.SetColor(UnlitColorId, new Color(b, b, b, k));
        }

        void OnDestroy()
        {
            if (_domeMesh) Destroy(_domeMesh);
            if (_domeMat) { Destroy(_domeMat.GetTexture("_UnlitColorMap")); Destroy(_domeMat); }
            foreach (var lv in new[] { _l1, _l2, _l3 })
                if (lv != null && lv.mesh) Destroy(lv.mesh);
        }
    }
}
