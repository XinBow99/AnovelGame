using UnityEngine;

namespace DUST.Vehicles
{
    // Roadster 推進器特效 / 聲音（issue #8 第三階段）。冷氣（壓縮氮氣）不燃燒：沒有火焰、沒有發光，
    // 只有高速的白色半透明噴流 + 快速膨脹的白霧（氣體急速膨脹降溫、空氣裡的水氣凝結）。
    // 參考：Tesla 發表會影片（tsl01/02：懸停時四道藍白色細噴流、落地 / 貼地時翻滾的大片白雲）、Tesla「Gravity」圖。
    // 每個噴嘴兩組粒子：
    //   Jet   — 細長、很快、很短命（拉伸 billboard），長度跟推力成正比
    //   Cloud — 慢、大、會撞到地面 / 牆往外散開（碰撞模組），翻滾（noise）後淡掉
    // 強度直接讀 DSTThrusterSystem 的實際閥門開度（遠端玩家讀網路同步的開度），不是按鍵。
    // 聲音（合成）：高壓氣體嘶聲（音量跟總推力）、閥門「喀」一聲、電動馬達的高頻嗚聲（音高跟車速）。
    [RequireComponent(typeof(DSTThrusterSystem))]
    public class DSTThrusterFX : MonoBehaviour
    {
        public Material vaporMaterial;
        public float jetLength = 1f;        // 整體縮放（半尺寸世界）
        public float cloudAmount = 1f;

        DSTThrusterSystem _sys;
        DSTRoadsterController _car;
        ParticleSystem[] _jet, _cloud;
        float[] _jetRate, _cloudRate;
        AudioSource _hiss, _valve, _motor;
        AudioClip _pop;
        float _lastPop, _maxTotal;

        void Awake()
        {
            _sys = GetComponent<DSTThrusterSystem>();
            _car = GetComponent<DSTRoadsterController>();
            int n = _sys.thrusters.Length;
            _jet = new ParticleSystem[n];
            _cloud = new ParticleSystem[n];
            _jetRate = new float[n];
            _cloudRate = new float[n];
            foreach (var t in _sys.thrusters) _maxTotal += t.maxThrust;
            if (vaporMaterial)
                for (int i = 0; i < n; i++)
                {
                    var t = _sys.thrusters[i];
                    var root = new GameObject("FX_" + t.name).transform;
                    root.SetParent(transform, false);
                    root.localPosition = t.localPos;
                    root.localRotation = Quaternion.LookRotation(t.localDir, Mathf.Abs(t.localDir.y) > 0.9f ? Vector3.forward : Vector3.up);
                    float speed = t.group == DSTThrusterSystem.Group.Rear ? 16f : t.group == DSTThrusterSystem.Group.Down ? 12f : 10f;
                    _jet[i] = Jet(root, t.radius, speed * jetLength, out _jetRate[i]);
                    _cloud[i] = Cloud(root, t.group, out _cloudRate[i]);
                }
            _sys.ValveOpened += OnValve;
            BuildAudio();
        }

        void OnDestroy()
        {
            if (_sys) _sys.ValveOpened -= OnValve;
        }

        // ------------------------------------------------------------------ 粒子
        ParticleSystem Jet(Transform parent, float radius, float speed, out float rate)
        {
            var ps = NewSystem(parent, "Jet", 400);
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.07f, 0.13f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.85f, speed * 1.15f);
            main.startSize = new ParticleSystem.MinMaxCurve(radius * 1.6f, radius * 2.4f);
            main.startColor = new Color(0.86f, 0.92f, 1f, 0.55f);   // 很淡的藍白（影片裡的細噴流）
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 3f;
            shape.radius = radius * 0.8f;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 5f));   // 往外急速膨脹
            Fade(ps, 0.05f, 0.55f);
            var inherit = ps.inheritVelocity;
            inherit.enabled = true;
            inherit.mode = ParticleSystemInheritVelocityMode.Initial;
            inherit.curve = 1f;
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.velocityScale = 0.035f;
            r.lengthScale = 1.5f;
            rate = 380f;
            return ps;
        }

        ParticleSystem Cloud(Transform parent, DSTThrusterSystem.Group g, out float rate)
        {
            bool down = g == DSTThrusterSystem.Group.Down;
            var ps = NewSystem(parent, "Cloud", 700);
            var main = ps.main;
            main.startLifetime = down ? new ParticleSystem.MinMaxCurve(1.2f, 2.4f) : new ParticleSystem.MinMaxCurve(0.7f, 1.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(3f, 7f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.10f, 0.22f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(0.95f, 0.96f, 0.98f, 0.42f);
            main.gravityModifier = -0.02f;                            // 冷空氣其實會往下沉，但白霧一下子就被捲上來
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 16f;
            shape.radius = 0.03f;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.25f, 4f), new Keyframe(1f, 8f)));
            Fade(ps, 0.08f, 1f);
            var limit = ps.limitVelocityOverLifetime;                // 空氣阻力：噴出去很快、一下子就慢下來
            limit.enabled = true;
            limit.limit = 0.6f;
            limit.dampen = 0.12f;
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.6f;
            noise.frequency = 1.4f;
            noise.scrollSpeed = 0.6f;
            var inherit = ps.inheritVelocity;
            inherit.enabled = true;
            inherit.mode = ParticleSystemInheritVelocityMode.Initial;
            inherit.curve = 0.6f;
            var col = ps.collision;                                   // 撞到地面就往外翻滾散開（影片裡懸停時的大片白雲）
            col.enabled = true;
            col.type = ParticleSystemCollisionType.World;
            col.mode = ParticleSystemCollisionMode.Collision3D;
            col.quality = ParticleSystemCollisionQuality.Medium;
            col.dampen = 0.55f;
            col.bounce = 0.08f;
            col.radiusScale = 0.3f;
            col.lifetimeLoss = 0f;
            rate = down ? 140f : 90f;
            return ps;
        }

        ParticleSystem NewSystem(Transform parent, string name, int max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = max;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            var em = ps.emission;
            em.rateOverTime = 0f;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.material = vaporMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.Distance;
            r.maxParticleSize = 0.6f;          // 鏡頭鑽進白霧時不要整個畫面糊掉
            ps.Play();
            return ps;
        }

        static void Fade(ParticleSystem ps, float inAt, float peak)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(peak, inAt), new GradientAlphaKey(peak * 0.5f, 0.5f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            float total = 0f;
            for (int i = 0; i < _jet.Length; i++)
            {
                float o = _sys.Output(i);
                total += o * _sys.thrusters[i].maxThrust;
                if (_jet[i]) { var em = _jet[i].emission; em.rateOverTime = _jetRate[i] * o; }
                if (_cloud[i]) { var em = _cloud[i].emission; em.rateOverTime = _cloudRate[i] * cloudAmount * Mathf.Sqrt(o); }
            }
            UpdateAudio(total / Mathf.Max(_maxTotal, 1f), dt);
        }

        // ------------------------------------------------------------------ 聲音（合成）
        void BuildAudio()
        {
            _hiss = Source("GasHiss", Hiss(), true, 3f);
            _motor = Source("MotorWhine", Whine(), true, 2f);
            _valve = Source("Valve", null, false, 2f);
            _pop = Pop();
        }

        AudioSource Source(string name, AudioClip clip, bool loop, float minDist)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var a = go.AddComponent<AudioSource>();
            a.clip = clip;
            a.loop = loop;
            a.volume = 0f;
            a.spatialBlend = 1f;
            a.rolloffMode = AudioRolloffMode.Logarithmic;
            a.minDistance = minDist;
            a.maxDistance = 300f;
            a.dopplerLevel = 0.5f;
            a.playOnAwake = false;
            if (loop && clip) a.Play();
            return a;
        }

        void OnValve(int i)
        {
            if (!_valve || !_pop || Time.time - _lastPop < 0.06f) return;
            _lastPop = Time.time;
            var t = _sys.thrusters[i];
            _valve.transform.localPosition = t.localPos;
            _valve.pitch = Random.Range(0.9f, 1.15f);
            _valve.PlayOneShot(_pop, t.group == DSTThrusterSystem.Group.Side ? 0.5f : 0.8f);
        }

        void UpdateAudio(float thrust01, float dt)
        {
            if (_hiss)
            {
                float target = Mathf.Sqrt(Mathf.Clamp01(thrust01 * 2.5f));
                _hiss.volume = Mathf.MoveTowards(_hiss.volume, target, dt * 12f);
                _hiss.pitch = 0.85f + 0.35f * target;
            }
            if (_motor && _car)
            {
                float v = Mathf.Abs(_car.SpeedKmh) / 3.6f;
                float load = Mathf.Clamp01(_car.DrivePowerW / Mathf.Max(_car.maxPowerW, 1f));
                bool on = _car.PlayerControlled || (_car.Throttle > 0.01f);
                _motor.volume = Mathf.MoveTowards(_motor.volume, on ? 0.06f + 0.35f * load + 0.1f * Mathf.Clamp01(v / 40f) : 0f, dt * 2f);
                _motor.pitch = 0.35f + v / 28f;            // 齒比固定：馬達轉速跟車速成正比
            }
        }

        // 高壓氣體：白噪音 + 一點高通（嘶聲），2 秒循環、頭尾交叉淡化
        static AudioClip Hiss()
        {
            const int rate = 22050, len = rate * 2, fade = rate / 4;
            var d = new float[len + fade];
            var rng = new System.Random(11);
            float lp = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float w = (float)rng.NextDouble() * 2f - 1f;
                lp += (w - lp) * 0.35f;
                d[i] = (w - lp * 0.7f) * 0.7f;
            }
            return Loop("GasHiss", d, len, fade, rate);
        }

        // 電動馬達：基頻 + 幾個齒輪泛音（音高由 AudioSource.pitch 跟車速）
        static AudioClip Whine()
        {
            const int rate = 22050, len = rate, fade = rate / 8;
            var d = new float[len + fade];
            var rng = new System.Random(5);
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)rate;
                d[i] = 0.30f * Mathf.Sin(2f * Mathf.PI * 520f * t) + 0.16f * Mathf.Sin(2f * Mathf.PI * 1040f * t)
                     + 0.10f * Mathf.Sin(2f * Mathf.PI * 1567f * t) + 0.03f * ((float)rng.NextDouble() * 2f - 1f);
            }
            return Loop("MotorWhine", d, len, fade, rate);
        }

        static AudioClip Loop(string name, float[] d, int len, int fade, int rate)
        {
            var o = new float[len];
            for (int i = 0; i < len; i++)
            {
                float v = d[i];
                if (i < fade) { float w = i / (float)fade; v = v * w + d[len + i] * (1f - w); }
                o[i] = v;
            }
            var clip = AudioClip.Create(name, len, 1, rate, false);
            clip.SetData(o, 0);
            return clip;
        }

        // 電磁閥：很短的金屬「喀」（衰減的高頻噪音）+ 低頻的「砰」（氣一下子衝出去）
        static AudioClip Pop()
        {
            const int rate = 22050, len = rate / 5;
            var d = new float[len];
            var rng = new System.Random(3);
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)rate;
                float click = ((float)rng.NextDouble() * 2f - 1f) * Mathf.Exp(-t * 180f);
                float thump = Mathf.Sin(2f * Mathf.PI * 90f * t) * Mathf.Exp(-t * 25f);
                float rush = ((float)rng.NextDouble() * 2f - 1f) * Mathf.Exp(-t * 18f) * 0.35f;
                d[i] = 0.9f * click + 0.6f * thump + rush;
            }
            var clip = AudioClip.Create("ValvePop", len, 1, rate, false);
            clip.SetData(d, 0);
            return clip;
        }
    }
}
