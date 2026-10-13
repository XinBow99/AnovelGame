using UnityEngine;

namespace DUST.World
{
    // 每個區塊上的擺設：石塊、沙堆、廢車，還有遺跡（用遺跡二的 4 m 牆模組拼出倒塌的房子）。
    // 亂數只由「種子 + 區塊座標」決定，所以每台電腦、每次重新載入都長得一樣。
    public static class DSTWorldScatter
    {
        // jobs：聚落物件很多，拆成好幾幀慢慢蓋（呼叫的人用 StartCoroutine 跑）
        public static GameObject Build(DSTWorldGen gen, DSTWorldPalette pal, Vector2Int c, Transform parent, System.Collections.Generic.List<System.Collections.IEnumerator> jobs = null)
        {
            var root = new GameObject("Scatter");
            root.transform.SetParent(parent, false);
            root.transform.position = Vector3.zero;
            var rng = new System.Random((int)DSTNoise.Hash(gen.Seed, c.x, c.y, 77));
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            Vector3 origin = DSTWorldChunk.Origin(c);

            // 台地附近石頭比較多
            gen.Sample(origin.x + 32f, origin.z + 32f, out float rockHere);
            int big = Mathf.RoundToInt(rockHere * R(3f, 8f) + R(-0.6f, 1.6f));
            int small = Mathf.RoundToInt(R(1f, 5f) + rockHere * 4f);

            for (int i = 0; i < big; i++)
                Place(rockHere > 0.3f ? pal.rocks : pal.smallRocks, R(0f, 64f), R(0f, 64f), R(0.8f, 2.4f), 0.25f);   // 石柱只長在台地附近
            for (int i = 0; i < small; i++)
                Place(pal.smallRocks != null && pal.smallRocks.Length > 0 ? pal.smallRocks : pal.rocks, R(0f, 64f), R(0f, 64f), R(0.25f, 0.6f), 0.08f);
            if (R(0f, 1f) < 0.035f) Place(new[] { pal.wreck }, R(8f, 56f), R(8f, 56f), 1f, 0.15f, alignToGround: true);

            // 遺跡：中心點落在這個區塊才蓋（避免相鄰區塊重複）
            int cx = Mathf.FloorToInt(origin.x / DSTWorldGen.SiteCell), cz = Mathf.FloorToInt(origin.z / DSTWorldGen.SiteCell);
            for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                var s = gen.SiteAt(cx + i, cz + j);
                if (!s.exists) continue;
                if (Mathf.FloorToInt(s.center.x / DSTWorldChunk.Size) != c.x || Mathf.FloorToInt(s.center.y / DSTWorldChunk.Size) != c.y) continue;
                BuildRuins(gen, pal, s, root.transform);
            }

            // 聚落：同樣只在中心點所在的區塊蓋
            var settlement = gen.SettlementCenteredIn(c);
            if (settlement != null && jobs != null) jobs.Add(DSTSettlementBuilder.Build(gen, pal, settlement, root.transform));
            return root;

            void Place(GameObject[] set, float lx, float lz, float scale, float sink, bool alignToGround = false)
            {
                if (set == null || set.Length == 0) return;
                var prefab = set[rng.Next(set.Length)];
                float yaw = R(0f, 360f), tiltX = R(-12f, 12f), tiltZ = R(-12f, 12f);
                if (!prefab) return;
                float x = origin.x + lx, z = origin.z + lz;
                if (x * x + z * z < 28f * 28f) return;             // 出生點留空
                if (gen.SettlementContaining(x, z, 4f) != null) return;   // 聚落裡不放野外的石頭
                if (InSite(gen, x, z, 6f)) return;                 // 遺跡裡面不放大石頭
                float y = gen.Height(x, z) - sink * scale;
                var rot = alignToGround
                    ? Quaternion.FromToRotation(Vector3.up, gen.Normal(x, z, 1.5f)) * Quaternion.Euler(0f, yaw, 0f)
                    : Quaternion.Euler(tiltX, yaw, tiltZ);
                var go = Object.Instantiate(prefab, new Vector3(x, y, z), rot, root.transform);
                go.transform.localScale = prefab.transform.localScale * scale;
            }
        }

        static bool InSite(DSTWorldGen gen, float x, float z, float margin)
        {
            int cx = Mathf.FloorToInt(x / DSTWorldGen.SiteCell), cz = Mathf.FloorToInt(z / DSTWorldGen.SiteCell);
            for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                var s = gen.SiteAt(cx + i, cz + j);
                if (s.exists && Vector2.Distance(new Vector2(x, z), s.center) < s.radius + margin) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ 遺跡
        // 1~3 間倒塌的房子：沿著外圍每 4 m 一段牆（完整 / 破損 / 拱窗 / 半截 / 整段塌掉），四角放轉角柱，
        // 牆腳積沙、屋內有碎石和陶罐。
        static void BuildRuins(DSTWorldGen gen, DSTWorldPalette pal, DSTWorldGen.Site s, Transform parent)
        {
            var rng = new System.Random((int)DSTNoise.Hash(gen.Seed, s.cellX, s.cellZ, 99));
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            T Pick<T>(T[] a) => a == null || a.Length == 0 ? default : a[rng.Next(a.Length)];

            var site = new GameObject("Ruins").transform;
            site.SetParent(parent, false);
            float baseY = s.height - 0.12f;
            site.position = new Vector3(s.center.x, baseY, s.center.y);
            site.rotation = Quaternion.Euler(0f, R(0f, 360f), 0f);

            int houses = 1 + rng.Next(3);
            float spread = Mathf.Max(0f, s.radius - 12f);
            for (int hIdx = 0; hIdx < houses; hIdx++)
            {
                int w = 2 + rng.Next(3), d = 2 + rng.Next(2);
                var house = new GameObject("House").transform;
                house.SetParent(site, false);
                house.localPosition = new Vector3(R(-spread, spread) - w * 2f, 0f, R(-spread, spread) - d * 2f);
                house.localRotation = Quaternion.Euler(0f, rng.Next(4) * 90f + R(-8f, 8f), 0f);
                // 太陡的地方不放（被沙丘擋到）
                float decay = R(0.25f, 0.6f);                     // 坍塌程度
                int gateEdge = rng.Next(4), gateIndex = -1;

                // 四邊：南(+x)、東(+z)、北(-x)、西(-z)；每邊從起點往前、牆的外側在左手邊外
                var starts = new[] { new Vector3(0, 0, 0), new Vector3(w * 4, 0, 0), new Vector3(w * 4, 0, d * 4), new Vector3(0, 0, d * 4) };
                var dirs = new[] { Vector3.right, Vector3.forward, Vector3.left, Vector3.back };
                var yaws = new[] { 0f, -90f, 180f, 90f };
                for (int e = 0; e < 4; e++)
                {
                    int segs = (e % 2 == 0) ? w : d;
                    if (e == gateEdge) gateIndex = rng.Next(segs);
                    var rot = Quaternion.Euler(0f, yaws[e], 0f);
                    // 轉角柱
                    if (R(0f, 1f) > decay * 0.5f) Spawn(pal.wallCorner, house, starts[e], rot);
                    for (int i = 0; i < segs; i++)
                    {
                        Vector3 end = starts[e] + dirs[e] * (4f * (i + 1));
                        float roll = R(0f, 1f);
                        GameObject piece;
                        if (e == gateEdge && i == gateIndex) piece = pal.wallGate;
                        else if (roll < decay * 0.45f) piece = null;                         // 整段塌掉
                        else if (roll < decay * 0.9f) piece = Pick(pal.wallDamaged);
                        else if (roll < decay * 0.9f + 0.15f) piece = Pick(pal.wallWindow);
                        else piece = Pick(pal.wallStraight);

                        if (piece == null)
                        {
                            // 塌掉的地方留一堆碎石，有時候剩半截牆
                            if (R(0f, 1f) < 0.5f && pal.wallHalf != null && pal.wallHalf.Length > 0)
                                Spawn(Pick(pal.wallHalf), house, end - dirs[e] * 2f, rot);
                            Spawn(Pick(pal.rubble), house, end - dirs[e] * 2f + Quaternion.Euler(0f, yaws[e], 0f) * new Vector3(0f, 0f, R(-1.5f, 0.5f)), Quaternion.Euler(0f, R(0f, 360f), 0f));
                        }
                        else Spawn(piece, house, end, rot);

                        // 外牆腳積沙
                        if (R(0f, 1f) < 0.45f)
                            Spawn(Pick(pal.drifts), house, end - dirs[e] * R(0.3f, 1.5f) + rot * new Vector3(0f, 0f, -0.5f), rot * Quaternion.Euler(0f, 180f, 0f), R(0.8f, 1.4f));
                    }
                }

                // 屋內雜物
                int clutter = rng.Next(1, 4);
                for (int i = 0; i < clutter; i++)
                {
                    var p = new Vector3(R(1f, w * 4 - 1f), 0f, R(1f, d * 4 - 1f));
                    float k = R(0f, 1f);
                    var prefab = k < 0.4f ? Pick(pal.rubble) : k < 0.75f ? Pick(pal.pots) : k < 0.9f ? pal.brokenCrate : Pick(pal.drifts);
                    Spawn(prefab, house, p, Quaternion.Euler(0f, R(0f, 360f), 0f), R(0.8f, 1.2f));
                }
            }

            // 遺跡周圍的石頭和沙堆
            int extra = rng.Next(3, 8);
            for (int i = 0; i < extra; i++)
            {
                float a = R(0f, Mathf.PI * 2f), r = R(s.radius * 0.6f, s.radius + 4f);
                var p = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                Spawn(R(0f, 1f) < 0.5f ? Pick(pal.rocks) : Pick(pal.drifts), site, p, Quaternion.Euler(0f, R(0f, 360f), 0f), R(0.6f, 1.6f));
            }

            // 平地邊緣是斜坡，每個物件貼著實際地面高度
            foreach (Transform t in site.GetComponentsInChildren<Transform>())
            {
                if (t == site || t.name == "House") continue;
                if (t.parent != site && t.parent.name != "House") continue;
                var wp = t.position;
                wp.y = gen.Height(wp.x, wp.z) - 0.12f;
                t.position = wp;
            }

            // 發條沙漠：遺跡外的石碑，只在每天固定時刻的斜光下看得到刻文
            DSTInscription.Build(gen, pal, s, site);

            void Spawn(GameObject prefab, Transform under, Vector3 local, Quaternion rot, float scale = 1f)
            {
                if (!prefab) return;
                var go = Object.Instantiate(prefab, under);
                go.transform.localPosition = local;
                go.transform.localRotation = rot;
                go.transform.localScale = prefab.transform.localScale * scale;
            }
        }
    }
}
