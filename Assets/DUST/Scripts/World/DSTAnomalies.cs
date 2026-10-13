using System.Collections.Generic;
using DUST.Player;
using UnityEngine;

namespace DUST.World
{
    // 異象的畫面與聲音（資料見 DSTAnomaly）：乾雷、落下的怪東西、鳴沙、幻日，和開始時的「快報」。
    // 顏色霧與太陽亮度由 DSTWeather 一起處理。全部由時鐘算出來，每台電腦自己畫。
    public class DSTAnomalies : MonoBehaviour
    {
        public Material glowMaterial;              // M_DST_Glow（HDRP/Unlit 加法、不受霧影響、整條一樣亮）
        public Material dustMaterial;              // M_DST_StormDust（落下的雨／灰／細晶，複製一份改顏色）
        public float skyDistance = 900f;

        // ---- 乾雷
        class Strike { public LineRenderer line; public DSTAnomaly.Bolt b; public Vector3 ground; public bool thundered; }
        readonly List<Strike> _strikes = new List<Strike>();
        readonly Stack<LineRenderer> _pool = new Stack<LineRenderer>();
        readonly HashSet<long> _shown = new HashSet<long>();
        long _lastSlot = long.MinValue;
        Light _flashLight;
        AudioClip _thunder;
        readonly List<AudioSource> _thunderSources = new List<AudioSource>();

        // ---- 其他
        ParticleSystem _fall;
        ParticleSystemRenderer _fallRenderer;
        Material _fallMat;
        int _fallKind = -1;
        AudioSource _hum;
        LineRenderer _halo, _dogL, _dogR;
        MaterialPropertyBlock _mpb;
        static readonly int ColorId = Shader.PropertyToID("_UnlitColor");

        // ---- 快報
        string _bulletin;
        float _bulletinUntil;
        double _announced = double.NaN;
        GUIStyle _big, _small;

        public static float Intensity { get; private set; }

        void Start()
        {
            _mpb = new MaterialPropertyBlock();
            _thunder = ThunderClip();
            _hum = gameObject.AddComponent<AudioSource>();
            _hum.clip = HumClip();
            _hum.loop = true; _hum.spatialBlend = 0f; _hum.volume = 0f; _hum.playOnAwake = false;
            var lg = new GameObject("BoltFlash");
            lg.transform.SetParent(transform, false);
            _flashLight = lg.AddComponent<Light>();
            _flashLight.type = LightType.Point;
            _flashLight.color = new Color(0.8f, 0.85f, 1f);
            _flashLight.shadows = LightShadows.None;
            _flashLight.lightUnit = UnityEngine.Rendering.LightUnit.Lumen;
            _flashLight.enabled = false;
            if (glowMaterial)
            {
                _halo = NewLine("Halo", 73); _halo.loop = false;
                _dogL = NewLine("SundogL", 2); _dogR = NewLine("SundogR", 2);
            }
        }

        void Update()
        {
            var cam = Camera.main;
            double now = DSTClockwork.Now;
            float k = DSTAnomaly.Current(now, out var e);
            Intensity = k;
            DSTWeather.Flash = Mathf.MoveTowards(DSTWeather.Flash, 0f, Time.deltaTime * 6f);

            Announce(now, k, e);
            if (!cam) return;
            UpdateBolts(cam, now);
            UpdateFall(cam, k, e);
            UpdateHum(k, e);
            UpdateHalo(cam, k, e);
        }

        // ------------------------------------------------------------------ 快報：開始時在畫面上方出現幾秒（像新聞插播）
        void Announce(double now, float k, DSTAnomaly.Event e)
        {
            if (k <= 0f || _announced.Equals(e.StartAt)) return;
            _announced = e.StartAt;
            _bulletin = e.FullName;
            _bulletinUntil = Time.time + 9f;
        }

        void OnGUI()
        {
            if (_bulletin == null || Time.time > _bulletinUntil || DSTNetPlayer.Local == null) return;
            _big ??= new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _small ??= new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
            float left = _bulletinUntil - Time.time;
            float a = Mathf.Clamp01(left / 1.5f) * Mathf.Clamp01((9f - left) / 0.8f);
            var r = new Rect(0, Screen.height * 0.16f, Screen.width, 44);
            Shadowed(new Rect(r.x, r.y - 30, r.width, 26), "— 異 象 —", _small, new Color(0.95f, 0.8f, 0.55f, a));
            Shadowed(r, _bulletin, _big, new Color(1f, 0.96f, 0.9f, a));
        }

        static void Shadowed(Rect r, string text, GUIStyle s, Color c)
        {
            s.normal.textColor = new Color(0f, 0f, 0f, c.a * 0.7f);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, s);
            s.normal.textColor = c;
            GUI.Label(r, text, s);
        }

        // ------------------------------------------------------------------ 乾雷：不下雨的閃電，遠處打下來，聲音照距離晚到
        void UpdateBolts(Camera cam, double now)
        {
            if (!glowMaterial) return;
            float sph = DSTTimeOfDay.Instance ? DSTTimeOfDay.Instance.SecondsPerHour : 50f;
            long slot = (long)System.Math.Floor(now / DSTAnomaly.BoltSlotHours);
            if (slot != _lastSlot)
            {
                for (long s = slot - 1; s <= slot; s++)
                    if (!_shown.Contains(s) && DSTAnomaly.BoltIn(s, out var b)) { _shown.Add(s); SpawnBolt(cam, b); }
                if (_shown.Count > 64) _shown.Clear();
                _lastSlot = slot;
            }

            float flashLight = 0f;
            for (int i = _strikes.Count - 1; i >= 0; i--)
            {
                var st = _strikes[i];
                float age = (float)((now - st.b.at) * sph);              // 真實秒數
                if (!st.thundered && age > st.b.distance / 343f)
                {
                    st.thundered = true;
                    PlayThunder(st.b.distance);
                }
                if (age > 0.6f)
                {
                    st.line.enabled = false;
                    if (st.thundered) { _pool.Push(st.line); _strikes.RemoveAt(i); }
                    continue;
                }
                if (age < 0f) { st.line.enabled = false; continue; }
                // 閃兩三下
                float on = age < 0.08f ? 1f : age < 0.16f ? 0.15f : age < 0.24f ? 0.8f : Mathf.Max(0f, 1f - (age - 0.24f) / 0.36f) * 0.5f;
                if (st.b.shape % 3 == 0 && age > 0.3f) on *= 0.2f;
                st.line.enabled = on > 0.02f;
                _mpb.SetColor(ColorId, new Color(0.75f, 0.8f, 1f) * (on * 40f));
                st.line.SetPropertyBlock(_mpb);
                float near = Mathf.Clamp01(1f - st.b.distance / 1200f);
                if (on * near > flashLight)
                {
                    flashLight = on * near;
                    _flashLight.transform.position = st.ground + Vector3.up * 120f;
                }
                DSTWeather.Flash = Mathf.Max(DSTWeather.Flash, on * near * 0.6f);
            }
            _flashLight.enabled = flashLight > 0.01f;
            _flashLight.intensity = flashLight * 4e7f;
            _flashLight.range = 1500f;
        }

        void SpawnBolt(Camera cam, DSTAnomaly.Bolt b)
        {
            var line = _pool.Count > 0 ? _pool.Pop() : NewLine("Bolt", 14);
            var rng = new System.Random(b.shape);
            var dir = Quaternion.Euler(0f, b.azimuth, 0f) * Vector3.forward;
            var p = cam.transform.position;
            var ground = p + dir * b.distance;
            var w = DSTWorld.Instance;
            ground.y = w && w.IsRandom && w.Gen != null ? w.Gen.Height(ground.x, ground.z) : p.y - 2f;
            var top = ground + Vector3.up * (260f + (float)rng.NextDouble() * 120f);
            var side = Vector3.Cross(dir, Vector3.up);
            int n = line.positionCount;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                var q = Vector3.Lerp(top, ground, t);
                if (i > 0 && i < n - 1)
                    q += side * ((float)rng.NextDouble() - 0.5f) * 40f + dir * ((float)rng.NextDouble() - 0.5f) * 25f;
                line.SetPosition(i, q);
            }
            float width = Mathf.Clamp(b.distance * 0.004f, 0.8f, 4f);
            line.startWidth = width; line.endWidth = width * 0.6f;
            line.enabled = false;
            _strikes.Add(new Strike { line = line, b = b, ground = ground });
        }

        void PlayThunder(float distance)
        {
            AudioSource src = null;
            foreach (var s in _thunderSources) if (!s.isPlaying) { src = s; break; }
            if (!src)
            {
                src = gameObject.AddComponent<AudioSource>();
                src.spatialBlend = 0f; src.playOnAwake = false;
                _thunderSources.Add(src);
            }
            src.clip = _thunder;
            src.volume = Mathf.Lerp(0.9f, 0.15f, Mathf.InverseLerp(150f, 1100f, distance));
            src.pitch = Mathf.Lerp(1.1f, 0.65f, Mathf.InverseLerp(150f, 1100f, distance));   // 越遠越低沉
            src.Play();
        }

        // ------------------------------------------------------------------ 落下的東西：1 雨（落地前蒸發）、2 灰、3 閃光的細晶
        void UpdateFall(Camera cam, float k, DSTAnomaly.Event e)
        {
            if (!dustMaterial) return;
            int kind = k > 0f ? e.fall : 0;
            if (kind != _fallKind && kind != 0) SetupFall(kind, e.fallColor);
            if (kind != 0) _fallKind = kind;
            if (!_fall) return;
            _fall.transform.position = cam.transform.position + Vector3.up * (_fallKind == 1 ? 14f : 8f);
            var em = _fall.emission;
            // 量要少：一多就刺眼，氣氛交給霧
            em.rateOverTime = kind == 0 ? 0f : k * (_fallKind == 1 ? 450f : _fallKind == 2 ? 220f : 35f);
            if (kind != 0 && !_fall.isPlaying) _fall.Play();
            else if (kind == 0 && _fall.isPlaying) _fall.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        void SetupFall(int kind, Color c)
        {
            if (!_fall)
            {
                var go = new GameObject("AnomalyFall");
                go.transform.SetParent(transform, false);
                _fall = go.AddComponent<ParticleSystem>();
                _fall.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                _fallRenderer = go.GetComponent<ParticleSystemRenderer>();
                _fallRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _fallRenderer.receiveShadows = false;
                var m0 = _fall.main;
                m0.loop = true; m0.playOnAwake = false; m0.maxParticles = 5000;
                m0.simulationSpace = ParticleSystemSimulationSpace.World;
                var shape = _fall.shape;
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale = new Vector3(40f, 1f, 40f);
            }
            _fall.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (!_fallMat) _fallMat = new Material(kind == 3 && glowMaterial ? glowMaterial : dustMaterial);
            else _fallMat.shader = (kind == 3 && glowMaterial ? glowMaterial : dustMaterial).shader;
            if (kind == 3 && glowMaterial) _fallMat.CopyPropertiesFromMaterial(glowMaterial);
            else _fallMat.CopyPropertiesFromMaterial(dustMaterial);
            _fallMat.SetColor(ColorId, kind == 1 ? new Color(c.r, c.g, c.b, 0.22f)
                                     : kind == 2 ? new Color(c.r, c.g, c.b, 0.45f)
                                     : new Color(c.r, c.g, c.b, 1f) * 1.5f);
            _fallRenderer.material = _fallMat;

            var main = _fall.main;
            var vel = _fall.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            var noise = _fall.noise;
            main.startSpeed = 0f;
            main.startSize3D = true;
            switch (kind)
            {
                case 1:     // 細長的雨絲，掉不到地面就沒了（virga）
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.8f);
                    main.startSizeX = new ParticleSystem.MinMaxCurve(0.02f, 0.035f);
                    main.startSizeY = new ParticleSystem.MinMaxCurve(0.6f, 1.1f);
                    main.startSizeZ = 1f;
                    vel.x = new ParticleSystem.MinMaxCurve(-1f, 1f); vel.y = new ParticleSystem.MinMaxCurve(-22f, -16f); vel.z = new ParticleSystem.MinMaxCurve(-1f, 1f);
                    noise.enabled = false;
                    _fallRenderer.renderMode = ParticleSystemRenderMode.Stretch;
                    _fallRenderer.velocityScale = 0.04f; _fallRenderer.lengthScale = 1f;
                    break;
                case 2:     // 灰：慢慢飄落、打轉
                    main.startLifetime = new ParticleSystem.MinMaxCurve(6f, 9f);
                    main.startSizeX = main.startSizeY = new ParticleSystem.MinMaxCurve(0.03f, 0.08f);
                    main.startSizeZ = 1f;
                    vel.x = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f); vel.y = new ParticleSystem.MinMaxCurve(-1.4f, -0.7f); vel.z = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
                    noise.enabled = true; noise.strength = 0.8f; noise.frequency = 0.4f;
                    _fallRenderer.renderMode = ParticleSystemRenderMode.Billboard;
                    break;
                default:    // 細晶：發亮的小點，幾乎懸在空中
                    main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 7f);
                    main.startSizeX = main.startSizeY = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
                    main.startSizeZ = 1f;
                    vel.x = new ParticleSystem.MinMaxCurve(-0.2f, 0.2f); vel.y = new ParticleSystem.MinMaxCurve(-0.5f, -0.1f); vel.z = new ParticleSystem.MinMaxCurve(-0.2f, 0.2f);
                    noise.enabled = true; noise.strength = 0.3f; noise.frequency = 0.6f;
                    _fallRenderer.renderMode = ParticleSystemRenderMode.Billboard;
                    break;
            }
        }

        // ------------------------------------------------------------------ 鳴沙：沙丘低沉的嗡鳴（真實存在的現象，約 70~110 Hz）
        void UpdateHum(float k, DSTAnomaly.Event e)
        {
            float target = k * e.hum * 0.5f * (0.8f + 0.2f * Mathf.PerlinNoise(Time.time * 0.2f, 9f));
            _hum.volume = Mathf.Lerp(_hum.volume, target, Time.deltaTime * 1.5f);
            if (k > 0f) _hum.pitch = e.humPitch;
            if (_hum.volume > 0.005f && !_hum.isPlaying) _hum.Play();
            else if (_hum.volume <= 0.005f && _hum.isPlaying) _hum.Stop();
        }

        // ------------------------------------------------------------------ 幻日：太陽外 22° 的光環，左右各一個假太陽
        void UpdateHalo(Camera cam, float k, DSTAnomaly.Event e)
        {
            if (!_halo) return;
            var tod = DSTTimeOfDay.Instance;
            bool on = k > 0f && e.halo && tod;
            float elev = 0f, yaw = 0f;
            if (on) { tod.SunAngles(tod.Hour, out elev, out yaw); on = elev > 2f; }
            _halo.enabled = _dogL.enabled = _dogR.enabled = on;
            if (!on) return;
            var toSun = -(Quaternion.Euler(elev, yaw, 0f) * Vector3.forward);
            var p = cam.transform.position;
            var basis = Quaternion.LookRotation(toSun);
            float r22 = Mathf.Tan(22f * Mathf.Deg2Rad);
            for (int i = 0; i < _halo.positionCount; i++)
            {
                float a = i / (float)(_halo.positionCount - 1) * Mathf.PI * 2f;
                var d = basis * new Vector3(Mathf.Cos(a) * r22, Mathf.Sin(a) * r22, 1f);
                _halo.SetPosition(i, p + d.normalized * skyDistance);
            }
            float fade = k * (1f - DSTWeather.Intensity);
            _halo.startWidth = _halo.endWidth = skyDistance * 0.03f;
            _mpb.SetColor(ColorId, new Color(1f, 0.95f, 0.85f) * (fade * 1.2f));
            _halo.SetPropertyBlock(_mpb);
            // 假太陽：和太陽同高，左右各偏 22°（太陽越高偏得越多），短短一段，內緣偏紅
            float off = 22f / Mathf.Max(0.3f, Mathf.Cos(elev * Mathf.Deg2Rad));
            float sunAz = Mathf.Atan2(toSun.x, toSun.z) * Mathf.Rad2Deg;
            for (int s = 0; s < 2; s++)
            {
                var l = s == 0 ? _dogL : _dogR;
                float sign = s == 0 ? -1f : 1f;
                l.SetPosition(0, p + Quaternion.Euler(-elev, sunAz + sign * off, 0f) * Vector3.forward * skyDistance);
                l.SetPosition(1, p + Quaternion.Euler(-elev, sunAz + sign * (off + 4f), 0f) * Vector3.forward * skyDistance);
                l.startWidth = skyDistance * 0.05f; l.endWidth = skyDistance * 0.012f;
                _mpb.SetColor(ColorId, new Color(1f, 0.85f, 0.7f) * (fade * 6f));
                l.SetPropertyBlock(_mpb);
            }
        }

        LineRenderer NewLine(string name, int points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<LineRenderer>();
            l.positionCount = points;
            l.useWorldSpace = true;
            l.material = glowMaterial;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.enabled = false;
            return l;
        }

        // ------------------------------------------------------------------ 合成的聲音
        static AudioClip ThunderClip()
        {
            const int rate = 22050, len = rate * 5;
            var data = new float[len];
            var rng = new System.Random(11);
            float lp = 0f, lp2 = 0f;
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)rate;
                float w = (float)rng.NextDouble() * 2f - 1f;
                lp += (w - lp) * 0.08f;
                lp2 += (lp - lp2) * 0.05f;
                // 先一聲劈裂，接著幾波滾動的低鳴
                float crack = Mathf.Exp(-t * 18f) * w * 0.6f;
                float roll = Mathf.Exp(-t * 0.9f) * (0.6f + 0.4f * Mathf.Sin(t * 5.3f + Mathf.Sin(t * 1.7f) * 2f));
                data[i] = crack + lp2 * roll * 6f + lp * roll * 0.8f;
            }
            var clip = AudioClip.Create("Thunder", len, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        static AudioClip HumClip()
        {
            const int rate = 22050, len = rate * 6;
            var data = new float[len];
            var rng = new System.Random(5);
            float lp = 0f;
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)rate;
                lp += (((float)rng.NextDouble() * 2f - 1f) - lp) * 0.02f;
                // 基頻 + 微微走音的泛音（拍頻）+ 一點沙沙的低噪
                float f = 95f;
                float s = Mathf.Sin(2f * Mathf.PI * f * t) * 0.5f
                        + Mathf.Sin(2f * Mathf.PI * (f * 1.5f + 0.5f) * t) * 0.22f
                        + Mathf.Sin(2f * Mathf.PI * (f * 2f - 0.333f) * t) * 0.15f;
                data[i] = s * (0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * t / 3f)) + lp * 1.5f;
            }
            const int fade = rate / 2;      // 頭尾交疊，循環時不會喀一聲
            for (int i = 0; i < fade; i++)
            {
                float a = i / (float)fade;
                data[i] = data[i] * a + data[len - fade + i] * (1f - a);
            }
            var trimmed = new float[len - fade];
            System.Array.Copy(data, trimmed, trimmed.Length);
            var clip = AudioClip.Create("SingingSand", trimmed.Length, 1, rate, false);
            clip.SetData(trimmed, 0);
            return clip;
        }
    }
}
