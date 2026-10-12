using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DUST.World
{
    // 地形的一個區塊（64 m × 64 m）。網格資料在背景執行緒算（Build），回到主執行緒才建 Mesh（Apply）。
    // 細節等級：0 = 每 1 m 一點（近處、有碰撞）、1 = 2 m、2 = 4 m（遠景）。
    // 不同細節的區塊相鄰時邊上會有縫，四周加一圈往下垂的「裙邊」蓋住。
    public class DSTWorldChunk : MonoBehaviour
    {
        public const float Size = 64f;
        public static readonly float[] LodStep = { 1f, 2f, 4f };

        public Vector2Int Coord { get; private set; }
        public int Lod { get; private set; } = -1;
        public bool HasCollider => _collider && _collider.sharedMesh;
        public bool ScatterBuilt => _scatter;
        public Transform ScatterRoot => _scatter ? _scatter.transform : null;

        MeshFilter _filter;
        MeshRenderer _renderer;
        MeshCollider _collider;
        Mesh _mesh;
        GameObject _scatter;

        public static Vector3 Origin(Vector2Int c) => new Vector3(c.x * Size, 0f, c.y * Size);

        public static DSTWorldChunk Create(Transform parent, Vector2Int c, DSTWorldPalette palette)
        {
            var go = new GameObject($"Chunk_{c.x}_{c.y}");
            go.transform.SetParent(parent, false);
            go.transform.position = Origin(c);
            go.isStatic = false;
            var ch = go.AddComponent<DSTWorldChunk>();
            ch.Coord = c;
            ch._filter = go.AddComponent<MeshFilter>();
            ch._renderer = go.AddComponent<MeshRenderer>();
            ch._renderer.sharedMaterials = new[] { palette.sand, palette.rock, palette.road ? palette.road : palette.sand };
            ch._renderer.shadowCastingMode = ShadowCastingMode.On;
            DSTOwnedMesh.OptOutGpuDriven(ch._renderer);
            ch._mesh = new Mesh { name = go.name };
            ch._mesh.MarkDynamic();
            ch._filter.sharedMesh = ch._mesh;
            return ch;
        }

        // ------------------------------------------------------------------ 背景執行緒
        public sealed class Data
        {
            public Vector2Int coord;
            public int lod;
            public Vector3[] vertices;
            public Vector3[] normals;
            public int[] sand;
            public int[] rock;
            public int[] road;
            public Bounds bounds;
        }

        public static Data Build(DSTWorldGen gen, Vector2Int c, int lod)
        {
            float step = LodStep[lod];
            int q = Mathf.RoundToInt(Size / step);       // 每邊幾格
            int n = q + 1;                               // 每邊幾個點
            int b = n + 2;                               // 外加一圈算法線用
            float ox = c.x * Size, oz = c.y * Size;

            var h = new float[b * b];
            var rockMask = new float[n * n];
            var roadMask = new float[n * n];
            for (int z = 0; z < b; z++)
            for (int x = 0; x < b; x++)
            {
                float wx = ox + (x - 1) * step, wz = oz + (z - 1) * step;
                float r, rd;
                h[z * b + x] = gen.Sample(wx, wz, out r, out rd);
                if (x >= 1 && x <= n && z >= 1 && z <= n)
                {
                    rockMask[(z - 1) * n + (x - 1)] = r;
                    roadMask[(z - 1) * n + (x - 1)] = rd;
                }
            }

            int skirtCount = 4 * q;
            var verts = new Vector3[n * n + skirtCount];
            var norms = new Vector3[verts.Length];
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                float y = h[(z + 1) * b + (x + 1)];
                verts[z * n + x] = new Vector3(x * step, y, z * step);
                float hx = h[(z + 1) * b + (x + 2)] - h[(z + 1) * b + x];
                float hz = h[(z + 2) * b + (x + 1)] - h[z * b + (x + 1)];
                norms[z * n + x] = new Vector3(-hx, 2f * step, -hz).normalized;
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }

            var sand = new List<int>(q * q * 6 + skirtCount * 6);
            var rock = new List<int>(q * q);
            var road = new List<int>(q * q);
            for (int z = 0; z < q; z++)
            for (int x = 0; x < q; x++)
            {
                int i0 = z * n + x, i1 = i0 + 1, i2 = i0 + n, i3 = i2 + 1;
                AddTri(i0, i2, i1);
                AddTri(i1, i2, i3);
            }

            void AddTri(int a, int b2, int c2)
            {
                float ny = (norms[a].y + norms[b2].y + norms[c2].y) / 3f;
                float rk = (rockMask[a] + rockMask[b2] + rockMask[c2]) / 3f;
                // 台地區的陡坡是砂岩崖；沙丘的落沙坡再陡也還是沙
                bool isRock = (rk > 0.45f && ny < 0.9f) || (rk > 0.12f && ny < 0.7f);
                // 街道改用聚落自己鋪的路面網格（邊緣不規則），地形這裡不分（逐三角形分會變成鋸齒）
                var list = isRock ? rock : sand;
                list.Add(a); list.Add(b2); list.Add(c2);
            }

            // 裙邊：沿著四邊（逆時針）每個點往下複製一個，兩兩接成往下垂的面
            float drop = 1.5f + step * 1.5f;
            var ring = new List<int>(skirtCount);
            for (int x = 0; x < q; x++) ring.Add(x);                       // 南邊 z=0
            for (int z = 0; z < q; z++) ring.Add(z * n + q);               // 東邊 x=q
            for (int x = q; x > 0; x--) ring.Add(q * n + x);               // 北邊 z=q
            for (int z = q; z > 0; z--) ring.Add(z * n);                   // 西邊 x=0
            int s0 = n * n;
            for (int i = 0; i < ring.Count; i++)
            {
                verts[s0 + i] = verts[ring[i]] + Vector3.down * drop;
                norms[s0 + i] = norms[ring[i]];
            }
            for (int i = 0; i < ring.Count; i++)
            {
                int a = ring[i], bb = ring[(i + 1) % ring.Count];
                int sa = s0 + i, sb = s0 + (i + 1) % ring.Count;
                // 面朝外
                sand.Add(a); sand.Add(bb); sand.Add(sa);
                sand.Add(bb); sand.Add(sb); sand.Add(sa);
            }

            var center = new Vector3(Size * 0.5f, (minY + maxY) * 0.5f - drop * 0.5f, Size * 0.5f);
            var size = new Vector3(Size, maxY - minY + drop, Size);
            return new Data
            {
                coord = c, lod = lod, vertices = verts, normals = norms,
                sand = sand.ToArray(), rock = rock.ToArray(), road = road.ToArray(),
                bounds = new Bounds(center, size),
            };
        }

        // ------------------------------------------------------------------ 主執行緒
        public void Apply(Data d)
        {
            bool hadCollider = HasCollider;
            _mesh.Clear();
            _mesh.indexFormat = d.vertices.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _mesh.vertices = d.vertices;
            _mesh.normals = d.normals;
            _mesh.subMeshCount = 3;
            _mesh.SetTriangles(d.sand, 0, false);
            _mesh.SetTriangles(d.rock, 1, false);
            _mesh.SetTriangles(d.road, 2, false);
            _mesh.bounds = d.bounds;
            Lod = d.lod;
            // 網格換了，碰撞要重新烘焙
            if (hadCollider) _collider.sharedMesh = null;
        }

        public EntityId MeshId => _mesh.GetEntityId();
        public bool CanCollide => Lod == 0;

        // 呼叫前要先在背景執行緒 Physics.BakeMesh(MeshId, false)
        public void AttachCollider()
        {
            if (!_collider) _collider = gameObject.AddComponent<MeshCollider>();
            _collider.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation | MeshColliderCookingOptions.EnableMeshCleaning | MeshColliderCookingOptions.WeldColocatedVertices | MeshColliderCookingOptions.UseFastMidphase;
            _collider.sharedMesh = _mesh;
        }

        public void DetachCollider()
        {
            if (_collider) _collider.sharedMesh = null;
        }

        public void SetScatter(GameObject root)
        {
            if (_scatter) Destroy(_scatter);
            _scatter = root;
        }

        public void ClearScatter()
        {
            if (_scatter) Destroy(_scatter);
            _scatter = null;
        }

        void OnDestroy()
        {
            if (_mesh) Destroy(_mesh);
        }
    }
}
