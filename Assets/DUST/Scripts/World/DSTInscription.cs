using System.Collections.Generic;
using DUST.Animals;
using DUST.Player;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.World
{
    // 遺跡刻文（發條沙漠，見 DSTClockwork）：每處遺跡外有一塊石碑，碑面的刻痕平常看不出來，
    // 只有在每天固定的時刻、陽光斜斜掃過碑面（入射角約 82°）時才浮現，持續大約一遊戲小時。
    // 碑面朝向是倒推出來的：先決定「幾點」，再把碑面轉到那個時刻光剛好擦過的角度。
    // 讀得到的時候站近一點，刻文會告訴你接下來會發生的事（流星落點、沙暴、哪種動物在哪聚集）。
    public class DSTInscription : MonoBehaviour
    {
        public float revealHour;
        public int salt;
        DecalProjector _decal;
        float _next, _reveal;
        int _readDay = -1;

        const float Grazing = 0.14f;        // cos(82°)
        const float Band = 0.085f;
        const float SlabW = 1.1f, SlabH = 2.1f, SlabT = 0.38f;

        // ------------------------------------------------------------------ 建造（DSTWorldScatter.BuildRuins 呼叫，只用遺跡自己的雜湊，不動原本的佈局）
        public static void Build(DSTWorldGen gen, DSTWorldPalette pal, DSTWorldGen.Site s, Transform parent)
        {
            if (!pal || !pal.inscription) return;
            int salt = (int)DSTNoise.Hash(gen.Seed, s.cellX, s.cellZ, 8100);
            float H(int i) => DSTNoise.Hash01(salt, i, 0, 8101);

            // 早上 6:00~8:30 或傍晚 16:30~18:45：太陽低，光才擦得過碑面
            float hour = H(0) < 0.5f ? 6f + H(1) * 2.5f : 16.5f + H(1) * 2.25f;
            float faceYaw = FaceYawFor(hour, H(2) < 0.5f ? 1f : -1f);

            float a = H(3) * Mathf.PI * 2f, r = s.radius + 2.5f;
            float x = s.center.x + Mathf.Cos(a) * r, z = s.center.y + Mathf.Sin(a) * r;
            var go = new GameObject("Stele");
            go.transform.SetParent(parent, true);
            go.transform.SetPositionAndRotation(new Vector3(x, gen.Height(x, z) - 0.25f, z), Quaternion.Euler(0f, faceYaw, 0f));
            go.AddComponent<MeshFilter>().sharedMesh = SlabMesh();
            go.AddComponent<MeshRenderer>().sharedMaterial = pal.rock;
            var col = go.AddComponent<BoxCollider>();
            col.center = new Vector3(0f, SlabH * 0.5f, 0f);
            col.size = new Vector3(SlabW, SlabH, SlabT);

            var dg = new GameObject("Carving");
            dg.transform.SetParent(go.transform, false);
            dg.transform.localPosition = new Vector3(0f, SlabH * 0.55f, SlabT * 0.5f + 0.15f);
            dg.transform.localRotation = Quaternion.LookRotation(Vector3.back, Vector3.up);   // 往碑面裡投影
            var d = dg.AddComponent<DecalProjector>();
            d.material = pal.inscription;
            d.size = new Vector3(SlabW * 0.78f, SlabH * 0.7f, 0.4f);
            d.pivot = new Vector3(0f, 0f, 0.2f);
            int panel = (int)(H(4) * 8f) % 8;
            d.uvScale = new Vector2(0.25f, 0.5f);
            d.uvBias = new Vector2(panel % 4 * 0.25f, panel / 4 * 0.5f);
            d.fadeFactor = 0f;
            d.drawDistance = 60f;

            var ins = go.AddComponent<DSTInscription>();
            ins.revealHour = hour;
            ins.salt = salt;
            ins._decal = d;
        }

        /// <summary>那個鐘點，碑面要朝哪個方向，陽光才會剛好以約 82° 擦過</summary>
        static float FaceYawFor(float hour, float side)
        {
            float elev, yaw;
            var tod = DSTTimeOfDay.Instance;
            if (tod) tod.SunAngles(hour, out elev, out yaw);
            else { elev = 20f; yaw = 219f + (hour - 12.2f) * 12.5f; }
            float toSun = yaw + 180f;                                         // 光往 yaw 照，太陽在反方向
            float c = Mathf.Clamp(Grazing / Mathf.Max(0.2f, Mathf.Cos(elev * Mathf.Deg2Rad)), -1f, 1f);
            return toSun + side * Mathf.Acos(c) * Mathf.Rad2Deg;
        }

        static Mesh _slab;
        static Mesh SlabMesh()
        {
            if (_slab) return _slab;
            // 微微往上收的石板（頂部窄一點），每面各自的頂點（硬邊）
            float w = SlabW * 0.5f, t = SlabT * 0.5f, top = 0.82f;
            var c = new[]
            {
                new Vector3(-w, 0, -t), new Vector3(w, 0, -t), new Vector3(w, 0, t), new Vector3(-w, 0, t),
                new Vector3(-w * top, SlabH, -t * 0.9f), new Vector3(w * top, SlabH, -t * 0.9f), new Vector3(w * top, SlabH, t * 0.9f), new Vector3(-w * top, SlabH, t * 0.9f),
            };
            int[][] faces = { new[] { 3, 2, 6, 7 }, new[] { 1, 0, 4, 5 }, new[] { 2, 1, 5, 6 }, new[] { 0, 3, 7, 4 }, new[] { 4, 7, 6, 5 } };
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            foreach (var f in faces)
            {
                int b = v.Count;
                for (int i = 0; i < 4; i++) v.Add(c[f[i]]);
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(0, 1));
                tri.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }
            _slab = new Mesh { name = "SM_Stele" };
            _slab.SetVertices(v); _slab.SetUVs(0, uv); _slab.SetTriangles(tri, 0);
            _slab.RecalculateNormals(); _slab.RecalculateTangents(); _slab.RecalculateBounds();
            return _slab;
        }

        // ------------------------------------------------------------------ 浮現
        /// <summary>現在的光有多「斜」：0 = 看不到，1 = 刻痕最清楚</summary>
        public float Reveal()
        {
            var tod = DSTTimeOfDay.Instance;
            if (!tod) return 0f;
            tod.SunAngles(tod.Hour, out float elev, out float yaw);
            if (elev < 1f) return 0f;
            var toSun = -(Quaternion.Euler(elev, yaw, 0f) * Vector3.forward);
            float cos = Vector3.Dot(transform.forward, toSun);
            float k = 1f - Mathf.Abs(cos - Grazing) / Band;
            if (k <= 0f) return 0f;
            k = k * k * (3f - 2f * k);
            return k * Mathf.Clamp01(elev / 4f) * (1f - DSTWeather.Intensity);   // 沙暴遮住太陽就看不到
        }

        void Update()
        {
            if (!_decal || Time.time < _next) return;
            _next = Time.time + 0.2f;
            _reveal = Mathf.MoveTowards(_reveal, Reveal(), 0.08f);
            _decal.fadeFactor = _reveal;
            _decal.enabled = _reveal > 0.01f;

            // 讀得到、而且有人站在碑前：念出來（每天一次）
            var me = DSTNetPlayer.Local;
            if (_reveal < 0.55f || !me) return;
            var to = me.transform.position - transform.position;
            if (to.sqrMagnitude > 6f * 6f || Vector3.Dot(to, transform.forward) < 0f) return;
            int day = DSTClockwork.DayOf(DSTClockwork.Now);
            if (day == _readDay) return;
            _readDay = day;
            DUST.UI.DSTSpeechBubbles.Show(transform, SlabH + 0.4f, "刻文", Clue(), new Color(0.95f, 0.82f, 0.55f), 9f);
        }

        // ------------------------------------------------------------------ 刻文的內容：接下來會發生的事（全部由時鐘算出來）
        public string Clue()
        {
            double now = DSTClockwork.Now;
            int today = DSTClockwork.DayOf(now);
            int pick = (int)(DSTNoise.Hash(salt, today, 0, 8102) % 3u);
            for (int tries = 0; tries < 3; tries++, pick = (pick + 1) % 3)
            {
                string s = pick switch { 0 => MeteorClue(today), 1 => StormClue(now, today), _ => GatherClue(now, today) };
                if (s != null) return s;
            }
            return "沙與星，都照著同一座鐘走。";
        }

        string MeteorClue(int today)
        {
            for (int d = today; d < today + 10; d++)
            {
                if (!DSTClockwork.Meteorite(d, out double land, out var where) || land < DSTClockwork.Now) continue;
                var to = new Vector3(where.x, 0f, where.y) - transform.position;
                return $"第 {d + 1} 夜，{Hm(land)}，一顆星墜於{Compass(to)}方，約 {Round(to)} 公尺。";
            }
            return null;
        }

        string StormClue(double now, int today)
        {
            for (int d = today; d < today + 8; d++)
            {
                if (!DSTClockwork.StormOn(d, out float start, out float dur) || d * 24.0 + start < now) continue;
                return $"第 {d + 1} 天 {Hm(d * 24.0 + start)}，風沙吞沒天空，萬獸歸巢。";
            }
            return null;
        }

        string GatherClue(double now, int today)
        {
            var bands = new List<DSTBand>();
            DSTMigration.Near(transform.position, 250f, bands);
            foreach (var b in bands)
                for (int d = today; d < today + 8; d++)
                {
                    var path = b.MassPath(d);
                    if (path == null) continue;
                    var g = path.PointAt(path.Turn, out _);
                    var to = g - transform.position;
                    return $"第 {d + 1} 天，{b.genome.name}將聚於{Compass(to)}方，約 {Round(to)} 公尺。";
                }
            return null;
        }

        static string Hm(double t)
        {
            double h = t - System.Math.Floor(t / 24.0) * 24.0;
            int hh = (int)h, mm = (int)((h - hh) * 60.0);
            return $"{hh:00}:{mm / 10 * 10:00}";
        }

        static int Round(Vector3 to) => Mathf.RoundToInt(new Vector2(to.x, to.z).magnitude / 10f) * 10;

        static string Compass(Vector3 to)
        {
            string[] n = { "北", "東北", "東", "東南", "南", "西南", "西", "西北" };
            float deg = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            return n[Mathf.RoundToInt(Mathf.Repeat(deg, 360f) / 45f) % 8];
        }
    }
}
