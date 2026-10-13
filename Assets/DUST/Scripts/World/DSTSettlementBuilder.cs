using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace DUST.World
{
    // 照 DSTSettlement 藍圖把聚落蓋出來：遺跡二的 4 m 牆模組拼房子（門朝街、拱窗、平屋頂、簷口與牆腳線條），
    // 廣場上的水井 / 綠洲水池、攤位、椰棗樹、屋前的陶罐木桶。物件很多，每幀蓋一間避免卡頓。
    public static class DSTSettlementBuilder
    {
        const float H = 4f;   // 牆高

        public static IEnumerator Build(DSTWorldGen gen, DSTWorldPalette pal, DSTSettlement s, Transform parent)
        {
            var root = new GameObject("Settlement_" + s.name).transform;
            root.SetParent(parent, false);
            root.position = new Vector3(s.center.x, s.height, s.center.y);
            float baseY = s.height - 0.05f;

            if (s.pondRadius > 0f && pal.water) CreatePond(root, s.pondRadius + 0.4f, gen.PondWaterHeight(s), pal.water);
            if (pal.road) CreateStreets(gen, s, root, pal.road);

            foreach (var b in s.buildings)
            {
                if (!root) yield break;
                BuildHouse(pal, b, root, baseY);
                yield return null;
            }

            if (!root) yield break;
            var walls = new GameObject("Walls").transform;
            walls.SetParent(root, false);
            var wrng = new System.Random(s.cellX * 7919 + s.cellZ);
            foreach (var (a, bpt) in s.walls)
            {
                var dir = (bpt - a).normalized;
                var rot = Quaternion.Euler(0f, Mathf.Atan2(-dir.y, dir.x) * Mathf.Rad2Deg, 0f);
                var piece = wrng.NextDouble() < 0.15 ? Pick(wrng, pal.wallDamaged) : Pick(wrng, pal.wallStraight);
                Spawn(piece, walls, new Vector3(bpt.x, baseY, bpt.y), rot);
                Spawn(pal.wallCorner, walls, new Vector3(a.x, baseY, a.y), rot);
                Spawn(pal.corniceStraight, walls, new Vector3(bpt.x, baseY + H, bpt.y), rot);
            }
            yield return null;

            if (!root) yield break;
            var props = new GameObject("Props").transform;
            props.SetParent(root, false);
            var frng = new System.Random(s.cellX * 104729 + s.cellZ * 31);
            foreach (var f in s.features)
            {
                GameObject prefab = null;
                switch (f.kind)
                {
                    case DSTSettlement.FeatureKind.Well: prefab = pal.well; break;
                    case DSTSettlement.FeatureKind.Stall: prefab = Pick(frng, pal.stalls); break;
                    case DSTSettlement.FeatureKind.Palm: prefab = Pick(frng, pal.palms); break;
                    case DSTSettlement.FeatureKind.Pot: prefab = Pick(frng, pal.pots); break;
                    case DSTSettlement.FeatureKind.Barrel: prefab = Pick(frng, pal.barrels); break;
                    case DSTSettlement.FeatureKind.Crate: prefab = Pick(frng, pal.crates); break;
                    case DSTSettlement.FeatureKind.Drift: prefab = Pick(frng, pal.drifts); break;
                    case DSTSettlement.FeatureKind.Rubble: prefab = Pick(frng, pal.rubble); break;
                    case DSTSettlement.FeatureKind.Wreck: prefab = pal.wreck; break;
                }
                if (!prefab) continue;
                float y = gen.Height(f.pos.x, f.pos.y) - (f.kind == DSTSettlement.FeatureKind.Palm ? 0.3f : 0.03f);
                var go = Spawn(prefab, props, new Vector3(f.pos.x, y, f.pos.y), Quaternion.Euler(0f, f.yaw, 0f));
                if (go) go.transform.localScale *= f.scale;
            }
            DSTWorldProp.Attach(root.gameObject, gen.Seed, s.cellX, s.cellZ, 2);   // 撞得倒、打得碎
        }

        // ------------------------------------------------------------------ 房子
        // 本地座標：前牆在 z = 0（面向街道、門在這面），x 往右、z 往屋內。四邊依序：前(+x)、右(+z)、後(-x)、左(-z)
        static void BuildHouse(DSTWorldPalette pal, DSTSettlement.Building b, Transform parent, float baseY)
        {
            var rng = new System.Random(b.seed);
            float W = b.w * 4f, D = b.d * 4f;
            var house = new GameObject("House").transform;
            house.SetParent(parent, false);
            house.position = new Vector3(b.origin.x, baseY, b.origin.y);
            house.rotation = Quaternion.Euler(0f, b.Yaw, 0f);
            bool ruined = b.decay > 0f;

            Box(pal, house, rng, 0, 0, b.w, b.d, 0f, b.doorIndex, b.decay, b.roof);

            // 有些房子在其中一格加蓋二樓（天際線才不會一樣高）
            if (!ruined && b.w * b.d >= 4 && rng.NextDouble() < 0.3)
            {
                int cx = rng.Next(b.w), cz = rng.Next(b.d);
                Box(pal, house, rng, cx, cz, 1, 1, H, -1, 0f, true);
            }

            // 屋頂邊的陶罐 / 屋內的碎石（破房子）
            if (ruined)
            {
                int n = rng.Next(1, 4);
                for (int i = 0; i < n; i++)
                    Spawn(Pick(rng, pal.rubble), house, Local(house, (float)rng.NextDouble() * W, (float)rng.NextDouble() * D, 0f), house.rotation * Quaternion.Euler(0f, rng.Next(360), 0f));
            }
        }

        // 一個 w×d 格的方盒：四面牆（含門 / 窗 / 破損）、轉角柱、牆腳線條、簷口、屋頂
        static void Box(DSTWorldPalette pal, Transform house, System.Random rng, int x0, int z0, int w, int d, float y, int doorIndex, float decay, bool roof)
        {
            float X0 = x0 * 4f, Z0 = z0 * 4f, W = w * 4f, D = d * 4f;
            var starts = new[] { new Vector3(X0, 0, Z0), new Vector3(X0 + W, 0, Z0), new Vector3(X0 + W, 0, Z0 + D), new Vector3(X0, 0, Z0 + D) };
            var dirs = new[] { Vector3.right, Vector3.forward, Vector3.left, Vector3.back };
            var yaws = new[] { 0f, -90f, 180f, 90f };
            bool trims = decay < 0.3f;
            for (int e = 0; e < 4; e++)
            {
                int segs = e % 2 == 0 ? w : d;
                var rot = house.rotation * Quaternion.Euler(0f, yaws[e], 0f);
                if (decay == 0f || rng.NextDouble() > decay * 0.5f)
                {
                    Spawn(pal.wallCorner, house, Local(house, starts[e], y), rot);
                    if (trims)
                    {
                        Spawn(pal.corniceCorner, house, Local(house, starts[e], y + H), rot);
                        if (y == 0f) Spawn(pal.plinthCorner, house, Local(house, starts[e], y), rot);
                    }
                }
                for (int i = 0; i < segs; i++)
                {
                    Vector3 end = starts[e] + dirs[e] * (4f * (i + 1));
                    var at = Local(house, end, y);
                    double roll = rng.NextDouble();
                    GameObject piece;
                    if (e == 0 && i == doorIndex) piece = pal.wallGate;
                    else if (decay > 0f && roll < decay * 0.4) piece = null;
                    else if (decay > 0f && roll < decay * 0.85) piece = Pick(rng, pal.wallDamaged);
                    else if (rng.NextDouble() < 0.35) piece = Pick(rng, pal.wallWindow);
                    else piece = Pick(rng, pal.wallStraight);

                    if (piece == null)
                    {
                        Spawn(Pick(rng, pal.rubble), house, Local(house, end - dirs[e] * 2f, y), rot);
                        continue;
                    }
                    Spawn(piece, house, at, rot);
                    if (trims)
                    {
                        Spawn(pal.corniceStraight, house, Local(house, end, y + H), rot);
                        if (y == 0f) Spawn(pal.plinthStraight, house, at, rot);
                    }
                }
            }
            if (roof && pal.roofCap)
                for (int i = 0; i < w; i++)
                for (int j = 0; j < d; j++)
                    Spawn(pal.roofCap, house, Local(house, new Vector3(X0 + 4f * (i + 1), 0f, Z0 + 4f * (j + 1)), y + H), house.rotation);
        }

        static Vector3 Local(Transform house, Vector3 local, float y) => house.TransformPoint(new Vector3(local.x, y, local.z));
        static Vector3 Local(Transform house, float x, float z, float y) => house.TransformPoint(new Vector3(x, y, z));

        // ------------------------------------------------------------------ 街道
        // 貼在地面上的一層薄路面（夯實的土）：沿街道中線鋪條帶、廣場鋪一圈，邊緣用雜訊弄得不規則。
        // 每個點都取實際地面高度，延伸到聚落外的斜坡也貼得住。
        const float RoadLift = 0.04f;

        static void CreateStreets(DSTWorldGen gen, DSTSettlement s, Transform parent, Material mat)
        {
            var verts = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();
            var origin = parent.position;
            var rng = new System.Random(s.cellX * 31337 + s.cellZ);
            float J(float a) => ((float)rng.NextDouble() * 2f - 1f) * a;

            Vector3 P(Vector2 p) => new Vector3(p.x, gen.Height(p.x, p.y) + RoadLift, p.y) - origin;

            void Tri(int a, int b, int c)
            {
                // 正面朝上（Unity 順時針為正面；外積朝上表示順序正確）
                if (Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]).y < 0f) (b, c) = (c, b);
                tris.Add(a); tris.Add(b); tris.Add(c);
            }

            foreach (var st in s.streets)
            {
                Vector2 u = (st.b - st.a).normalized, n = new Vector2(-u.y, u.x);
                float len = Vector2.Distance(st.a, st.b);
                int steps = Mathf.Max(2, Mathf.CeilToInt(len / 1.5f));
                int start = verts.Count;
                for (int i = 0; i <= steps; i++)
                {
                    var c = st.a + u * (len * i / steps);
                    // 路的盡頭收窄一點，不要像被刀切斷
                    float taper = i == steps ? 0.55f : 1f;
                    verts.Add(P(c - n * (st.halfWidth + J(0.35f)) * taper));
                    verts.Add(P(c + n * (st.halfWidth + J(0.35f)) * taper));
                }
                for (int i = 0; i < steps; i++)
                {
                    int l0 = start + i * 2, r0 = l0 + 1, l1 = l0 + 2, r1 = l0 + 3;
                    Tri(l0, l1, r0);
                    Tri(r0, l1, r1);
                }
            }

            // 廣場：一圈（有水池的話中間留空）
            int seg = 36;
            float inner = s.pondRadius > 0f ? s.pondRadius + 0.6f : 0f;
            int ring0 = verts.Count;
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                verts.Add(P(s.center + d * (s.plazaRadius + J(0.5f))));
                verts.Add(inner > 0f ? P(s.center + d * inner) : P(s.center));
            }
            for (int i = 0; i < seg; i++)
            {
                int o0 = ring0 + i * 2, i0 = o0 + 1, o1 = ring0 + ((i + 1) % seg) * 2, i1 = o1 + 1;
                Tri(o0, o1, i0);
                if (inner > 0f) Tri(i0, o1, i1);
            }

            var mesh = new Mesh { name = "Streets", indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            var normals = new Vector3[verts.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            mesh.normals = normals;
            mesh.RecalculateBounds();
            var go = new GameObject("Streets");
            go.transform.SetParent(parent, false);
            go.transform.position = origin;
            go.AddComponent<DSTOwnedMesh>().mesh = mesh;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            DSTOwnedMesh.OptOutGpuDriven(mr);
        }

        // ------------------------------------------------------------------ 綠洲水池
        static void CreatePond(Transform parent, float radius, float y, Material water)
        {
            const int seg = 40;
            var v = new Vector3[seg + 1];
            var n = new Vector3[seg + 1];
            var t = new int[seg * 3];
            v[0] = Vector3.zero;
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                v[i + 1] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
            }
            for (int i = 0; i <= seg; i++) n[i] = Vector3.up;
            for (int i = 0; i < seg; i++) { t[i * 3] = 0; t[i * 3 + 1] = (i + 1) % seg + 1; t[i * 3 + 2] = i + 1; }
            var mesh = new Mesh { name = "Pond", vertices = v, normals = n, triangles = t };
            mesh.RecalculateBounds();
            var go = new GameObject("Pond");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(parent.position.x, y, parent.position.z);
            go.AddComponent<DSTOwnedMesh>().mesh = mesh;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = water;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            DSTOwnedMesh.OptOutGpuDriven(mr);
        }

        static T Pick<T>(System.Random rng, T[] a) where T : class => a == null || a.Length == 0 ? null : a[rng.Next(a.Length)];

        static GameObject Spawn(GameObject prefab, Transform parent, Vector3 pos, Quaternion rot)
        {
            if (!prefab) return null;
            var go = Object.Instantiate(prefab, pos, rot, parent);
            return go;
        }
    }
}
