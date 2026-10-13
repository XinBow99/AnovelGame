using System.Collections.Generic;
using UnityEngine;

namespace DUST.Animals
{
    // 依基因把身體組出來：每個會動的部位是一個 Transform（腿、脖子、頭、尾巴一節一節），底下掛程式產生的網格。
    // 不用蒙皮：關節處的網格互相重疊，轉動時看不出接縫。同一物種的網格 / 材質只做一次，全部個體共用。
    // 座標：+Z 前、+Y 上，原點在身體正下方的地面。
    public sealed class CreatureRig
    {
        public sealed class Leg
        {
            public Transform hip, knee, foot;
            public float upper, lower;
            public bool front;
            public float side;            // -1 左、+1 右
            public Vector3 restFoot;      // 站姿時腳掌的位置（Visual 座標）
        }

        public Transform root, body, neck, head;
        public readonly List<Transform> tail = new List<Transform>();
        public readonly Leg[] legs = new Leg[4];   // 0 左前、1 右前、2 左後、3 右後
        public Quaternion neckRest, headRest;
        public readonly List<Quaternion> tailRest = new List<Quaternion>();
        public Vector3 bodyRest;
        public Bounds bounds;
    }

    public static class DSTCreatureBuilder
    {
        const int Sides = 12;
        const int TailSegs = 5;

        sealed class Kit
        {
            public Mesh body, neck, head, upper, lower, foot, ear, eye, horn;
            public Mesh[] tailSegs;
            public Material coat, hoof, eye_, horn_;
        }
        static readonly Dictionary<int, Kit> _kits = new Dictionary<int, Kit>();

        public static CreatureRig Build(DSTGenome g, Transform parent, Material template)
        {
            var k = KitFor(g, template);
            var rig = new CreatureRig();
            rig.root = new GameObject("Visual").transform;
            rig.root.SetParent(parent, false);

            float L = g.bodyLength, R = g.bodyRadius, hip = g.hipHeight;
            rig.body = Part("Body", rig.root, new Vector3(0f, hip, 0f), k.body, k.coat);
            rig.bodyRest = rig.body.localPosition;

            // 脖子從胸口前上方往前上方伸；頭接在脖子末端
            rig.neck = Part("Neck", rig.body, new Vector3(0f, R * 0.45f, L * 0.4f), k.neck, k.coat);
            rig.neck.localRotation = rig.neckRest = Quaternion.Euler(-g.neckPitch, 0f, 0f);
            rig.head = Part("Head", rig.neck, new Vector3(0f, 0f, g.neckLength), k.head, k.coat);
            rig.head.localRotation = rig.headRest = Quaternion.Euler(g.neckPitch * 0.85f, 0f, 0f);   // 頭大致保持水平

            // 耳朵、眼睛、角
            float hr = g.headRadius;
            if (g.ears != EarShape.None)
                for (int s = -1; s <= 1; s += 2)
                {
                    var ear = Part("Ear", rig.head, new Vector3(s * hr * 0.55f, hr * 0.75f, g.headLength * 0.15f), k.ear, k.coat);
                    ear.localRotation = Quaternion.Euler(-15f, 0f, -s * (g.ears == EarShape.Long ? 12f : 25f));
                }
            for (int s = -1; s <= 1; s += 2)
                Part("Eye", rig.head, new Vector3(s * hr * 0.72f, hr * 0.3f, g.headLength * 0.42f), k.eye, k.eye_);
            if (g.horns)
                for (int s = -1; s <= 1; s += 2)
                {
                    var hornT = Part("Horn", rig.head, new Vector3(s * hr * 0.4f, hr * 0.85f, g.headLength * 0.3f), k.horn, k.horn_);
                    hornT.localRotation = Quaternion.Euler(-60f, s * 12f, -s * 10f);
                }

            // 四條腿（前腳在肩膀、後腳在臀部）
            for (int i = 0; i < 4; i++)
            {
                bool front = i < 2;
                float side = (i % 2 == 0) ? -1f : 1f;
                var leg = new CreatureRig.Leg { front = front, side = side, upper = g.upperLeg, lower = g.lowerLeg };
                var attach = new Vector3(side * R * 0.55f, -R * 0.35f, (front ? 0.32f : -0.32f) * L);
                leg.hip = new GameObject(front ? "ShoulderLeg" : "HipLeg").transform;
                leg.hip.SetParent(rig.body, false);
                leg.hip.localPosition = attach;
                Mesh(leg.hip, k.upper, k.coat);
                leg.knee = new GameObject("Knee").transform;
                leg.knee.SetParent(leg.hip, false);
                leg.knee.localPosition = new Vector3(0f, -g.upperLeg, 0f);
                Mesh(leg.knee, k.lower, k.coat);
                leg.foot = Part("Foot", leg.knee, new Vector3(0f, -g.lowerLeg, 0f), k.foot, k.hoof);
                leg.restFoot = new Vector3(attach.x, 0f, attach.z);
                rig.legs[i] = leg;
            }

            // 尾巴：一節一節，每節接在上一節末端
            float segLen = g.tailLength / TailSegs;
            Transform prev = rig.body;
            var at = new Vector3(0f, R * 0.25f, -L * 0.48f);
            for (int i = 0; i < TailSegs && g.tailLength > 0.01f; i++)
            {
                var t = Part("Tail" + i, prev, at, k.tailSegs[i], k.coat);
                // 尾巴沿 -Z：繞 X 正轉 = 往上翹。第一節決定翹起的角度，後面每節再往下彎一點（自然垂下）
                t.localRotation = Quaternion.Euler(i == 0 ? g.tailLift : -8f, 0f, 0f);
                rig.tail.Add(t);
                rig.tailRest.Add(t.localRotation);
                prev = t;
                at = new Vector3(0f, 0f, -segLen);
            }

            float h = hip + R + g.neckLength * Mathf.Sin(g.neckPitch * Mathf.Deg2Rad) + hr;
            rig.bounds = new Bounds(new Vector3(0f, h * 0.5f, L * 0.1f), new Vector3(R * 2.2f, h, L + g.headLength + g.neckLength * 0.6f));
            return rig;
        }

        static Transform Part(string name, Transform parent, Vector3 pos, Mesh mesh, Material mat)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
            Mesh(t, mesh, mat);
            return t;
        }

        static void Mesh(Transform t, Mesh mesh, Material mat)
        {
            t.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = t.gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
        }

        // ------------------------------------------------------------------ 每個物種一套網格 / 材質
        static Kit KitFor(DSTGenome g, Material template)
        {
            if (_kits.TryGetValue(g.key, out var k) && k.coat) return k;
            k = new Kit();
            float L = g.bodyLength, R = g.bodyRadius;

            // 身體：後端 → 前端，中間微微下垂，胸比臀厚一點
            k.body = Loft(9, t =>
            {
                float z = Mathf.Lerp(-L * 0.5f, L * 0.5f, t);
                float end = Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Lerp(0.04f, 0.96f, t)), 0.45f);
                float chest = Mathf.Lerp(0.88f, 1.08f, t);
                float sag = -Mathf.Sin(Mathf.PI * t) * R * 0.12f;
                return (new Vector3(0f, sag, z), new Vector2(R * end * chest, R * 1.12f * end * chest));
            }, Vector3.up);

            float nr = g.neckRadius;
            k.neck = Loft(5, t => (new Vector3(0f, 0f, Mathf.Lerp(-nr * 0.6f, g.neckLength + nr * 0.3f, t)),
                                   Vector2.one * nr * Mathf.Lerp(1.15f, 0.85f, t)), Vector3.up);

            float hr = g.headRadius, hl = g.headLength;
            k.head = Loft(8, t =>
            {
                float z = Mathf.Lerp(-hr * 0.7f, hl, t);
                float taper = Mathf.Lerp(1f, 1f - 0.62f * g.snout, Mathf.SmoothStep(0.25f, 1f, t));
                float cap = Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Lerp(0.06f, 0.94f, t)), 0.35f);
                return (new Vector3(0f, -t * hr * 0.25f, z), new Vector2(hr * 0.9f * taper * cap, hr * taper * cap));
            }, Vector3.up);

            float lr = g.legRadius;
            // 大腿從身體裡面長出來（上端很粗、藏在身體裡），兩端都是圓頭，關節轉動時看不到切面
            k.upper = Loft(8, t => (new Vector3(0f, Mathf.Lerp(R * 0.45f, -g.upperLeg - lr * 0.5f, t), 0f),
                                    Vector2.one * Mathf.Lerp(lr * 2.3f, lr, Mathf.SmoothStep(0f, 1f, t)) * Round(t, 0.35f)), Vector3.forward);
            k.lower = Loft(7, t => (new Vector3(0f, Mathf.Lerp(lr * 0.9f, -g.lowerLeg, t), 0f), Vector2.one * lr * Mathf.Lerp(1.05f, 0.7f, t) * Round(t, 0.3f)), Vector3.forward);
            k.foot = Loft(4, t => (new Vector3(0f, -lr * 0.3f, Mathf.Lerp(-lr * 0.8f, lr * 1.4f, t)),
                                   new Vector2(lr * 0.95f, lr * 0.55f) * Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Lerp(0.1f, 0.9f, t)), 0.5f)), Vector3.up);

            float seg = Mathf.Max(0.001f, g.tailLength / TailSegs), tr = g.tailRadius;
            k.tailSegs = new Mesh[TailSegs];
            for (int i = 0; i < TailSegs; i++)
            {
                float r0 = tr * Mathf.Lerp(1f, 0.35f, i / (float)TailSegs), r1 = tr * Mathf.Lerp(1f, 0.35f, (i + 1) / (float)TailSegs);
                k.tailSegs[i] = Loft(6, t => (new Vector3(0f, 0f, Mathf.Lerp(r0 * 0.9f, -seg - r1 * 0.9f, t)), Vector2.one * Mathf.Lerp(r0, r1, t) * Round(t, 0.3f)), Vector3.up);
            }

            float es = g.earSize;
            k.ear = g.ears switch
            {
                EarShape.Round => Loft(4, t => (new Vector3(0f, t * es, 0f), new Vector2(es * 0.45f, es * 0.12f) * Mathf.Sin(Mathf.PI * Mathf.Lerp(0.08f, 0.92f, t))), Vector3.forward),
                _ => Loft(4, t => (new Vector3(0f, t * es, 0f), new Vector2(es * 0.3f, es * 0.08f) * (1f - t * 0.92f)), Vector3.forward),
            };
            k.eye = Loft(4, t => (new Vector3(0f, 0f, Mathf.Lerp(-1f, 1f, t) * hr * 0.12f), Vector2.one * hr * 0.14f * Mathf.Sin(Mathf.PI * Mathf.Lerp(0.05f, 0.95f, t))), Vector3.up);
            float horn = g.hornLength, curl = g.hornCurl;
            k.horn = Loft(6, t =>
            {
                // 沿著弧線彎（curl 正 = 往後彎）
                float ang = curl * Mathf.Deg2Rad * t;
                var p = new Vector3(0f, 0f, 0f);
                p.z = curl == 0f ? t * horn : Mathf.Sin(ang) / (curl * Mathf.Deg2Rad) * horn;
                p.y = curl == 0f ? 0f : (1f - Mathf.Cos(ang)) / (curl * Mathf.Deg2Rad) * horn;
                return (p, Vector2.one * hr * 0.18f * (1f - t * 0.9f));
            }, Vector3.up);

            k.coat = new Material(template) { name = "M_Gen_Coat_" + g.key };
            k.coat.SetTexture("_BaseColorMap", CoatTexture(g));
            k.coat.SetColor("_BaseColor", Color.white);
            k.hoof = Solid(template, g.hoof, 0.35f);
            k.eye_ = Solid(template, new Color(0.03f, 0.025f, 0.02f), 0.85f);
            k.horn_ = Solid(template, Color.Lerp(g.belly, new Color(0.25f, 0.2f, 0.15f), 0.45f), 0.45f);
            _kits[g.key] = k;
            return k;
        }

        // 圓頭：兩端半徑收成半球（p 越小越接近圓柱、越大越尖）
        static float Round(float t, float p) => Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Lerp(0.02f, 0.98f, t)), p);

        static Material Solid(Material template, Color c, float smooth)
        {
            var m = new Material(template);
            m.SetTexture("_BaseColorMap", null);
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            return m;
        }

        // 毛色：u = 繞身體一圈（0.25 = 背、0.75 = 肚子），v = 沿著身體
        static Texture2D CoatTexture(DSTGenome g)
        {
            const int W = 64, H = 128;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = "T_Gen_Coat_" + g.key, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            var px = new Color[W * H];
            var r = new System.Random(g.key ^ 0x2c1b3c6d);
            float ox = (float)r.NextDouble() * 100f, oy = (float)r.NextDouble() * 100f;
            float sc = g.patternScale;
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float u = x / (float)W, v = y / (float)H;
                // 肚子顏色：u 在 0.75 附近
                float bellyW = Mathf.Clamp01(1f - Mathf.Abs(Mathf.DeltaAngle(u * 360f, 270f)) / 70f);
                var c = Color.Lerp(g.coat, g.belly, Mathf.SmoothStep(0f, 1f, bellyW));
                float back = Mathf.Clamp01(1f - Mathf.Abs(Mathf.DeltaAngle(u * 360f, 90f)) / 90f);   // 背上 1
                float m = 0f;
                switch (g.pattern)
                {
                    case Pattern.Stripes:
                        float wob = Mathf.PerlinNoise(ox + u * 3f, oy + v * 2f) * 0.6f;
                        m = Mathf.SmoothStep(0.55f, 0.75f, Mathf.Sin((v * 9f * sc + wob) * Mathf.PI * 2f) * 0.5f + 0.5f) * Mathf.SmoothStep(0.0f, 0.35f, back + 0.25f);
                        break;
                    case Pattern.Spots:
                        float n = Mathf.PerlinNoise(ox + u * 10f * sc, oy + v * 18f * sc);
                        m = Mathf.SmoothStep(0.62f, 0.7f, n) * Mathf.SmoothStep(0.0f, 0.5f, back + 0.2f);
                        break;
                    case Pattern.Saddle:
                        m = Mathf.SmoothStep(0.45f, 0.65f, back + (Mathf.PerlinNoise(ox + v * 4f, oy) - 0.5f) * 0.3f);
                        break;
                }
                c = Color.Lerp(c, g.mark, m * 0.85f);
                // 一點點雜色，看起來比較像毛
                float grain = (Mathf.PerlinNoise(ox + u * 40f, oy + v * 60f) - 0.5f) * 0.08f;
                c = new Color(Mathf.Clamp01(c.r + grain), Mathf.Clamp01(c.g + grain), Mathf.Clamp01(c.b + grain), 1f);
                px[y * W + x] = c;
            }
            tex.SetPixels(px);
            tex.Apply(true, true);
            return tex;
        }

        // ------------------------------------------------------------------ 網格：沿一條線放橢圓截面，頭尾封口
        static Mesh Loft(int rings, System.Func<float, (Vector3 c, Vector2 r)> f, Vector3 upHint)
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            var cs = new Vector3[rings];
            var rs = new Vector2[rings];
            for (int i = 0; i < rings; i++) { var s = f(i / (float)(rings - 1)); cs[i] = s.c; rs[i] = s.r; }

            for (int i = 0; i < rings; i++)
            {
                var fwd = (cs[Mathf.Min(i + 1, rings - 1)] - cs[Mathf.Max(i - 1, 0)]).normalized;
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
                var up = Vector3.ProjectOnPlane(upHint, fwd);
                if (up.sqrMagnitude < 1e-6f) up = Vector3.ProjectOnPlane(Vector3.right, fwd);
                up.Normalize();
                var right = Vector3.Cross(up, fwd).normalized;
                for (int s = 0; s <= Sides; s++)
                {
                    float a = s / (float)Sides * Mathf.PI * 2f;
                    float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                    verts.Add(cs[i] + right * ca * rs[i].x + up * sa * rs[i].y);
                    norms.Add((right * ca / Mathf.Max(1e-4f, rs[i].x) + up * sa / Mathf.Max(1e-4f, rs[i].y)).normalized);
                    uvs.Add(new Vector2(s / (float)Sides, i / (float)(rings - 1)));
                }
            }
            int row = Sides + 1;
            for (int i = 0; i < rings - 1; i++)
            for (int s = 0; s < Sides; s++)
            {
                int a = i * row + s, b = a + 1, c = a + row, d = c + 1;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
            // 封口
            Cap(verts, norms, uvs, tris, cs[0], (cs[0] - cs[1]).normalized, 0, row, false);
            Cap(verts, norms, uvs, tris, cs[rings - 1], (cs[rings - 1] - cs[rings - 2]).normalized, (rings - 1) * row, row, true);

            var m = new Mesh { name = "Gen" };
            m.SetVertices(verts);
            m.SetNormals(norms);
            m.SetUVs(0, uvs);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            m.RecalculateTangents();
            return m;
        }

        static void Cap(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t, Vector3 c, Vector3 dir, int start, int row, bool end)
        {
            int ci = v.Count;
            v.Add(c); n.Add(dir); uv.Add(new Vector2(0.5f, end ? 1f : 0f));
            for (int s = 0; s < row - 1; s++)
            {
                if (end) { t.Add(start + s); t.Add(start + s + 1); t.Add(ci); }
                else { t.Add(start + s + 1); t.Add(start + s); t.Add(ci); }
            }
        }
    }
}
