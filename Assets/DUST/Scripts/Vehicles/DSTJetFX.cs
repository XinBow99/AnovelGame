using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.Vehicles
{
    // 米格-25 的執行期特效（都跟著 DSTJetController 的油門 / 高度 / 速度變）。
    // 後燃器照白天的實拍（F-16、Rafale 起飛照，Wikimedia Commons；migflug.com 的火焰顏色說明）：
    //   白天看得到的主要是「噴口裡面」橘黃色的光（火焰穩定器那一圈），噴出來的火焰短、半透明、
    //   帶幾道淡淡的震波環；藍色只有晚上看得到。所以：
    //   - 噴口光盤：縮在噴口內 12 cm 的橘黃圓盤 + 更深處一片藍紫色小圓盤（火焰穩定器），疊起來中間偏粉白
    //   - 火焰：短的半透明錐（只畫外側，避免好幾層疊加），震波環在貼圖裡
    //   - 熱浪：噴口後方一段只做畫面扭曲的錐（HDRP distortion），開車就有，油門越大越明顯
    //   亮度直接給 _UnlitColor（實測校正，EV 13.8 大太陽下）：火焰 ~20 剛好、80 就 bloom 爆白；
    //   這個專案的 HDRP Unlit 顏色不吃曝光，所以不用照 EV 換算（換算成 17000 nit 那版整片白，玩家說看不到路）。
    //   參考：玩家給的米格-25 起飛照（nationalinterest.org）—— 噴口裡面橘黃、中間藍紫色的火焰穩定器環、
    //   外面的火焰很短，後面一大片熱浪 / 淡煙。
    //   （第一版用 30000 nit 的加法錐，四層疊起來被 bloom 吹成整片白，玩家說後面看不到路）
    // 第二次對照實拍（Wikimedia Commons「MiG-25 take offs」：MiG-25RB 起飛的正後方 / 側面 / 正面照，2026-10-15）：
    //   - 噴口：後燃器時整個噴口都填滿光（不是中間一顆亮點）——外圈是燒紅的橘色內襯，裡面黃白斑駁，
    //     疊著藍紫色的火焰穩定器（兩圈環 + 放射狀支架）。改成一張有顏色的噴口貼圖，蓋滿噴口內徑。
    //   - 側面看得到一段半透明的橘色火焰，大約一個垂尾弦長（實機 ~5 m），原本 1.9（半尺寸）太短太淡。
    //   - 正面照：進氣道下方主輪艙前兩盞著陸燈 + 前起落架上一盞滑行燈，放下起落架就亮。
    //   - 落地：機尾兩片垂尾之間的傘艙拋出兩頂十字形減速傘（testpilot.ru：兩頂、共約 50 m²，左腹鰭探針觸地自動開傘）。
    // 跑道揚沙、落地胎煙、合成引擎聲同前。
    [RequireComponent(typeof(DSTJetController))]
    public class DSTJetFX : MonoBehaviour
    {
        public Material flameMaterial;            // HDRP Unlit 加法（亮度在執行期依曝光給）
        public Material nozzleMaterial;           // 噴口面：有顏色的加法貼圖（橘色內襯、黃白火焰、藍紫火焰穩定器）
        public Material hazeMaterial;             // HDRP Unlit distortion only
        public Material dustMaterial;
        public float plumeLength = 2.6f;          // 全後燃器火焰長度（m，半尺寸；實機起飛側面照約 5 m）
        public float glowIntensity = 2f;          // 噴口面（貼圖本身有顏色；4 以上中心吹白、看不到藍紫色火焰穩定器，實測）
        public float plumeIntensity = 9f;         // 火焰錐，側面看的亮度（正後方只剩 10%）；4.5 在大太陽下側面幾乎看不到
        public float dustHeight = 6f;

        DSTJetController _jet;
        readonly Transform[] _nozzles = new Transform[2];
        readonly Transform[] _plume = new Transform[2], _core = new Transform[2], _haze = new Transform[2];
        Renderer[] _glowR = new Renderer[2], _plumeR = new Renderer[2], _coreR = new Renderer[2], _hazeR = new Renderer[2];
        MaterialPropertyBlock _mpb;
        ParticleSystem _dust, _smoke;
        AudioSource _roar, _whine;
        float _spool;

        static readonly int UnlitColor = Shader.PropertyToID("_UnlitColor");
        static readonly int DistortionScale = Shader.PropertyToID("_DistortionScale");

        void Awake()
        {
            _jet = GetComponent<DSTJetController>();
            _jet.TouchDown += OnTouchDown;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "MiG25_Nozzle_L") _nozzles[0] = t;
                if (t.name == "MiG25_Nozzle_R") _nozzles[1] = t;
            }
            _mpb = new MaterialPropertyBlock();
            BuildFlames();
            _dust = BuildParticles("ExhaustDust", 1500, false);
            _smoke = BuildParticles("TyreSmoke", 400, true);
            BuildAudio();
            BuildSupersonic();
            BuildChute();
            BuildLamps();
        }

        void OnDestroy()
        {
            if (_jet) { _jet.TouchDown -= OnTouchDown; _jet.SoundBarrier -= OnSoundBarrier; }
            if (_chuteRoot && _chuteRoot.parent != transform) Destroy(_chuteRoot.gameObject);   // 拋掉、還躺在地上的傘
        }

        // ------------------------------------------------------------------ 後燃器
        void BuildFlames()
        {
            var cone = ConeMesh(24, 0.55f);      // 火焰：差不多是一根柱子、尾端稍微收（實拍側面）
            var coreCone = ConeMesh(16, 0.05f);  // 內焰：尖的
            var disc = FaceDiscMesh(32);
            var haze = ConeMesh(16, 3.2f);       // 熱浪往後越散越開
            for (int i = 0; i < 2; i++)
            {
                if (!_nozzles[i]) continue;
                if (flameMaterial)
                {
                    _glowR[i] = Part("NozzleGlow_" + i, disc, nozzleMaterial ? nozzleMaterial : flameMaterial, _nozzles[i], -0.12f, out _);
                    _plumeR[i] = Part("Plume_" + i, cone, flameMaterial, _nozzles[i], 0f, out _plume[i]);
                    _coreR[i] = Part("PlumeCore_" + i, coreCone, flameMaterial, _nozzles[i], 0f, out _core[i]);
                }
                if (hazeMaterial) _hazeR[i] = Part("Haze_" + i, haze, hazeMaterial, _nozzles[i], 0.1f, out _haze[i]);
            }
        }

        // 噴口出口中心往後（-forward）；along = 沿噴流方向的位移（負 = 縮進噴口裡）
        Renderer Part(string name, Mesh mesh, Material mat, Transform nozzle, float along, out Transform t)
        {
            var go = new GameObject(name);
            t = go.transform;
            t.SetParent(transform, false);
            t.rotation = Quaternion.LookRotation(-transform.forward, transform.up);
            t.position = nozzle.position - transform.forward * along;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            return r;
        }

        // 單位錐：底在 z = 0（半徑 1），z = 1 處半徑 tipRadius；u = 0..1 沿長度。法線朝外（只畫外側）
        static Mesh ConeMesh(int seg, float tipRadius)
        {
            var v = new System.Collections.Generic.List<Vector3>();
            var uv = new System.Collections.Generic.List<Vector2>();
            var tri = new System.Collections.Generic.List<int>();
            const int rings = 10;
            for (int r = 0; r <= rings; r++)
            {
                float t = r / (float)rings;
                float rad = tipRadius < 1f ? Mathf.Lerp(1f, tipRadius, Mathf.Pow(t, 0.8f)) : Mathf.Lerp(1f, tipRadius, t);
                for (int s = 0; s <= seg; s++)
                {
                    float a = s / (float)seg * Mathf.PI * 2f;
                    v.Add(new Vector3(Mathf.Cos(a) * rad, Mathf.Sin(a) * rad, t));
                    uv.Add(new Vector2(t, s / (float)seg));
                }
            }
            for (int r = 0; r < rings; r++)
            for (int s = 0; s < seg; s++)
            {
                int a = r * (seg + 1) + s, b = a + seg + 1;
                tri.AddRange(new[] { a, a + 1, b, a + 1, b + 1, b });
            }
            var m = new Mesh { name = "FlameCone" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
            m.RecalculateNormals();
            float rmax = Mathf.Max(1f, tipRadius);
            m.bounds = new Bounds(new Vector3(0, 0, 0.5f), new Vector3(rmax * 2.2f, rmax * 2.2f, 1.2f));
            return m;
        }

        // 單位圓盤，面朝 +z；平面 UV（貼圖整張貼在圓上：中心 0.5,0.5、邊緣半徑 0.5）
        static Mesh FaceDiscMesh(int seg)
        {
            var v = new System.Collections.Generic.List<Vector3> { Vector3.zero };
            var uv = new System.Collections.Generic.List<Vector2> { new Vector2(0.5f, 0.5f) };
            var tri = new System.Collections.Generic.List<int>();
            for (int s = 0; s <= seg; s++)
            {
                float a = s / (float)seg * Mathf.PI * 2f;
                v.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f));
                uv.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a)));
            }
            for (int s = 1; s <= seg; s++) tri.AddRange(new[] { 0, s, s + 1 });   // 從 +z 看是正面（單面材質才看得到）
            var m = new Mesh { name = "NozzleGlow" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
            m.RecalculateNormals();
            return m;
        }

        void UpdateFlames()
        {
            bool on = _jet.EngineOn && !_jet.Hidden;
            float ab = on ? _jet.Afterburner01 : 0f;
            float thr = on ? _jet.Throttle : 0f;
            float flicker = 0.9f + 0.1f * Mathf.PerlinNoise(Time.time * 17f, 0.3f);
            for (int i = 0; i < 2; i++)
            {
                if (!_nozzles[i]) continue;
                // 噴口面：蓋滿噴口內徑（噴口外徑 0.29、內襯 ~0.25，半尺寸）。顏色在貼圖裡，這裡只給亮度：
                // 慢車 / 軍用推力只有暗紅的餘光，後燃器整片亮起來
                if (_glowR[i])
                {
                    _glowR[i].transform.localScale = Vector3.one * 0.25f;
                    var c = ab > 0f ? Color.Lerp(new Color(1f, 0.55f, 0.35f), Color.white, ab) * glowIntensity * Mathf.Lerp(0.5f, 1f, ab)
                                    : new Color(0.6f, 0.12f, 0.03f) * (0.05f + 0.25f * thr);
                    Set(_glowR[i], c * flicker, on);
                }
                // 火焰：只有後燃器才有，短而淡
                float len = plumeLength * Mathf.SmoothStep(0f, 1f, ab) * (0.93f + 0.07f * flicker);
                if (_plumeR[i])
                {
                    _plume[i].localScale = new Vector3(0.22f, 0.22f, Mathf.Max(0.01f, len));
                    _plumeCol[i] = new Color(1f, 0.45f, 0.15f) * plumeIntensity * ab * flicker;
                    Set(_plumeR[i], _plumeCol[i], ab > 0.01f);
                }
                if (_coreR[i])
                {
                    _core[i].localScale = new Vector3(0.13f, 0.13f, Mathf.Max(0.01f, len * 0.3f));
                    _coreCol[i] = new Color(1f, 0.8f, 0.55f) * plumeIntensity * 1.4f * ab * flicker;
                    Set(_coreR[i], _coreCol[i], ab > 0.01f);
                }
                // 熱浪：慢慢轉 + 微微抖，扭曲的圖案就會一直變
                if (_hazeR[i])
                {
                    float k = Mathf.Clamp01(0.25f + thr) * (1f + ab);
                    _haze[i].localScale = new Vector3(0.28f, 0.28f, 2.5f + 3.5f * thr + 2f * ab) * (0.97f + 0.06f * Mathf.PerlinNoise(Time.time * 6f, i));
                    _haze[i].Rotate(0f, 0f, 160f * Time.deltaTime, Space.Self);
                    _hazeR[i].GetPropertyBlock(_mpb);
                    _mpb.SetFloat(DistortionScale, 0.35f * k);
                    _hazeR[i].SetPropertyBlock(_mpb);
                    _hazeR[i].enabled = on;
                }
            }
        }

        // 火焰錐從正後方（順著噴流看進去）幾乎透明、看得到噴口面；從側面看才是一段橘色火焰（實拍正後方 / 側面照）。
        // 每台攝影機渲染前照它的視角調亮度，所以追車鏡頭、旁觀者、截圖各看各的
        readonly Color[] _plumeCol = new Color[2], _coreCol = new Color[2];

        void OnEnable() { RenderPipelineManager.beginCameraRendering += OnBeginCamera; }
        void OnDisable() { RenderPipelineManager.beginCameraRendering -= OnBeginCamera; }

        void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (!_jet) return;
            Vector3 axis = -transform.forward;
            for (int i = 0; i < 2; i++)
            {
                if (!_nozzles[i]) continue;
                Vector3 toCam = (cam.transform.position - _nozzles[i].position).normalized;
                float cos = Vector3.Dot(axis, toCam);
                float sin = Mathf.Sqrt(Mathf.Max(0f, 1f - cos * cos));
                float k = Mathf.Lerp(0.05f, 1f, sin * sin * sin);     // sin³θ：正後方 / 斜後方 15° 都幾乎透明（sin² 在 15° 還是一片橘，實測）、側面 1
                if (_plumeR[i] && _plumeR[i].enabled) SetColor(_plumeR[i], _plumeCol[i] * k);
                if (_coreR[i] && _coreR[i].enabled) SetColor(_coreR[i], _coreCol[i] * k);
            }
        }

        void SetColor(Renderer r, Color c)
        {
            r.GetPropertyBlock(_mpb);
            c.a = 1f;
            _mpb.SetColor(UnlitColor, c);
            r.SetPropertyBlock(_mpb);
        }

        void Set(Renderer r, Color c, bool visible)
        {
            r.GetPropertyBlock(_mpb);
            // alpha 固定 1：Color × float 連 alpha 一起乘，加法混合又是 rgb × alpha → 亮了幾百倍（第一版整片白球的原因之一，實測）
            c.a = 1f;
            _mpb.SetColor(UnlitColor, c);
            r.SetPropertyBlock(_mpb);
            r.enabled = visible && c.maxColorComponent > 0.05f;
        }

        // ------------------------------------------------------------------ 揚沙 / 胎煙
        ParticleSystem BuildParticles(string name, int max, bool smoke)
        {
            if (!dustMaterial) return null;
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = smoke ? new ParticleSystem.MinMaxCurve(1.5f, 3f) : new ParticleSystem.MinMaxCurve(1.2f, 2.5f);
            main.startSpeed = 0f;
            main.startSize = smoke ? new ParticleSystem.MinMaxCurve(0.6f, 1.4f) : new ParticleSystem.MinMaxCurve(0.8f, 2.2f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = smoke ? new Color(0.9f, 0.9f, 0.88f, 0.6f) : new Color(0.78f, 0.6f, 0.4f, 0.22f);
            main.maxParticles = max;
            main.gravityModifier = smoke ? -0.02f : 0.15f;
            var em = ps.emission;
            em.rateOverTime = 0f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = smoke ? new Vector3(0.6f, 0.1f, 0.6f) : new Vector3(4f, 0.2f, 2f);
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 2f));
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                         new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
            col.color = grad;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.material = dustMaterial;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.maxParticleSize = 0.35f;     // 鏡頭鑽進沙裡時不要整個畫面糊掉
            ps.Play();
            return ps;
        }

        void UpdateDust()
        {
            if (!_dust) return;
            var em = _dust.emission;
            float agl = _jet.AltitudeAGL;
            float thr = _jet.EngineOn ? _jet.Throttle : 0f;
            // 只有低空、大油門才吹得起沙；往兩側散開比往後多（鏡頭在正後方，不要擋住）
            float k = _jet.Hidden ? 0f : Mathf.Clamp01((thr - 0.5f) * 2f) * Mathf.Clamp01(1f - agl / dustHeight) * (1f + _jet.Afterburner01);
            em.rateOverTime = k * 60f;
            if (k <= 0.001f) return;
            Vector3 back = -Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, back);
            Vector3 p = transform.position + back * (9f + agl);
            if (Physics.Raycast(p + Vector3.up * 20f, Vector3.down, out var hit, 60f, ~0, QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(transform))
                p = hit.point + Vector3.up * 0.3f;
            _dust.transform.SetPositionAndRotation(p, Quaternion.LookRotation(back));
            var vel = _dust.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            Vector3 v = back * (3f + 6f * thr);
            vel.x = new ParticleSystem.MinMaxCurve(v.x - 6f * Mathf.Abs(side.x) - 1f, v.x + 6f * Mathf.Abs(side.x) + 1f);
            vel.y = new ParticleSystem.MinMaxCurve(0.3f, 1.8f);
            vel.z = new ParticleSystem.MinMaxCurve(v.z - 6f * Mathf.Abs(side.z) - 1f, v.z + 6f * Mathf.Abs(side.z) + 1f);
        }

        void OnTouchDown(float verticalSpeed)
        {
            if (!_smoke || verticalSpeed < 0.8f) return;
            foreach (var t in GetComponentsInChildren<Transform>())
            {
                if (!t.name.StartsWith("MiG25_MainWheel")) continue;
                _smoke.transform.position = t.position - Vector3.up * 0.3f;
                _smoke.Emit(Mathf.RoundToInt(Mathf.Clamp(verticalSpeed * 12f, 8f, 50f)));
            }
        }

        // ------------------------------------------------------------------ 引擎聲（合成）
        void BuildAudio()
        {
            _roar = Source("EngineRoar", Synth(false));
            _whine = Source("EngineWhine", Synth(true));
        }

        AudioSource Source(string name, AudioClip clip)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 1.2f, -4.5f);
            var a = go.AddComponent<AudioSource>();
            a.clip = clip;
            a.loop = true;
            a.volume = 0f;
            a.spatialBlend = 1f;
            a.rolloffMode = AudioRolloffMode.Logarithmic;
            a.minDistance = 12f;
            a.maxDistance = 900f;
            a.dopplerLevel = 0.6f;
            a.playOnAwake = false;
            a.Play();
            return a;
        }

        // 轟鳴：棕噪音（低頻）；渦輪：幾個不協和的高頻正弦 + 一點噪音。2 秒循環，頭尾交叉淡化
        static AudioClip Synth(bool whine)
        {
            const int rate = 22050, len = rate * 2, fade = rate / 4;
            var data = new float[len + fade];
            var rng = new System.Random(whine ? 25 : 7);
            float brown = 0f, lp = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float white = (float)rng.NextDouble() * 2f - 1f;
                if (whine)
                {
                    float t = i / (float)rate;
                    data[i] = 0.18f * Mathf.Sin(2f * Mathf.PI * 1830f * t) + 0.12f * Mathf.Sin(2f * Mathf.PI * 2710f * t)
                            + 0.08f * Mathf.Sin(2f * Mathf.PI * 4120f * t) + 0.05f * white;
                }
                else
                {
                    brown = Mathf.Clamp(brown + white * 0.04f, -1f, 1f) * 0.998f;
                    lp += (white - lp) * 0.08f;
                    data[i] = brown * 1.6f + lp * 0.5f;
                }
            }
            var outData = new float[len];
            for (int i = 0; i < len; i++)
            {
                float d = data[i];
                if (i < fade) { float w = i / (float)fade; d = d * w + data[len + i] * (1f - w); }
                outData[i] = d;
            }
            var clip = AudioClip.Create(whine ? "JetWhine" : "JetRoar", len, 1, rate, false);
            clip.SetData(outData, 0);
            return clip;
        }

        void UpdateAudio(float dt)
        {
            if (!_roar) return;
            float target = _jet.EngineOn && !_jet.Hidden ? Mathf.Lerp(0.25f, 1f, _jet.Throttle) : 0f;
            _spool = Mathf.MoveTowards(_spool, target, dt * (target > _spool ? 0.25f : 0.15f));   // 渦輪慢慢轉起來 / 慢慢停
            float ab = _jet.EngineOn ? _jet.Afterburner01 : 0f;
            _roar.volume = Mathf.Clamp01(_spool * 0.55f + ab * 0.45f);
            _roar.pitch = 0.55f + 0.35f * _spool + 0.1f * ab;
            _whine.volume = _spool * 0.22f;
            _whine.pitch = 0.5f + 0.6f * _spool;
        }

        // ------------------------------------------------------------------ 超音速：凝結尾、翼尖渦流、音障雲、音爆
        // 凝結尾：遊戲高度 2.5 km 以上（≈ 實機 10 km，噴流的水氣在高空凍結），從噴口後 5 m 開始拖 30 秒
        // 翼尖渦流：大 G（> 3.5）又在濃的空氣裡，翼尖拉出細白線
        // 音障雲（Prandtl–Glauert 凝結雲）：照實拍（玩家給的照片）是包住機身中後段的一團蓬鬆白霧，
        //   從座艙後面開始、往後張開成鐘形，邊緣軟、飛機在霧裡面看得到。
        //   做法：沿著鐘形表面一直噴很短命的柔軟白霧粒子（跟著飛機的本地座標），疊起來有體積感；
        //   濃度照 Prandtl–Glauert 的 1/√|1−M²|（M = 1 最濃、往兩邊掉），再乘空氣密度（高空太稀不會凝結）
        // 音爆：馬赫錐掃過旁觀者的那一刻才聽得到（N 波：雙響 + 低頻）；錐外（飛機前方）連引擎聲都還沒到
        //       駕駛自己只有突破音障那一下悶響
        public Material contrailMaterial;          // HDRP Unlit 透明，淡出做在貼圖裡（HDRP Unlit 不吃頂點色）
        public Material vaporMaterial;            // 圓形柔邊霧粒子（HDRP Unlit 透明）
        public float contrailAltitude = 2500f;
        public float boomRange = 6000f;

        TrailRenderer[] _contrails, _vortices;
        ParticleSystem _vapor;
        public float VaporDensity { get; private set; }   // 0..1，給測試看
        AudioSource _boom;
        AudioClip _boomClip;
        AudioListener _listener;
        bool _inCone;
        float _boomCooldown;
        static readonly int UnlitColorId = Shader.PropertyToID("_UnlitColor");

        void BuildSupersonic()
        {
            if (contrailMaterial)
            {
                _contrails = new TrailRenderer[2];
                for (int i = 0; i < 2; i++)
                {
                    if (!_nozzles[i]) continue;
                    var t = Trail("Contrail_" + i, contrailMaterial, 30f, 15f, AnimationCurve.Linear(0f, 0.5f, 1f, 9f));
                    t.transform.position = _nozzles[i].position - transform.forward * 5f;
                    _contrails[i] = t;
                }
                _vortices = new TrailRenderer[2];
                for (int i = 0; i < 2; i++)
                {
                    var t = Trail("WingtipVortex_" + i, contrailMaterial, 1.2f, 2f, AnimationCurve.Linear(0f, 0.12f, 1f, 0.6f));
                    t.transform.localPosition = new Vector3(i == 0 ? -3.45f : 3.45f, 1.33f, -3.75f);
                    _vortices[i] = t;
                }
            }
            if (vaporMaterial)
            {
                var go = new GameObject("VaporCloud");
                go.transform.SetParent(transform, false);
                var ps = go.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main;
                main.loop = true;
                main.playOnAwake = false;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;   // 凝結雲一直在同一個位置重新生成，跟著飛機
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.12f, 0.3f);
                main.startSpeed = 0f;
                main.startSize = new ParticleSystem.MinMaxCurve(1.1f, 2.4f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.maxParticles = 1500;
                var em = ps.emission;
                em.rateOverTime = 0f;
                var shape = ps.shape;
                shape.enabled = false;
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = vaporMaterial;
                r.renderMode = ParticleSystemRenderMode.Billboard;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.sortMode = ParticleSystemSortMode.Distance;
                ps.Play();
                _vapor = ps;
            }
            _boomClip = SynthBoom();
            var b = new GameObject("SonicBoom");
            b.transform.SetParent(transform, false);
            _boom = b.AddComponent<AudioSource>();
            _boom.spatialBlend = 0f;        // 音爆是一整片壓力波，不分方向
            _boom.playOnAwake = false;
            _jet.SoundBarrier += OnSoundBarrier;
        }

        TrailRenderer Trail(string name, Material mat, float time, float minVertex, AnimationCurve width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var t = go.AddComponent<TrailRenderer>();
            t.sharedMaterial = mat;
            t.time = time;
            t.minVertexDistance = minVertex;
            t.widthCurve = width;
            t.widthMultiplier = 1f;
            t.textureMode = LineTextureMode.Stretch;
            t.alignment = LineAlignment.View;
            t.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            t.receiveShadows = false;
            t.emitting = false;
            t.numCapVertices = 2;
            return t;
        }

        // Prandtl–Glauert：壓力係數 ∝ 1/√|1−M²|，M = 1 發散。換成 0..1 的凝結濃度（M 0.9 ≈ 0.3、0.95 ≈ 0.7、
        // 0.98–1.02 ≈ 1、1.05 ≈ 0.65、1.1 ≈ 0.25、1.15 以上沒有）
        static float PrandtlGlauert(float m)
        {
            float d = Mathf.Abs(1f - m * m);
            if (d < 1e-4f) return 1f;
            return Mathf.Clamp01((1f / Mathf.Sqrt(d) - 1.6f) / 2.4f);
        }

        // 鐘形表面上的一點（飛機本地座標，半尺寸）：從座艙後（z +1.8）往後到 z −3.4，半徑 1.5 → 3.2 m（前緣是圓頂、不是尖的）
        static Vector3 VaporPoint(float s, float angle, float jitter)
        {
            float z = Mathf.Lerp(1.8f, -3.4f, s);
            float rad = Mathf.Lerp(1.5f, 3.2f, Mathf.Sqrt(s)) * (1f + jitter);
            return new Vector3(Mathf.Cos(angle) * rad, 1.2f + Mathf.Sin(angle) * rad * 0.8f, z);
        }

        // N 波：機頭震波 + 機尾震波兩響（相隔 0.12 s），每響是很陡的起音 + 快速衰減的噪音，後面拖一段低頻轟隆
        static AudioClip SynthBoom()
        {
            const int rate = 22050;
            int len = rate * 2;
            var d = new float[len];
            var rng = new System.Random(2830);
            float brown = 0f;
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)rate;
                float white = (float)rng.NextDouble() * 2f - 1f;
                brown = Mathf.Clamp(brown + white * 0.08f, -1f, 1f) * 0.995f;
                float crack1 = t < 0.002f ? t / 0.002f : Mathf.Exp(-(t - 0.002f) / 0.05f);
                float t2 = t - 0.12f;
                float crack2 = t2 < 0f ? 0f : t2 < 0.002f ? t2 / 0.002f : Mathf.Exp(-(t2 - 0.002f) / 0.06f);
                float rumble = Mathf.Exp(-t / 0.6f) * (0.6f * Mathf.Sin(2f * Mathf.PI * 38f * t) + 0.5f * brown);
                d[i] = Mathf.Clamp(0.9f * crack1 * (0.6f + 0.4f * white) - 0.8f * crack2 * (0.6f + 0.4f * white) + 0.7f * rumble, -1f, 1f);
            }
            var clip = AudioClip.Create("SonicBoom", len, 1, rate, false);
            clip.SetData(d, 0);
            return clip;
        }

        void OnSoundBarrier(bool up)
        {
            // 駕駛那台：突破音障只有一下悶響（震波在機身後面，駕駛聽不到真正的音爆）
            if (up && _boom && _jet.PlayerControlled) _boom.PlayOneShot(_boomClip, 0.25f);
        }

        // 給測試 / 除錯：目前這台電腦的聽者在不在馬赫錐裡、響過幾次
        public bool ListenerInCone => _inCone;
        public int BoomCount { get; private set; }

        void UpdateSupersonic(float dt)
        {
            float mach = _jet.Mach;
            bool hidden = _jet.Hidden;
            // 凝結尾（有遲滯，不要在臨界高度一直閃）
            if (_contrails != null)
                foreach (var c in _contrails)
                {
                    if (!c) continue;
                    if (hidden || !_jet.EngineOn) { c.emitting = false; continue; }
                    if (!c.emitting && _jet.Altitude > contrailAltitude + 100f) c.emitting = true;
                    else if (c.emitting && _jet.Altitude < contrailAltitude - 100f) c.emitting = false;
                    c.widthMultiplier = 0.7f + 0.6f * _jet.Throttle;
                }
            if (_vortices != null)
                foreach (var v in _vortices)
                    if (v) v.emitting = !hidden && _jet.GForce > 3.5f && _jet.AirDensity > 0.4f && _jet.AirSpeed > 100f;
            // 音障雲：照 Prandtl–Glauert 曲線決定這一幀噴幾顆霧
            VaporDensity = hidden ? 0f : PrandtlGlauert(mach) * Mathf.InverseLerp(0.25f, 0.6f, _jet.AirDensity);
            if (_vapor && VaporDensity > 0.01f)
            {
                int n = Mathf.RoundToInt(VaporDensity * 2400f * dt + Random.value);
                var ep = new ParticleSystem.EmitParams();
                for (int i = 0; i < n; i++)
                {
                    // 後緣最濃最亮（照片），往前漸淡；後面大顆、前面小顆
                    float s = 1f - Random.value * Random.value;
                    ep.position = VaporPoint(s, Random.value * Mathf.PI * 2f, Random.Range(-0.25f, 0.1f));
                    ep.startSize = Mathf.Lerp(0.9f, 2.6f, s) * Random.Range(0.8f, 1.2f);
                    ep.startLifetime = Random.Range(0.1f, 0.28f);
                    _vapor.Emit(ep, 1);
                }
            }
            // 音爆：馬赫錐掃過這台電腦的聽者
            if (!_listener) _listener = FindAnyObjectByType<AudioListener>();
            _boomCooldown -= dt;
            if (!_listener || hidden) { _inCone = false; return; }
            Vector3 d = _listener.transform.position - transform.position;
            float dist = d.magnitude;
            bool near = dist < 40f;                       // 自己就是駕駛（追車鏡頭 / 座艙）
            bool inCone = true;
            if (mach > 1.01f && !near)
            {
                float mu = Mathf.Asin(1f / mach);           // 馬赫角
                inCone = Vector3.Angle(d, -VelocityDir()) < mu * Mathf.Rad2Deg;
                if (inCone && !_inCone && dist < boomRange && _boomCooldown <= 0f && _boom)
                {
                    _boom.PlayOneShot(_boomClip, Mathf.Clamp01(1.2f - dist / boomRange));
                    _boomCooldown = 1.5f;
                    BoomCount++;
                }
            }
            _inCone = inCone;
            // 錐外（飛機在前面還沒飛到）連引擎聲都還沒傳到
            if (_roar) _roar.mute = !inCone;
            if (_whine) _whine.mute = !inCone;
        }

        Vector3 VelocityDir()
        {
            var rb = _jet.GetComponent<Rigidbody>();
            var v = rb && !rb.isKinematic ? rb.linearVelocity : transform.forward * _jet.AirSpeed;
            return v.sqrMagnitude > 1f ? v.normalized : transform.forward;
        }

        // ------------------------------------------------------------------ 減速傘
        // 兩頂十字形帶狀傘：充氣後是四瓣的圓頂（Commons 上 MiG-29 落地開傘照：圓頂、一圈圈帶狀縫、很多條傘繩收到一個匯流點），
        // 傘口直徑約 2.8 m、深 1.5 m（半尺寸；實機一頂 ~25 m²），傘艙到傘口約 7 m，一左一右、一前一後錯開。
        // 開傘：傘包拋出 → 傘繩拉直（0.35 s）→ 充氣張開、稍微過衝（0.5 s）；之後左右擺動、一鼓一鼓。
        // 拋傘：脫離飛機，往後飄、往下掉、塌掉，躺在跑道上 20 秒後消失。
        public Material chuteMaterial;            // HDRP Lit 雙面，米白尼龍
        static readonly Vector3 ChuteMount = new Vector3(0f, 1.55f, -5.6f);   // 兩片垂尾之間、噴口上方的傘艙
        Transform _chuteRoot;
        readonly Transform[] _canopy = new Transform[2];
        readonly LineRenderer[] _risers = new LineRenderer[2];
        float _chuteT = -1f;                      // 開傘後秒數，-1 = 收著
        bool _chuteFree;                          // 拋掉了（不跟飛機）
        Vector3 _chuteVel;
        float _chuteFreeT;
        const float CanopyR = 1.4f, CanopyDepth = 1.5f;
        const int CanopySeg = 24, CanopyLines = 12;
        static readonly Vector3[] CanopyRest = { new Vector3(-1.5f, 0.35f, 7.0f), new Vector3(1.6f, 0.15f, 7.8f) };

        void BuildChute()
        {
            if (!chuteMaterial) return;
            _chuteRoot = new GameObject("DragChute").transform;
            _chuteRoot.SetParent(transform, false);
            _chuteRoot.localPosition = ChuteMount;
            var mesh = CanopyMesh(CanopySeg, 8);
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject("Canopy_" + i);
                go.transform.SetParent(_chuteRoot, false);
                go.transform.localPosition = CanopyRest[i];
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = chuteMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                _canopy[i] = go.transform;
                var lr = new GameObject("Risers_" + i).AddComponent<LineRenderer>();
                lr.transform.SetParent(_chuteRoot, false);
                lr.sharedMaterial = chuteMaterial;
                lr.useWorldSpace = true;
                lr.widthMultiplier = 0.018f;
                lr.positionCount = 2 + CanopyLines * 2;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.numCapVertices = 0;
                _risers[i] = lr;
            }
            _chuteRoot.gameObject.SetActive(false);
        }

        // 傘口半徑：四瓣（十字形傘的四條臂充氣後鼓成四瓣）
        static float LobeR(float phi) => CanopyR * (1f + 0.1f * Mathf.Cos(4f * phi));

        // 傘衣：沿傘軸 s = 0（傘口，z = 0）→ 1（傘頂，z = CanopyDepth），繞一圈 phi。傘口朝 −z（朝飛機）。
        // 傘口收一點（傘繩拉著）、中段最胖、傘頂圓；uv.y = s（貼圖裡有一圈圈帶狀縫）
        static Mesh CanopyMesh(int seg, int rings)
        {
            var v = new System.Collections.Generic.List<Vector3>();
            var uv = new System.Collections.Generic.List<Vector2>();
            var tri = new System.Collections.Generic.List<int>();
            for (int r = 0; r <= rings; r++)
            {
                float s = r / (float)rings;
                float z = CanopyDepth * Mathf.Sin(s * Mathf.PI * 0.5f);
                // Max(0)：float 的 cos(π/2) 是 −4e-8，Pow 負數 = NaN → 整個網格邊界 NaN、被剔除看不到（實測）
                float shape = Mathf.Pow(Mathf.Max(0f, Mathf.Cos(s * Mathf.PI * 0.5f)), 0.6f) * Mathf.Lerp(0.85f, 1f, Mathf.Clamp01(s / 0.2f));
                for (int k = 0; k <= seg; k++)
                {
                    float phi = k / (float)seg * Mathf.PI * 2f;
                    float rad = LobeR(phi) * shape;
                    v.Add(new Vector3(Mathf.Cos(phi) * rad, Mathf.Sin(phi) * rad, z));
                    uv.Add(new Vector2(k / (float)seg, s));
                }
            }
            for (int r = 0; r < rings; r++)
            for (int k = 0; k < seg; k++)
            {
                int a = r * (seg + 1) + k, b = a + seg + 1;
                tri.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
            }
            var m = new Mesh { name = "ChuteCanopy" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        // 傘口上的傘繩接點（傘本地座標）
        static Vector3 SkirtPoint(int k)
        {
            float phi = k / (float)CanopyLines * Mathf.PI * 2f;
            float rad = LobeR(phi) * 0.85f;
            return new Vector3(Mathf.Cos(phi) * rad, Mathf.Sin(phi) * rad, 0f);
        }

        void UpdateChute(float dt)
        {
            if (!_chuteRoot) return;
            bool want = _jet.ChuteDeployed && !_jet.Hidden;
            if (want && (_chuteT < 0f || _chuteFree))
            {
                // 開傘（上一頂還躺在地上就直接收回來重用）
                _chuteT = 0f; _chuteFree = false;
                _chuteRoot.SetParent(transform, false);
                _chuteRoot.localPosition = ChuteMount;
                _chuteRoot.gameObject.SetActive(true);
            }
            else if (!want && _chuteT >= 0f && !_chuteFree)
            {
                if (_jet.Hidden) { _chuteRoot.gameObject.SetActive(false); _chuteT = -1f; return; }
                // 拋傘
                _chuteFree = true; _chuteFreeT = 0f;
                _chuteVel = VelocityDir() * _jet.AirSpeed * 0.85f;
                _chuteRoot.SetParent(null, true);
            }
            if (_chuteT < 0f) return;
            _chuteT += dt;

            Vector3 mount = _chuteFree ? _chuteRoot.position : transform.TransformPoint(ChuteMount);
            if (!_chuteFree)
            {
                // 傘拖在飛機後面、順著氣流；左右上下慢慢擺
                Vector3 back = -VelocityDir();
                back = Vector3.ProjectOnPlane(back, Vector3.up).sqrMagnitude > 0.01f ? back : -transform.forward;
                var rot = Quaternion.LookRotation(back, Vector3.up)
                          * Quaternion.Euler((Mathf.PerlinNoise(Time.time * 0.7f, 3f) - 0.5f) * 7f, (Mathf.PerlinNoise(Time.time * 0.6f, 7f) - 0.5f) * 10f, 0f);
                _chuteRoot.SetPositionAndRotation(mount, rot);
            }
            else
            {
                // 脫離：被風拖慢、往下掉，掉到地面就停
                _chuteFreeT += dt;
                _chuteVel *= Mathf.Exp(-1.6f * dt);
                _chuteVel += Vector3.down * 2.5f * dt;
                Vector3 pos = _chuteRoot.position + _chuteVel * dt;
                float ground = float.NegativeInfinity;
                if (Physics.Raycast(pos + Vector3.up * 3f, Vector3.down, out var hit, 10f, ~0, QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(transform))
                    ground = hit.point.y + 0.05f;
                if (pos.y < ground) { pos.y = ground; _chuteVel = Vector3.zero; }
                _chuteRoot.position = pos;
                // 傘衣往下倒、躺平
                var flat = Quaternion.LookRotation(Vector3.ProjectOnPlane(_chuteRoot.forward, Vector3.up).normalized, Vector3.up);
                _chuteRoot.rotation = Quaternion.Slerp(_chuteRoot.rotation, flat, dt * 1.5f);
                if (_chuteFreeT > 20f) { _chuteRoot.gameObject.SetActive(false); _chuteT = -1f; _chuteFree = false; _chuteRoot.SetParent(transform, false); return; }
            }

            // 開傘動畫：傘繩先拉直，再充氣（過衝一點），之後一鼓一鼓
            float extend = Mathf.SmoothStep(0.25f, 1f, Mathf.Clamp01(_chuteT / 0.35f));
            float inflate = Mathf.Clamp01((_chuteT - 0.25f) / 0.5f);
            float over = 1f + 0.12f * Mathf.Sin(inflate * Mathf.PI) * (1f - inflate * 0.5f);
            float collapse = _chuteFree ? Mathf.Clamp01(_chuteFreeT / 1.8f) : 0f;
            for (int i = 0; i < 2; i++)
            {
                if (!_canopy[i]) continue;
                float breathe = 1f + 0.04f * Mathf.Sin(Time.time * (11f + i * 2.3f) + i);
                float sxy = Mathf.Lerp(0.12f, 1f, Mathf.SmoothStep(0f, 1f, inflate)) * over * breathe * Mathf.Lerp(1f, 0.55f, collapse);
                float sz = Mathf.Lerp(2.2f, 1f, inflate) * Mathf.Lerp(1f, 0.4f, collapse);
                // 塌掉：壓扁貼地（傘本地 y 大約是垂直方向）
                _canopy[i].localScale = new Vector3(sxy, sxy * Mathf.Lerp(1f, 0.12f, collapse), sz);
                Vector3 rest = CanopyRest[i];
                _canopy[i].localPosition = new Vector3(rest.x * Mathf.Lerp(0.2f, 1f, inflate), Mathf.Lerp(rest.y, 0.15f, collapse), rest.z * extend * Mathf.Lerp(1f, 0.85f, collapse))
                                           + new Vector3(Mathf.PerlinNoise(Time.time * 1.3f, i * 5f) - 0.5f, Mathf.PerlinNoise(Time.time * 1.1f, i * 5f + 2f) - 0.5f, 0f) * 0.35f * (1f - collapse);
                _canopy[i].localRotation = Quaternion.LookRotation(_canopy[i].localPosition.normalized, Vector3.up);
                // 傘繩：傘艙 → 匯流點（傘口前約 4 m）→ 傘口一圈 12 個接點，來回畫
                var lr = _risers[i];
                if (!lr) continue;
                Vector3 conf = _chuteRoot.TransformPoint(_canopy[i].localPosition * 0.42f);
                lr.SetPosition(0, mount);
                lr.SetPosition(1, conf);
                for (int k = 0; k < CanopyLines; k++)
                {
                    lr.SetPosition(2 + k * 2, _canopy[i].TransformPoint(SkirtPoint(k)));
                    lr.SetPosition(3 + k * 2, conf);
                }
            }
        }

        // ------------------------------------------------------------------ 著陸燈 / 滑行燈
        // 實拍正面照：兩盞著陸燈在進氣道下方、主輪艙前（跟著主起落架收放），一盞滑行燈在前起落架上。
        // 起落架放下、引擎開著就亮；燈本體用一小片加法光點（白天也看得到亮點），晚上照得到地面。
        public Material lampMaterial;             // HDRP Unlit 加法，圓形柔光點
        public float lampGlow = 30f;
        Light[] _lamps;
        Renderer[] _lampGlowR;

        void BuildLamps()
        {
            // (掛在哪個零件, 本地位置（機體座標，半尺寸）, 往下打幾度, 光錐角, 燭光)
            var defs = new (string part, Vector3 pos, float down, float angle, float cd)[]
            {
                ("MiG25_MainGear_L", new Vector3(-0.55f, 0.62f, -0.72f), 4f, 22f, 150000f),
                ("MiG25_MainGear_R", new Vector3(0.55f, 0.62f, -0.72f), 4f, 22f, 150000f),
                ("MiG25_NoseGear", new Vector3(0f, 0.52f, 2.62f), 8f, 40f, 40000f),
            };
            _lamps = new Light[defs.Length];
            _lampGlowR = new Renderer[defs.Length];
            Mesh disc = lampMaterial ? FaceDiscMesh(16) : null;
            for (int i = 0; i < defs.Length; i++)
            {
                Transform parent = transform;
                foreach (var t in GetComponentsInChildren<Transform>(true)) if (t.name == defs[i].part) { parent = t; break; }
                var go = new GameObject("Lamp_" + defs[i].part);
                go.transform.SetPositionAndRotation(transform.TransformPoint(defs[i].pos), transform.rotation * Quaternion.Euler(defs[i].down, 0f, 0f));
                go.transform.SetParent(parent, true);
                var l = go.AddComponent<Light>();
                l.type = LightType.Spot;
                l.spotAngle = defs[i].angle;
                l.innerSpotAngle = defs[i].angle * 0.5f;
                l.color = new Color(1f, 0.93f, 0.8f);
                l.lightUnit = UnityEngine.Rendering.LightUnit.Candela;
                l.intensity = defs[i].cd;
                l.range = defs[i].angle < 30f ? 250f : 80f;
                l.shadows = LightShadows.None;
                l.enabled = false;
                _lamps[i] = l;
                if (disc)
                {
                    var g = new GameObject("Glow");
                    g.transform.SetParent(go.transform, false);
                    g.transform.localPosition = Vector3.forward * 0.03f;
                    g.transform.localScale = Vector3.one * (defs[i].cd > 100000f ? 0.09f : 0.06f);
                    g.AddComponent<MeshFilter>().sharedMesh = disc;
                    var r = g.AddComponent<MeshRenderer>();
                    r.sharedMaterial = lampMaterial;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    r.enabled = false;
                    _lampGlowR[i] = r;
                }
            }
        }

        void UpdateLamps()
        {
            if (_lamps == null) return;
            bool on = _jet.EngineOn && !_jet.Hidden && _jet.GearDown01 > 0.97f;
            for (int i = 0; i < _lamps.Length; i++)
            {
                if (_lamps[i]) _lamps[i].enabled = on;
                if (_lampGlowR[i]) Set(_lampGlowR[i], new Color(1f, 0.95f, 0.85f) * lampGlow, on);
            }
        }

        void LateUpdate()
        {
            UpdateFlames();
            UpdateDust();
            UpdateAudio(Time.deltaTime);
            UpdateSupersonic(Time.deltaTime);
            UpdateChute(Time.deltaTime);
            UpdateLamps();
        }
    }
}
