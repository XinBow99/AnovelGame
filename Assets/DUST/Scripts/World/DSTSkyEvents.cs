using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.World
{
    // 流星雨（發條沙漠，見 DSTClockwork）：每一顆流星的時間、方向都由時鐘算出來，所有人同時看到同一顆。
    // 流星雨那晚有一顆落地：先是一道往地平線墜落的火光，之後隕石在原地發光、冒著熱氣的光柱，到隔天中午冷掉。
    public class DSTSkyEvents : MonoBehaviour
    {
        public Material streakMaterial;            // M_DST_Meteor（HDRP/Unlit 透明、不受霧影響）
        public Material meteoriteMaterial;         // M_DST_Meteorite（HDRP/Lit 發光）
        public Material scorchMaterial;            // M_DST_Scorch（HDRP/Decal）
        public float skyDistance = 900f;
        public float beaconHeight = 60f;

        class Streak { public LineRenderer line; public DSTClockwork.Meteor m; public bool fall; }
        readonly List<Streak> _streaks = new List<Streak>();
        readonly Stack<LineRenderer> _pool = new Stack<LineRenderer>();
        readonly HashSet<long> _shown = new HashSet<long>();
        long _lastSlot = long.MinValue;
        MaterialPropertyBlock _mpb;
        static readonly int ColorId = Shader.PropertyToID("_UnlitColor");

        GameObject _rock;
        Light _glow;
        LineRenderer _beacon;
        DecalProjector _scorch;
        Material _rockMat;
        double _fallShownFor = double.NaN;

        void Update()
        {
            var cam = Camera.main;
            if (!cam || !streakMaterial) return;
            _mpb ??= new MaterialPropertyBlock();
            double now = DSTClockwork.Now;
            float sph = DSTTimeOfDay.Instance ? DSTTimeOfDay.Instance.SecondsPerHour : 50f;

            // ---- 這一格和前一格（流星會跨格）裡的流星
            long slot = (long)System.Math.Floor(now / DSTClockwork.SlotHours);
            if (slot != _lastSlot)
            {
                for (long s = slot - 1; s <= slot; s++)
                    if (!_shown.Contains(s) && DSTClockwork.MeteorIn(s, out var m)) { _shown.Add(s); Spawn(m, false); }
                if (_shown.Count > 64) _shown.Clear();
                _lastSlot = slot;
            }

            // ---- 會落地的那一顆：落地前幾秒劃出一道往地平線的火光
            int day = DSTClockwork.DayOf(now);
            for (int d = day - 1; d <= day; d++)
            {
                if (!DSTClockwork.Meteorite(d, out double land, out var where)) continue;
                double lead = 2.5 / sph;
                if (now >= land - lead && now < land && !_fallShownFor.Equals(land))
                {
                    _fallShownFor = land;
                    var p = cam.transform.position;
                    var to = new Vector3(where.x - p.x, 0f, where.y - p.y);
                    var fm = new DSTClockwork.Meteor
                    {
                        slot = -1, start = land - lead, seconds = 2.5f, brightness = 3f, length = 40f,
                        azimuth = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, elevation = 38f, heading = 180f,
                    };
                    Spawn(fm, true);
                }
            }

            // ---- 畫：頭往前跑、尾巴跟著，亮度先升後降
            for (int i = _streaks.Count - 1; i >= 0; i--)
            {
                var st = _streaks[i];
                float t = (float)((now - st.m.start) * sph / st.m.seconds);
                if (t > 1.15f) { Release(st); _streaks.RemoveAt(i); continue; }
                if (t < 0f) { st.line.enabled = false; continue; }
                st.line.enabled = true;
                var a = SkyPoint(cam, st, Mathf.Clamp01(t));
                var b = SkyPoint(cam, st, Mathf.Clamp01(t - 0.45f));
                st.line.SetPosition(0, b);
                st.line.SetPosition(1, a);
                float k = Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI) * st.m.brightness * (1f - DSTWeather.Intensity);
                _mpb.SetColor(ColorId, new Color(0.85f, 0.92f, 1f) * (k * 6f));
                st.line.SetPropertyBlock(_mpb);
                float w = (st.fall ? 6f : 2.2f) * skyDistance / 900f;
                st.line.startWidth = w * 0.15f; st.line.endWidth = w;
            }

            UpdateMeteorite(cam, now);
        }

        // 流星在天球上的位置：從 (方位, 仰角) 出發，往 heading 方向劃過 length 度
        Vector3 SkyPoint(Camera cam, Streak st, float t)
        {
            var m = st.m;
            float el = m.elevation, az = m.azimuth;
            if (st.fall) el = Mathf.Lerp(m.elevation, -1f, t);                         // 往地平線掉
            else
            {
                float h = m.heading * Mathf.Deg2Rad, d = m.length * t;
                el += Mathf.Cos(h) * d * 0.5f - d * 0.35f;                             // 大多往下劃
                az += Mathf.Sin(h) * d / Mathf.Max(0.3f, Mathf.Cos(el * Mathf.Deg2Rad));
            }
            var dir = Quaternion.Euler(-el, az, 0f) * Vector3.forward;
            return cam.transform.position + dir * skyDistance;
        }

        void Spawn(DSTClockwork.Meteor m, bool fall)
        {
            var line = _pool.Count > 0 ? _pool.Pop() : NewLine();
            line.enabled = false;
            _streaks.Add(new Streak { line = line, m = m, fall = fall });
        }

        void Release(Streak st) { st.line.enabled = false; _pool.Push(st.line); }

        LineRenderer NewLine()
        {
            var go = new GameObject("Meteor");
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<LineRenderer>();
            l.positionCount = 2;
            l.useWorldSpace = true;
            l.material = streakMaterial;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.numCapVertices = 2;
            return l;
        }

        // ------------------------------------------------------------------ 隕石：發光的石頭 + 熱氣光柱（遠遠就看得到）+ 焦痕
        void UpdateMeteorite(Camera cam, double now)
        {
            var w = DSTWorld.Instance;
            Vector2 where = default;
            float heat = w && w.IsRandom && w.Gen != null ? DSTClockwork.MeteoriteHeat(now, out where, out _) : 0f;
            if (heat <= 0f)
            {
                if (_rock) _rock.SetActive(false);
                return;
            }
            if (!_rock) BuildMeteorite(w);
            var pos = new Vector3(where.x, w.Gen.Height(where.x, where.y), where.y);
            if (!_rock.activeSelf || (_rock.transform.position - pos).sqrMagnitude > 1f)
            {
                _rock.transform.position = pos;
                _rock.SetActive(true);
            }
            float dist = Vector3.Distance(cam.transform.position, pos);
            // 石頭本體只在附近才顯示（遠處靠光柱找）
            foreach (var r in _rock.GetComponentsInChildren<MeshRenderer>(true)) r.enabled = dist < 400f;
            float flicker = 0.85f + 0.15f * Mathf.PerlinNoise(Time.time * 2.3f, 0.7f);
            _glow.intensity = 2500f * heat * flicker;
            if (_beacon)
            {
                _beacon.SetPosition(0, pos);
                _beacon.SetPosition(1, pos + Vector3.up * beaconHeight * (0.4f + 0.6f * heat));
                float night = DSTTimeOfDay.Instance && DSTTimeOfDay.Instance.IsNight ? 1f : 0.25f;
                _mpb.SetColor(ColorId, new Color(1f, 0.45f, 0.15f) * (heat * night * 3f));
                _beacon.SetPropertyBlock(_mpb);
                float bw = Mathf.Clamp(dist * 0.008f, 0.8f, 6f);       // 遠一點也看得到
                _beacon.startWidth = bw; _beacon.endWidth = bw * 0.2f;
            }
            if (_rockMat) _rockMat.SetColor("_EmissiveColor", new Color(1f, 0.3f, 0.06f) * (heat * 40f));
        }

        void BuildMeteorite(DSTWorld w)
        {
            _rock = new GameObject("Meteorite");
            _rock.transform.SetParent(transform, false);
            var pal = w.palette;
            if (pal && pal.rocks != null && pal.rocks.Length > 0 && pal.rocks[0])
            {
                var body = Instantiate(pal.rocks[0], _rock.transform);
                body.transform.localScale = pal.rocks[0].transform.localScale * 0.45f;
                body.transform.localPosition = Vector3.down * 0.15f;
                body.transform.localRotation = Quaternion.Euler(20f, 37f, 11f);
                if (meteoriteMaterial)
                {
                    _rockMat = new Material(meteoriteMaterial);      // 每台電腦自己的一份（亮度隨溫度變，不要改到資產）
                    foreach (var r in body.GetComponentsInChildren<MeshRenderer>()) r.sharedMaterial = _rockMat;
                }
                foreach (var c in body.GetComponentsInChildren<Collider>()) c.enabled = true;
            }
            var lg = new GameObject("Glow");
            lg.transform.SetParent(_rock.transform, false);
            lg.transform.localPosition = Vector3.up * 0.8f;
            _glow = lg.AddComponent<Light>();
            _glow.type = LightType.Point;
            _glow.color = new Color(1f, 0.38f, 0.12f);
            _glow.range = 30f;
            _glow.shadows = LightShadows.None;
            _glow.lightUnit = UnityEngine.Rendering.LightUnit.Lumen;

            var bg = new GameObject("Beacon");
            bg.transform.SetParent(_rock.transform, false);
            _beacon = bg.AddComponent<LineRenderer>();
            _beacon.positionCount = 2;
            _beacon.useWorldSpace = true;
            _beacon.material = streakMaterial;
            _beacon.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            if (scorchMaterial)
            {
                var sg = new GameObject("Scorch");
                sg.transform.SetParent(_rock.transform, false);
                sg.transform.localPosition = Vector3.up * 1f;
                sg.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                _scorch = sg.AddComponent<DecalProjector>();
                _scorch.material = scorchMaterial;
                _scorch.size = new Vector3(7f, 7f, 3f);
                _scorch.pivot = new Vector3(0f, 0f, 1.5f);
            }
        }
    }
}
