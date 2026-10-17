using Unity.Netcode;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Vehicles
{
    // Roadster（issue #8）第四階段：主動空力 + 車底吸地風扇。模式照 Tesla 發表會的說明：
    //
    // 1. 空力模式（X 切換；T 是聊天）
    //    低風阻（Low Drag）：尾翼收平貼在車尾，阻力最小（直線 / 極速）。
    //    高下壓力（High Downforce，＝賽道模式）：可變角度尾翼升起，角度跟著車速 / 過彎 / 煞車變；車身降低 4 cm（實車）。
    //    兩種模式下重煞車（高速）時尾翼都會立起來當空氣煞車。
    // 2. 吸地風扇（G 開 / 關；開著時自動選模式）
    //    低速吸地：低速時把車身吸住地面（跟車速無關的吸力，約 1.2 × 車重）→ 輪胎抓地力超過 2 g（起步、低速過彎）。
    //    高速流場：高速時風扇改成幫車底前後的氣流加速（下壓力跟速度平方成正比，吸力本身降下來）。
    //    吸力方向 = 朝向偵測到的路面（不是世界下方）→ 牆面也吸得住（發表會的垂直坡道）。
    //    有效程度 = 車底離地距離（離開地面很快就沒了）× 車身跟路面的夾角 × 裙邊密封；吸力不是空中穩定器。
    //    風扇用電（HUD 顯示）；風扇往後排氣，有一點點推力。
    // 3. 推進套件（DSTThrusterSystem）：極限加速（Shift）、姿態控制 / 飛行（E、Q/R、V）。
    //
    // 數值都是半尺寸世界的設計估算（Tesla 只說 >2 g、「接近 F1 的下壓力」），力一律用 AddForceAtPosition：
    // 尾翼的下壓力施在尾翼上、風扇吸力施在車底前後 → 前後配重 / 俯仰由物理自己算。
    [RequireComponent(typeof(Rigidbody))]
    public class DSTRoadsterAero : NetworkBehaviour
    {
        public enum AeroMode { LowDrag, HighDownforce }
        public enum FanMode { Off, LowSpeedSuction, HighSpeedFlow }

        public Transform wing;
        public Transform[] fanRotors = new Transform[0];

        [Header("Active wing")]
        public float wingRaise = 0.15f;              // m（半尺寸）：賽道模式升起的高度
        public float wingBasePitch = 14f;            // 度：高下壓力模式的基本攻角
        public float wingCornerPitch = 8f;           // 大側向 G 時再加
        public float wingBrakePitch = 38f;           // 空氣煞車
        public float wingHighSpeedPitch = 9f;        // 高速時收小，免得阻力太大
        public float wingRate = 70f;                 // 度/秒（Optimus 的致動器）
        public float wingRaiseRate = 0.25f;          // m/s
        public float wingDownCoef = 0.33f;           // N/(m/s)²，在基本攻角時
        public float wingDragCoef = 0.045f;
        public float rideLowerTrack = 0.008f;        // m（半尺寸，實車 1.6 cm；1.2 cm 時吸地滿載車底板碰地，實測）

        [Header("Fan suction")]
        public bool fansEnabled = false;             // 預設關（玩家要求）：按 G 開
        public float suctionWeights = 1.2f;          // 低速吸地：× 車重
        public float highSpeedSuctionWeights = 1.0f; // 高速流場：吸力不減，再疊上跟速度平方成正比的流場下壓力
        public float flowCoef = 0.25f;               // 高速流場：額外下壓力 N/(m/s)²
        public float lowToHighSpeed = 22f;           // m/s：從低速吸地切到高速流場（22–30 之間混合）
        public float sealGap = 0.06f;                // 車底離地在這以內 = 完全有效
        public float lostGap = 0.16f;                // 超過這個高度吸力歸零
        public float seal = 0.92f;                   // 裙邊密封
        public float fanSpinUp = 0.5f;               // 秒
        public float fanPowerW = 3500f;              // 全速時用電（半尺寸）
        public float fanThrustWeights = 0.03f;       // 風扇往後排氣的推力

        // ------------------------------------------------------------------ 狀態
        public AeroMode Aero { get; private set; } = AeroMode.LowDrag;
        public FanMode Fans { get; private set; } = FanMode.Off;
        public float Suction { get; private set; }       // N（目前的吸力，含有效程度）
        public float WingDownforce { get; private set; }
        public float WingPitch => _pitch;
        public float FanThrottle => _fan;
        public float FanPowerW => _fan * fanPowerW;
        public float Effectiveness { get; private set; }
        public string HudText
        {
            get
            {
                string a = Aero == AeroMode.HighDownforce ? "高下壓力" : "低風阻";
                if (_brakeWing) a += "·空氣煞車";
                string f = !fansEnabled ? "吸地 關" : Fans == FanMode.HighSpeedFlow ? "高速流場" : "低速吸地";
                float g = _rb ? (Suction + WingDownforce) / (_rb.mass * 9.81f) : 0f;
                return $"空力 {a}　{f}{(fansEnabled ? $" {Effectiveness * 100f:0}%" : "")}　下壓 {g:0.00} g　風扇 {FanPowerW / 1000f:0.0} kW";
            }
        }

        public void SetAeroMode(AeroMode m) => Aero = m;
        public void SetFans(bool on) => fansEnabled = on;

        readonly NetworkVariable<int> _net = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        Rigidbody _rb;
        DSTRoadsterController _car;
        Vector3 _wingRestPos;
        Quaternion _wingRestRot;
        float _pitch, _raise, _fan, _spin, _pitchSign = 1f, _netTimer;
        bool _brakeWing;
        Quaternion[] _rotorRest;
        AudioSource _turbine, _servo;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _car = GetComponent<DSTRoadsterController>();
            if (wing)
            {
                _wingRestPos = transform.InverseTransformPoint(wing.position);
                _wingRestRot = Quaternion.Inverse(transform.rotation) * wing.rotation;
                // 尾翼繞自己的本地 X 轉；哪個方向是「後緣往上」由幾何決定（不靠記號表）
                var r = wing.GetComponent<Renderer>();
                if (r)
                {
                    Vector3 te = r.bounds.center - transform.forward * r.bounds.extents.z;   // 後緣（車尾方向）
                    Vector3 arm = te - wing.position;
                    Vector3 turned = Quaternion.AngleAxis(10f, wing.right) * arm;
                    _pitchSign = Vector3.Dot(turned - arm, transform.up) >= 0f ? 1f : -1f;
                }
            }
            _rotorRest = new Quaternion[fanRotors.Length];
            for (int i = 0; i < fanRotors.Length; i++)
                if (fanRotors[i]) _rotorRest[i] = Quaternion.Inverse(transform.rotation) * fanRotors[i].rotation;
            BuildAudio();
        }

        // ------------------------------------------------------------------ 輸入
        void Update()
        {
            if (_car && _car.PlayerControlled && !_car.externalInput) ReadInput();
            Animate(Time.deltaTime);
        }

        void ReadInput()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = DUST.UI.DSTChat.Typing ? null : Keyboard.current;
            bool aero = kb != null && kb.xKey.wasPressedThisFrame;     // T 是聊天
            bool fans = kb != null && kb.gKey.wasPressedThisFrame;
            var gp = Gamepad.current;
            if (gp != null)
            {
                aero |= gp.dpad.down.wasPressedThisFrame;
                fans |= gp.leftStickButton.wasPressedThisFrame;
            }
            if (aero) Aero = Aero == AeroMode.LowDrag ? AeroMode.HighDownforce : AeroMode.LowDrag;
            if (fans) fansEnabled = !fansEnabled;
#endif
        }

        // ------------------------------------------------------------------ 物理
        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (_rb.isKinematic)
            {
                if (!IsSpawned || IsOwner) return;   // 只有別人開的車才照網路的狀態（換駕駛那一下別把自己的模式蓋掉）
                int s = _net.Value;
                Aero = (s & 1) != 0 ? AeroMode.HighDownforce : AeroMode.LowDrag;
                fansEnabled = (s & 2) != 0;
                _fan = ((s >> 8) & 255) / 255f;
                _pitch = ((s >> 16) & 255) / 255f * 45f;
                _raise = ((s >> 24) & 127) / 127f * wingRaise;
                return;
            }
            float v = Vector3.Dot(_rb.linearVelocity, transform.forward);
            float speed = Mathf.Abs(v);
            float m = _rb.mass, g = Physics.gravity.magnitude;

            // ---- 尾翼：模式 + 主動角度
            _brakeWing = speed > 25f && _car && _car.Brake > 0.6f;
            float wantPitch = 0f, wantRaise = 0f;
            if (Aero == AeroMode.HighDownforce || _brakeWing)
            {
                wantRaise = wingRaise;
                float corner = _car ? Mathf.Clamp01(Mathf.Abs(_car.LatAccel) / (2f * g)) : 0f;
                wantPitch = Mathf.Lerp(wingBasePitch, wingHighSpeedPitch, Mathf.InverseLerp(45f, 70f, speed)) + corner * wingCornerPitch;
                if (_brakeWing) wantPitch = wingBrakePitch;
            }
            _pitch = Mathf.MoveTowards(_pitch, wantPitch, wingRate * dt);
            _raise = Mathf.MoveTowards(_raise, wantRaise, wingRaiseRate * dt);
            if (_car) _car.rideLower = Mathf.MoveTowards(_car.rideLower, Aero == AeroMode.HighDownforce ? rideLowerTrack : 0f, 0.02f * dt);

            // 尾翼力：升起來才有效（收平時貼在車尾 = 車身的一部分）
            float deployed = wingRaise > 0f ? _raise / wingRaise : 0f;
            float a = _pitch * Mathf.Deg2Rad, a0 = wingBasePitch * Mathf.Deg2Rad;
            float liftShape = Mathf.Sin(2f * Mathf.Min(a, 0.7f)) / Mathf.Sin(2f * a0);   // 攻角太大會失速（空氣煞車時下壓力不再增加）
            float dragShape = 0.2f + Mathf.Pow(Mathf.Sin(a) / Mathf.Sin(a0), 2f);
            WingDownforce = deployed * wingDownCoef * liftShape * v * v;
            float wingDrag = deployed * wingDragCoef * dragShape * v * v;
            Vector3 wingPoint = wing ? wing.position : transform.TransformPoint(new Vector3(0f, 0.45f, -0.95f));
            _rb.AddForceAtPosition(-transform.up * WingDownforce - _rb.linearVelocity.normalized * wingDrag, wingPoint);

            // ---- 風扇
            float fanTarget = fansEnabled ? 1f : 0f;
            _fan = Mathf.MoveTowards(_fan, fanTarget, dt / Mathf.Max(fanSpinUp, 0.05f));
            float high = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(lowToHighSpeed, lowToHighSpeed + 8f, speed));
            Fans = !fansEnabled ? FanMode.Off : high > 0.5f ? FanMode.HighSpeedFlow : FanMode.LowSpeedSuction;
            // 有效程度：車底到路面的距離（沿車身向下打射線）、車身和路面夾角、密封
            // 從車身裡面往下打（吸地 + 降車高時車底只離地 1~2 cm，射線起點太低會從地面下面打出去 → 有效程度一直跳 0 / 0.92，實測）
            const float startY = 0.25f, floorY = 0.043f;    // 射線起點、車底板（車身座標）
            Vector3 from = transform.TransformPoint(new Vector3(0f, startY, -0.1f));
            float gap = lostGap + 1f;
            RaycastHit hit = default;
            int nh = Physics.RaycastNonAlloc(from, -transform.up, _hits, startY + lostGap + 0.3f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < nh; i++)
            {
                if (_hits[i].transform.IsChildOf(transform) || _hits[i].distance >= best) continue;
                best = _hits[i].distance; hit = _hits[i];
            }
            if (best < float.MaxValue) gap = best - (startY - floorY);
            float clearance = Mathf.Clamp01(1f - (gap - sealGap) / (lostGap - sealGap));
            Vector3 n = _car ? _car.GroundNormal : Vector3.up;
            if (gap <= lostGap && hit.collider) n = hit.normal;   // 吸向射線打到的面（牆也算）
            float align = Mathf.Clamp01(Vector3.Dot(transform.up, n));
            Effectiveness = _fan * clearance * align * align * seal;
            float suctionN = Mathf.Lerp(suctionWeights, highSpeedSuctionWeights, high) * m * g + high * flowCoef * v * v;
            Suction = suctionN * Effectiveness;
            if (Suction > 0f)
            {
                // 往路面吸（牆上也一樣），施力點在車底前後（後面多一點：風扇在後面）
                Vector3 f = -n * Suction;
                _rb.AddForceAtPosition(f * 0.45f, transform.TransformPoint(new Vector3(0f, 0.05f, 0.45f)));
                _rb.AddForceAtPosition(f * 0.55f, transform.TransformPoint(new Vector3(0f, 0.05f, -0.55f)));
            }
            // 風扇往後排氣
            if (_fan > 0.01f) _rb.AddForceAtPosition(transform.forward * fanThrustWeights * m * g * _fan, transform.TransformPoint(new Vector3(0f, 0.13f, -1.0f)));

            if (IsSpawned && IsOwner && (_netTimer -= dt) <= 0f)
            {
                _netTimer = 0.1f;
                _net.Value = (Aero == AeroMode.HighDownforce ? 1 : 0) | (fansEnabled ? 2 : 0)
                             | Mathf.RoundToInt(_fan * 255f) << 8 | Mathf.Clamp(Mathf.RoundToInt(_pitch / 45f * 255f), 0, 255) << 16
                             | Mathf.Clamp(Mathf.RoundToInt(_raise / Mathf.Max(wingRaise, 1e-3f) * 127f), 0, 127) << 24;
            }
        }

        // ------------------------------------------------------------------ 外觀 / 聲音
        void Animate(float dt)
        {
            if (wing)
            {
                var rot = transform.rotation * _wingRestRot;
                Vector3 axis = rot * Vector3.right;
                wing.SetPositionAndRotation(transform.TransformPoint(_wingRestPos) + transform.up * _raise,
                                            Quaternion.AngleAxis(_pitch * _pitchSign, axis) * rot);
            }
            _spin += _fan * 2600f * dt;                     // 度/秒（顯示用，不是真轉速，免得閃爍）
            for (int i = 0; i < fanRotors.Length; i++)
                if (fanRotors[i])
                    fanRotors[i].rotation = Quaternion.AngleAxis(_spin, -transform.forward) * (transform.rotation * _rotorRest[i]);
            if (_turbine)
            {
                _turbine.volume = Mathf.MoveTowards(_turbine.volume, _fan * 0.32f, dt);
                _turbine.pitch = 0.6f + 0.5f * _fan;
            }
            if (_servo)
            {
                bool moving = Mathf.Abs(_pitch - _lastPitch) > 0.01f || Mathf.Abs(_raise - _lastRaise) > 1e-4f;
                _servo.volume = Mathf.MoveTowards(_servo.volume, moving ? 0.25f : 0f, dt * 4f);
                _lastPitch = _pitch; _lastRaise = _raise;
            }
        }
        float _lastPitch, _lastRaise;
        readonly RaycastHit[] _hits = new RaycastHit[8];

        void BuildAudio()
        {
            _turbine = Loop("FanTurbine", Synth(2350f, 3520f, 0.25f, 9), new Vector3(0f, 0.15f, -1.0f));
            _servo = Loop("WingActuator", Synth(180f, 360f, 0.15f, 4), new Vector3(0f, 0.45f, -0.9f));
        }

        AudioSource Loop(string name, AudioClip clip, Vector3 local)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = local;
            var a = go.AddComponent<AudioSource>();
            a.clip = clip; a.loop = true; a.volume = 0f; a.spatialBlend = 1f;
            a.rolloffMode = AudioRolloffMode.Logarithmic; a.minDistance = 2f; a.maxDistance = 250f;
            a.playOnAwake = false;
            a.Play();
            return a;
        }

        // 渦輪 / 致動器：兩個正弦 + 噪音，1 秒循環、頭尾交叉淡化
        static AudioClip Synth(float f1, float f2, float noise, int seed)
        {
            const int rate = 22050, len = rate, fade = rate / 8;
            var d = new float[len + fade];
            var rng = new System.Random(seed);
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)rate;
                d[i] = 0.35f * Mathf.Sin(2f * Mathf.PI * f1 * t) + 0.18f * Mathf.Sin(2f * Mathf.PI * f2 * t)
                     + noise * ((float)rng.NextDouble() * 2f - 1f);
            }
            var o = new float[len];
            for (int i = 0; i < len; i++)
            {
                float v = d[i];
                if (i < fade) { float w = i / (float)fade; v = v * w + d[len + i] * (1f - w); }
                o[i] = v;
            }
            var clip = AudioClip.Create("Synth" + seed, len, 1, rate, false);
            clip.SetData(o, 0);
            return clip;
        }
    }
}
