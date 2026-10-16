using DUST.Player;
using DUST.World;
using Unity.Netcode;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Vehicles
{
    // 米格-25（issue #7）：簡化的空氣動力學 —— 推力、阻力、隨攻角變化的升力（16° 失速）、側滑力；
    // 操縱用「角速度指令」（像真機的電傳：桿量 = 想要的轉動速度，受 5 G 和空速限制），
    // 放開桿子就保持姿態。地面：三個起落架各自做彈簧阻尼懸吊、前輪轉向、煞車、側向摩擦。
    // 半尺寸世界（長度 ×0.5 → 速度 ×√0.5、質量 ×1/8、重力不變）：起飛 ~65 m/s、滑跑 ~500 m。
    // 超音速（issue #7 第三步）：空氣密度隨高度指數變小（ρ = e^(−h/2124 m)：遊戲 5 km ≈ 實機 20 km 的空氣），
    // 升力 / 阻力都乘 ρ；阻力係數隨馬赫數變（0.85 起穿音速阻力暴增、1.05 最大、之後下降）；
    // 推力 × ρ^0.44（高空推力變小）×（1 + 1.15 M）（衝壓），M 2.80 以上推力淡出（R-15 的溫度上限）。
    // 結果（數值解，tune2.js）：海平面極速 ~237 m/s（M 0.99，跟實機低空限速一樣）、3 km 以上 M 2.85；
    // 5 km 高度從 M 1 加速到 M 2.8 約 90 秒（第一版照實機算要 7 分鐘以上，太慢）。
    // 音速：半尺寸世界速度 ×√0.5 → 海平面 240 m/s、高空 209 m/s。
    //
    // 操作照 GTA V（PC 預設鍵位，GTABoom 鍵位表）：W 加油門（按住才有推力，放開回慢車）、S 減速（空中減速板 / 地面煞車）、
    // A/D 方向舵（地面 = 前輪轉向）、數字鍵 8/5 俯仰（8 = 推桿低頭、5 = 拉桿抬頭；也可以用 ↑/↓）、
    // 數字鍵 4/6 滾轉（也可以用 ←/→）、按住滑鼠左鍵 = 滑鼠操縱（左右滾轉、前後俯仰）、Space 煞車、G 起落架、C 視角。
    // 手把：RT 油門、LT 減速、左搖桿 俯仰 / 滾轉、LB/RB 方向舵、A 煞車、X 起落架。
    // 手感照 GTA 的戰鬥機（玩家要求，2026-10-15）：轉得快、「指哪飛哪」（速度方向一直被拉向機頭，轉彎幾乎不掉速）、
    // 放開桿子保持姿態、不自動改平；低速才會失速掉頭。
    //
    // 活動零件（Blender 已經把原點放在樞紐、一律繞本地 X 軸轉；正負號見 DSTMiG25Setup）。
    // 在遺跡二（沒有跑道）整架藏起來不動，只在隨機世界出現（DSTWorld.Relocate 停到跑道頭）。
    [RequireComponent(typeof(Rigidbody))]
    public class DSTJetController : NetworkBehaviour, IDSTDrivable
    {
        public Transform centerOfMass;

        [Header("Engine (half-scale world)")]
        public float idleThrust = 0.04f;        // × 重量
        public float dryThrust = 0.36f;         // 不開後燃器的最大推力 × 重量
        public float afterburnerThrust = 0.62f; // 實機 2×R-15 後燃器推重比約 0.55~0.65
        public float afterburnerFrom = 0.85f;   // 油門超過這裡 = 後燃器
        public float throttleRate = 0.6f;       // 每秒

        [Header("Aerodynamics")]
        public float liftK = 11.9f;             // ½ρS（半尺寸）：65 m/s、攻角 10° 剛好離地
        public float cl0 = 0.15f;               // 機翼 +2° 安裝角
        public float clPerRad = 4.6f;
        public float stallDeg = 16f;
        public float dragK = 0.508f;            // 零升阻力（× ρ × Cd(M)），跟下面三個一起用數值解調出來（tune2.js）
        public float inducedK = 0.09f;
        public float sideK = 6f;                // 側滑 → 側向力（機身 + 雙垂尾）
        public float densityScaleHeight = 2124f;
        public float thrustLapse = 0.44f;
        public float ramFactor = 1.15f;
        public float machLimit = 2.80f;      // 推力從這裡淡出、到 +0.15 歸零 → 極速 ~M 2.85
        public float soundSpeedSea = 240f;
        public float soundSpeedHigh = 209f;     // 2.7 km 以上

        [Header("Handling (GTA-style arcade: snappy, flies where the nose points)")]
        public float maxPitchRate = 45f;        // 度/秒（原本照實機 16，玩家要 GTA 手感）
        public float maxRollRate = 150f;
        public float maxYawRate = 25f;
        public float maxG = 16f;                // 高速時俯仰角速度上限 = maxG·g / v（200 m/s 約 45°/s：筋斗 ~8 秒；M 2.8 約 15°/s）
        public float arcadeGrip = 2.2f;         // 速度方向往機頭方向轉的速率（1/秒）：越大越「指哪飛哪」
        public float airbrakeDrag = 1.2f;       // S 減速：額外阻力（× 零升阻力）
        public float throttleUpRate = 1.6f;     // W 按住：油門 0 → 1 約 0.6 秒
        public float throttleDownRate = 0.8f;   // 放開：回到慢車
        public float controlFullSpeed = 55f;    // 這個速度以上舵面完全有效
        public float rateGain = 6f;             // 角速度跟上指令的快慢（GTA：幾乎立刻）
        public float alphaLimitUp = 14f;        // 攻角限制器：拉桿超過這個攻角就不再加（防止失速尾旋）
        public float alphaLimitDown = -8f;

        [Header("Ground")]
        public float suspensionTravel = 0.25f;  // 油壓支柱行程：靜載重壓到這裡（0.12 太短，硬一點落地就彈起來，玩家回報）
        public float compressionDamping = 1.0f; // 阻尼比：壓縮時吸收衝擊
        public float reboundDamping = 3.0f;     // 回彈時阻尼大很多（油壓支柱單向阀）
        public float reboundForceLimit = 1.3f;  // 回彈中最多只推 1.3 倍靜載重：不會把飛機彈回空中
        public float wheelFriction = 0.9f;
        public float brakeFriction = 0.55f;
        public float rollingResistance = 0.015f;
        public float noseSteerDeg = 35f;
        public float gearTime = 5f;             // 收 / 放起落架秒數

        [Header("Drag chute")]
        // 實機：機尾兩片垂尾之間的傘艙，兩頂十字形減速傘（testpilot.ru：兩頂、共約 50 m²），
        // 主輪落地時左腹鰭的探針碰到跑道就自動拋出；滑跑慢下來後拋掉
        public float chuteDecel = 3.2f;         // 70 m/s 時的減速度（m/s²），跟速度平方成正比
        public float chuteDeploySpeed = 30f;    // 落地時要比這個快才會開傘
        public float chuteJettisonSpeed = 12f;  // 慢到這裡拋傘

        // ------------------------------------------------------------------ IDSTDrivable
        public bool PlayerControlled
        {
            get => _controlled;
            set
            {
                _controlled = value;
                if (!value) { _pitchIn = _rollIn = _yawIn = 0f; _throttle = 0f; _brake = 1f; _airbrake = 0f; _mouseStick = Vector2.zero; MouseFlightActive = false; }
                else _brake = 0f;
            }
        }

        public float SpeedKmh => !_rb ? 0f : _rb.isKinematic ? _syncedSpeedKmh : Vector3.Dot(_rb.linearVelocity, transform.forward) * 3.6f;
        public string ControlsHint => "[F] 下機  [W] 油門  [S] 減速  [A/D] 方向舵  [數字鍵8/5 或 ↑/↓] 俯仰  [數字鍵4/6 或 ←/→] 滾轉  [按住滑鼠左鍵] 滑鼠操縱  [G] 起落架  [C] 視角";
        public string EnterHint => "[F] 登機";

        public bool CanExit(float maxExitSpeedKmh, out string reason)
        {
            if (!Grounded) { reason = "先降落才能下機"; return false; }
            reason = "先停下來才能下機";
            return Mathf.Abs(SpeedKmh) <= maxExitSpeedKmh;
        }

        // 給特效 / HUD
        public float Throttle => _rb && _rb.isKinematic ? _netThrottle.Value / 255f : _throttle;
        public float Afterburner01 => Mathf.InverseLerp(afterburnerFrom, 1f, Throttle);
        public bool EngineOn => _controlled || (_rb && _rb.isKinematic && _netThrottle.Value > 0);
        public bool Grounded { get; private set; }
        public float GearDown01 => _gear01;
        public float AltitudeAGL { get; private set; }
        public float AirSpeed => _rb ? (_rb.isKinematic ? Mathf.Abs(_syncedSpeedKmh) / 3.6f : _rb.linearVelocity.magnitude) : 0f;
        public float AngleOfAttackDeg { get; private set; }
        public float Altitude { get; private set; }     // 跑道（營地）高度以上，公尺
        public float AirDensity { get; private set; } = 1f;
        public float Mach { get; private set; }
        public event System.Action<bool> SoundBarrier;  // true = 加速突破音障、false = 減速回到次音速
        public bool Stalling { get; private set; }
        public float GForce { get; private set; }
        public bool Hidden => _hidden;
        public event System.Action<float> TouchDown;   // 參數 = 落地垂直速度 m/s
        public bool ChuteDeployed => _chute;            // 減速傘張開中（網路同步，特效用）
        // 除錯：各輪壓縮量 / 靜載重
        public string DebugWheels
        {
            get
            {
                var s = "";
                if (_wheels != null) foreach (var w in _wheels) s += $"{(w.nose ? "N" : "M")} z{w.local.z:F2} c{(w.contact ? w.compression : 0f):F3} load{w.load:F0}  ";
                return s;
            }
        }

        // 網路：駕駛那台寫，其他人用來動零件 / 特效
        readonly NetworkVariable<byte> _netThrottle = new NetworkVariable<byte>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<int> _netControls = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        Rigidbody _rb;
        bool _controlled;
        float _throttle, _pitchIn, _rollIn, _yawIn, _brake = 1f;
        float _airbrake;                                // S 按住（空中減速）
        Vector2 _mouseStick;                            // 滑鼠操縱的虛擬搖桿（-1..1，放開慢慢回中）
        /// <summary>這一幀駕駛正在用滑鼠操縱（按住左鍵）——追車鏡頭這時不吃滑鼠轉視角。</summary>
        public static bool MouseFlightActive { get; private set; }
        bool _gearCmdDown = true;
        bool _chute, _chuteUsed;                        // 這次落地已經用過傘了（再起飛才重置）
        float _gear01 = 1f;
        bool _hidden;
        Vector3 _lastPosition;
        float _syncedSpeedKmh;
        float _netTimer;
        Vector3 _prevVel;

        // 外部（AI、測試）直接給輸入；打開 externalInput 時不讀鍵盤 / 手把
        [HideInInspector] public bool externalInput;

        public void SetInput(float throttle, float pitch, float roll, float yaw, float brake)
        {
            _throttle = Mathf.Clamp01(throttle);
            _pitchIn = Mathf.Clamp(pitch, -1f, 1f);
            _rollIn = Mathf.Clamp(roll, -1f, 1f);
            _yawIn = Mathf.Clamp(yaw, -1f, 1f);
            _brake = Mathf.Clamp01(brake);
        }

        public void SetGear(bool down) => _gearCmdDown = down || Grounded;

        // ------------------------------------------------------------------ 起落架 / 零件
        struct Wheel
        {
            public Vector3 local;      // 機輪中心（相對機體）
            public float radius, restLength, k, c, load;
            public bool nose;
            public float compression;
            public bool contact;
        }
        Wheel[] _wheels;

        struct Part { public Transform t; public Quaternion rest; public Vector3 restPos; }
        Part _stabL, _stabR, _ailInL, _ailInR, _ailOutL, _ailOutR, _flapL, _flapR, _rudL, _rudR, _canopy,
             _noseGear, _mainGearL, _mainGearR, _noseDoorL, _noseDoorR, _outDoorL, _outDoorR, _inDoorL, _inDoorR, _rampL, _rampR;
        Transform _nozzleL, _nozzleR;
        Transform[] _wheelTs;
        float[] _wheelRadius;
        float _flap01, _canopy01;
        Renderer[] _renderers;
        Collider[] _colliders;

        Part P(string name)
        {
            var t = Find(transform, name);
            return new Part { t = t, rest = t ? t.localRotation : Quaternion.identity, restPos = t ? t.localPosition : Vector3.zero };
        }

        static Transform Find(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (centerOfMass) _rb.centerOfMass = transform.InverseTransformPoint(centerOfMass.position);
            _lastPosition = transform.position;
            _renderers = GetComponentsInChildren<Renderer>(true);
            _colliders = GetComponentsInChildren<Collider>(true);

            _stabL = P("MiG25_Stabilator_L"); _stabR = P("MiG25_Stabilator_R");
            _ailInL = P("MiG25_AileronIn_L"); _ailInR = P("MiG25_AileronIn_R");
            _ailOutL = P("MiG25_AileronOut_L"); _ailOutR = P("MiG25_AileronOut_R");
            _flapL = P("MiG25_Flap_L"); _flapR = P("MiG25_Flap_R");
            _rudL = P("MiG25_Rudder_L"); _rudR = P("MiG25_Rudder_R");
            _canopy = P("MiG25_CanopyHood");
            _noseGear = P("MiG25_NoseGear"); _mainGearL = P("MiG25_MainGear_L"); _mainGearR = P("MiG25_MainGear_R");
            _noseDoorL = P("MiG25_NoseDoor_L"); _noseDoorR = P("MiG25_NoseDoor_R");
            _outDoorL = P("MiG25_MainDoorOuter_L"); _outDoorR = P("MiG25_MainDoorOuter_R");
            _inDoorL = P("MiG25_MainDoorInner_L"); _inDoorR = P("MiG25_MainDoorInner_R");
            _rampL = P("MiG25_IntakeRamp_L"); _rampR = P("MiG25_IntakeRamp_R");
            _nozzleL = Find(transform, "MiG25_Nozzle_L"); _nozzleR = Find(transform, "MiG25_Nozzle_R");

            BuildWheels();
        }

        // 懸吊點 = 各機輪中心；靜止載重依重心前後分配，彈簧在靜載重下壓縮 suspensionTravel
        void BuildWheels()
        {
            var names = new[] { "MiG25_NoseWheel_L", "MiG25_NoseWheel_R", "MiG25_MainWheel_L", "MiG25_MainWheel_R" };
            _wheelTs = new Transform[names.Length];
            _wheelRadius = new float[names.Length];
            var list = new System.Collections.Generic.List<Wheel>();
            // 用重心標記算（Awake 時 Rigidbody.centerOfMass 可能還是自動算的值 → 前輪分到太少、機頭下沉，實測）
            Vector3 com = centerOfMass ? transform.InverseTransformPoint(centerOfMass.position) : _rb.centerOfMass;
            float nz = 0f, mz = 0f;
            for (int i = 0; i < names.Length; i++)
            {
                var t = Find(transform, names[i]);
                _wheelTs[i] = t;
                if (!t) continue;
                var r = t.GetComponent<Renderer>();
                var b = r ? r.bounds : new Bounds(t.position, Vector3.one * 0.5f);
                float radius = b.extents.y;
                _wheelRadius[i] = radius;
                var local = transform.InverseTransformPoint(b.center);
                bool nose = i < 2;
                if (nose) nz = local.z; else mz = local.z;
                list.Add(new Wheel { local = local, radius = radius, nose = nose, restLength = radius + suspensionTravel });
            }
            _wheels = list.ToArray();
            // 重心在主輪前面一點：前輪分到的比例
            float noseShare = Mathf.Clamp(Mathf.Abs(nz - mz) > 0.01f ? (com.z - mz) / (nz - mz) : 0.1f, 0.04f, 0.4f);
            float w = _rb.mass * Physics.gravity.magnitude;
            for (int i = 0; i < _wheels.Length; i++)
            {
                float load = w * (_wheels[i].nose ? noseShare * 0.5f : (1f - noseShare) * 0.5f);
                _wheels[i].load = load;
                _wheels[i].k = load / suspensionTravel;
                _wheels[i].c = 2f * Mathf.Sqrt(_wheels[i].k * load / Physics.gravity.magnitude);   // 臨界阻尼，再乘阻尼比
            }
        }

        // ------------------------------------------------------------------ 網路
        public override void OnNetworkSpawn()
        {
            _lastPosition = transform.position;
        }

        // ------------------------------------------------------------------ 輸入
        void Update()
        {
            UpdateHidden();
            if (_hidden) return;
            if (_controlled && !externalInput) ReadPlayerInput(Time.deltaTime);
            Animate(Time.deltaTime);
        }

        void ReadPlayerInput(float dt)
        {
            float thrUp = 0f, slow = 0f, p = 0f, r = 0f, y = 0f, brake = 0f;
            bool gear = false, mouseFly = false;
#if ENABLE_INPUT_SYSTEM
            var kb = DUST.UI.DSTChat.Typing ? null : Keyboard.current;   // 聊天打字中不吃鍵盤
            if (kb != null)
            {
                if (kb.wKey.isPressed) thrUp = 1f;
                if (kb.sKey.isPressed) slow = 1f;
                if (kb.dKey.isPressed) y += 1f;
                if (kb.aKey.isPressed) y -= 1f;
                if (kb.numpad5Key.isPressed || kb.downArrowKey.isPressed) p += 1f;   // 拉桿 = 抬頭
                if (kb.numpad8Key.isPressed || kb.upArrowKey.isPressed) p -= 1f;
                if (kb.numpad6Key.isPressed || kb.rightArrowKey.isPressed) r += 1f;
                if (kb.numpad4Key.isPressed || kb.leftArrowKey.isPressed) r -= 1f;
                if (kb.spaceKey.isPressed) brake = 1f;
                gear |= kb.gKey.wasPressedThisFrame;
            }
            // 滑鼠操縱（GTA：按住左鍵）：滑鼠移動推虛擬搖桿，放開慢慢回中；往前推滑鼠 = 低頭
            var mouse = Mouse.current;
            if (mouse != null && Cursor.lockState == CursorLockMode.Locked && mouse.leftButton.isPressed && !DUST.UI.DSTChat.ConsumesKeys)
            {
                mouseFly = true;
                var d = mouse.delta.ReadValue();
                _mouseStick = new Vector2(Mathf.Clamp(_mouseStick.x + d.x * 0.012f, -1f, 1f), Mathf.Clamp(_mouseStick.y + d.y * 0.012f, -1f, 1f));
            }
            _mouseStick *= Mathf.Exp(-(mouseFly ? 2.5f : 12f) * dt);
            r += _mouseStick.x;
            p -= _mouseStick.y;
            var gp = Gamepad.current;
            if (gp != null)
            {
                thrUp = Mathf.Max(thrUp, gp.rightTrigger.ReadValue());
                slow = Mathf.Max(slow, gp.leftTrigger.ReadValue());
                r += gp.leftStick.x.ReadValue();
                p += -gp.leftStick.y.ReadValue();                // 搖桿往後拉 = 抬頭
                if (gp.rightShoulder.isPressed) y += 1f;
                if (gp.leftShoulder.isPressed) y -= 1f;
                if (gp.buttonSouth.isPressed) brake = 1f;
                gear |= gp.buttonWest.wasPressedThisFrame;
            }
#else
            thrUp = Mathf.Max(0f, Input.GetAxis("Vertical"));
            slow = Mathf.Max(0f, -Input.GetAxis("Vertical"));
            y = Input.GetAxis("Horizontal");
            brake = Input.GetKey(KeyCode.Space) ? 1f : 0f;
            gear = Input.GetKeyDown(KeyCode.G);
#endif
            MouseFlightActive = mouseFly;
            ApplyPilotInput(thrUp, slow, p, r, y, brake, gear, dt);
        }

        /// <summary>
        /// 駕駛輸入（已經從鍵盤 / 手把 / 滑鼠讀好）→ 油門、舵面、煞車。測試也直接呼叫這個（編輯器裡注入按鍵到不了遊戲）。
        /// thrUp = W / RT（0..1）、slow = S / LT（0..1）、pitch 正 = 抬頭、roll 正 = 右滾、yaw 正 = 右偏。
        /// </summary>
        public void ApplyPilotInput(float thrUp, float slow, float pitch, float roll, float yaw, float brake, bool gearToggle, float dt)
        {
            // GTA 式油門：按住 W 推上去（扳機按多深就推到多少），放開回慢車；S 收油門 + 減速
            float target = slow > 0.01f ? 0f : thrUp;
            _throttle = Mathf.MoveTowards(_throttle, target, (target > _throttle ? throttleUpRate : throttleDownRate * (1f + 2f * slow)) * dt);
            _airbrake = Grounded ? 0f : slow;
            _pitchIn = Mathf.Clamp(pitch, -1f, 1f);
            _rollIn = Mathf.Clamp(roll, -1f, 1f);
            _yawIn = Mathf.Clamp(yaw, -1f, 1f);
            _brake = Mathf.Max(brake, Grounded ? slow : 0f);    // 地面：S 也是煞車
            if (gearToggle) _gearCmdDown = !_gearCmdDown || Grounded;    // 機輪壓著地不能收
        }

        // ------------------------------------------------------------------ 物理
        void FixedUpdate()
        {
            if (_hidden) return;
            float dt = Time.fixedDeltaTime;

            if (_rb.isKinematic)
            {
                // 別人在飛：位置由網路同步，速度用位移估算（給零件、特效、動物警戒用）
                float kmh = Vector3.Dot(transform.position - _lastPosition, transform.forward) / dt * 3.6f;
                _syncedSpeedKmh = Mathf.Lerp(_syncedSpeedKmh, kmh, 0.2f);
                _lastPosition = transform.position;
                DecodeControls(_netControls.Value);
                UpdateAtmosphere(AirSpeed);
                Grounded = RayGround(transform.position + Vector3.up * 0.5f, Vector3.down, 1.2f, out _);
                return;
            }
            _lastPosition = transform.position;

            _rb.useGravity = true;
            _rb.linearDamping = 0f;
            _rb.angularDamping = 0.5f;

            // 起落架
            if (Grounded) _gearCmdDown = true;
            _gear01 = Mathf.MoveTowards(_gear01, _gearCmdDown ? 1f : 0f, dt / gearTime);

            Vector3 v = _rb.linearVelocity;
            float speed = v.magnitude;
            Vector3 fwd = transform.forward, up = transform.up, right = transform.right;
            float m = _rb.mass, g = Physics.gravity.magnitude;

            UpdateAtmosphere(speed);

            // 推力
            float thrust;
            if (!EngineOn) thrust = 0f;
            else if (_throttle < afterburnerFrom) thrust = Mathf.Lerp(idleThrust, dryThrust, _throttle / afterburnerFrom);
            else thrust = Mathf.Lerp(dryThrust, afterburnerThrust, Mathf.InverseLerp(afterburnerFrom, 1f, _throttle));
            float rho = AirDensity, mach = Mach;
            thrust *= Mathf.Pow(rho, thrustLapse) * (1f + ramFactor * mach) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(machLimit, machLimit + 0.15f, mach)));
            _rb.AddForce(fwd * thrust * m * g);

            // 空氣動力
            Vector3 vl = transform.InverseTransformDirection(v);
            float alpha = speed > 1f ? Mathf.Atan2(-vl.y, vl.z) : 0f;
            float beta = speed > 1f ? Mathf.Atan2(vl.x, vl.z) : 0f;
            AngleOfAttackDeg = alpha * Mathf.Rad2Deg;
            float stall = stallDeg * Mathf.Deg2Rad;
            float cl = cl0 + clPerRad * alpha;
            float clMax = cl0 + clPerRad * stall;
            Stalling = !Grounded && speed > 10f && Mathf.Abs(alpha) > stall;
            if (Mathf.Abs(alpha) > stall)
                cl = Mathf.Sign(alpha) * clMax * Mathf.Lerp(0.55f, 0.2f, Mathf.InverseLerp(stall, stall * 2.5f, Mathf.Abs(alpha)));
            cl += _flap01 * 0.25f;                               // 襟翼
            float q = rho * speed * speed;   // 動壓（÷½ 已併進係數）
            if (speed > 1f)
            {
                Vector3 vdir = v / speed;
                Vector3 liftDir = Vector3.Cross(vdir, right).normalized;
                float drag = dragK * (DragCoefficient(mach) + _gear01 * 0.35f + _flap01 * 0.25f + _airbrake * airbrakeDrag) + inducedK * liftK * cl * cl;
                if (mach > machLimit + 0.3f) drag += dragK * 6f * (mach - machLimit - 0.3f);   // 結構極限
                _rb.AddForce(liftDir * liftK * q * cl - vdir * drag * q - right * sideK * q * beta);
            }

            // GTA 式「指哪飛哪」：速度方向每一步往機頭方向拉（等效空速夠、沒失速才有；低速 / 失速時慢慢失效，飛機會往下掉）。
            // 只轉方向、不改大小 → 轉彎幾乎不掉速。地面不做（交給機輪）
            if (!Grounded && speed > 1f)
            {
                float easNow = speed * Mathf.Sqrt(rho);
                float grip = arcadeGrip * Mathf.Clamp01((easNow - 40f) / 25f) * (Stalling ? 0.2f : 1f);
                if (grip > 0f)
                {
                    var vCur = _rb.linearVelocity;
                    _rb.linearVelocity = Vector3.Slerp(vCur, fwd * vCur.magnitude, 1f - Mathf.Exp(-grip * dt));
                }
            }

            // 操縱：角速度指令（本地座標：x 正 = 低頭、y 正 = 右轉、z 正 = 左滾）
            float authority = Mathf.SmoothStep(0f, 1f, Mathf.Sqrt(rho) * speed / controlFullSpeed);   // 舵面效果看動壓，高空要飛更快才夠力
            float gLimitRate = speed > 5f ? maxG * g / speed * Mathf.Rad2Deg : maxPitchRate;
            // 攻角限制器：接近限制時把那個方向的桿量淡掉（玩家拉滿也不會失速翻掉）
            float pitchIn = _pitchIn;
            if (!Grounded)
            {
                float aDeg = alpha * Mathf.Rad2Deg;
                if (pitchIn > 0f) pitchIn *= Mathf.Clamp01((alphaLimitUp - aDeg) / 4f);
                if (pitchIn < 0f) pitchIn *= Mathf.Clamp01((aDeg - alphaLimitDown) / 4f);
            }
            float pitchRate = -pitchIn * Mathf.Min(maxPitchRate, gLimitRate) * authority;   // 度/秒，抬頭 = 負 x
            float rollRate = -_rollIn * maxRollRate * authority;
            float yawRate = _yawIn * maxYawRate * authority;
            Vector3 wl = transform.InverseTransformDirection(_rb.angularVelocity) * Mathf.Rad2Deg;
            Vector3 want;
            if (Grounded)
            {
                // 地面：滾轉 / 偏航交給機輪；只有速度夠了才能拉起機頭
                want = new Vector3(_pitchIn != 0f && speed > 35f ? pitchRate : wl.x, wl.y, wl.z);
            }
            else
            {
                // 空中：放開桿子就保持姿態；機頭順著氣流（側滑會被風向舵效應拉正）；失速 / 太慢時機頭往下掉
                pitchRate += Stalling ? (Mathf.Abs(alpha) - stall) * Mathf.Rad2Deg * Mathf.Sign(alpha) * 1.5f : 0f;
                float eas = speed * Mathf.Sqrt(AirDensity);   // 等效空速：高空同樣的真空速，舵面和升力都比較弱
                if (eas < 45f) pitchRate += (1f - eas / 45f) * 8f;
                // 風向舵效應：氣流從右邊來（β > 0）→ 機頭往右轉對正（號誌寫反會發散成平螺旋，實測）
                yawRate += beta * Mathf.Rad2Deg * 1.2f * Mathf.Clamp01(speed / 30f);
                want = new Vector3(pitchRate, yawRate, rollRate);
            }
            Vector3 dw = (want - wl) * Mathf.Deg2Rad * rateGain;
            _rb.AddRelativeTorque(dw, ForceMode.Acceleration);

            // 機輪
            bool wasGrounded = Grounded;
            float vyBefore = v.y;
            Grounded = WheelForces(dt);
            if (Grounded && !wasGrounded && vyBefore < -1f) TouchDown?.Invoke(-vyBefore);

            // 減速傘：主輪壓地、油門收著、速度還快 → 自動開傘；慢下來或推油門重飛 → 拋掉
            float vNow = _rb.linearVelocity.magnitude;
            if (!_chute && !_chuteUsed && Grounded && vNow > chuteDeploySpeed && _throttle < 0.3f && EngineOn) { _chute = true; _chuteUsed = true; }
            // 慢下來、推油門重飛、或彈跳飛離地面（GTA 手感下接地後還拉著桿會再飛起來）→ 拋傘
            if (_chute && (vNow < chuteJettisonSpeed || _throttle > 0.6f || (!Grounded && AltitudeAGL > 3f))) _chute = false;
            if (!Grounded && AltitudeAGL > 20f) _chuteUsed = false;
            if (_chute && vNow > 1f) _rb.AddForce(-_rb.linearVelocity / vNow * chuteDecel * m * (vNow * vNow / 4900f));

            // 對地高度 / G 值（HUD、特效）
            // 不算自己的碰撞體：倒飛時機身在原點下面，射線會打到自己（2 km 高空讀成 1 m，實測）
            AltitudeAGL = RayGround(transform.position + Vector3.up * 0.5f, Vector3.down, 2000f, out var hit)
                ? hit.distance - 0.5f
                : DSTWorld.Instance && DSTWorld.Instance.IsRandom ? transform.position.y - DSTWorld.Instance.GroundHeight(transform.position.x, transform.position.z) : 2000f;
            Vector3 accel = (_rb.linearVelocity - _prevVel) / dt;
            _prevVel = _rb.linearVelocity;
            GForce = Mathf.Lerp(GForce, Vector3.Dot(accel + Vector3.up * g, up) / g, 0.2f);

            // 網路：每 0.1 秒把油門 / 舵面送出去
            if (IsSpawned && IsOwner && (_netTimer -= dt) <= 0f)
            {
                _netTimer = 0.1f;
                _netThrottle.Value = (byte)(EngineOn ? Mathf.Max(1, Mathf.RoundToInt(_throttle * 255f)) : 0);
                _netControls.Value = EncodeControls();
            }
        }

        // 阻力係數：次音速 1 → 穿音速（M 0.85–1.05）暴增到 2.4 → 超音速慢慢降到 1.4
        static float DragCoefficient(float m)
        {
            if (m < 0.85f) return 1f;
            if (m < 1.05f) return 1f + 1.4f * Mathf.SmoothStep(0f, 1f, (m - 0.85f) / 0.2f);
            if (m < 2f) return 2.4f - (m - 1.05f) / 0.95f;
            return 1.4f;
        }

        void UpdateAtmosphere(float speed)
        {
            var w = DSTWorld.Instance;
            float baseY = w && w.IsRandom && w.Gen != null ? w.Gen.SpawnHeight : 0f;
            Altitude = transform.position.y - baseY;
            AirDensity = Mathf.Exp(-Mathf.Max(0f, Altitude) / densityScaleHeight);
            float a = Mathf.Lerp(soundSpeedSea, soundSpeedHigh, Mathf.Clamp01(Altitude / 2700f));
            float newMach = speed / a;
            if (Mach < 1f && newMach >= 1f) SoundBarrier?.Invoke(true);
            else if (Mach >= 1f && newMach < 0.98f) SoundBarrier?.Invoke(false);
            Mach = newMach;
        }

        bool WheelForces(float dt)
        {
            if (_gear01 < 0.95f) { for (int i = 0; i < _wheels.Length; i++) _wheels[i].contact = false; return false; }
            bool any = false;
            Vector3 up = transform.up;
            float steer = _yawIn * noseSteerDeg * Mathf.Lerp(1f, 0.1f, Mathf.InverseLerp(5f, 50f, AirSpeed));
            for (int i = 0; i < _wheels.Length; i++)
            {
                ref var w = ref _wheels[i];
                Vector3 c = transform.TransformPoint(w.local);
                float reach = 0.4f + w.restLength;
                w.contact = false;
                if (!RayGround(c + up * 0.4f, -up, reach, out var hit)) continue;
                float compression = reach - hit.distance;
                if (compression <= 0f) continue;
                w.contact = true;
                w.compression = compression;
                any = true;
                Vector3 pv = _rb.GetPointVelocity(hit.point);
                float springV = Vector3.Dot(pv, up);
                // springV < 0 = 正在壓縮（吸收落地衝擊）、> 0 = 正在回彈
                float damp = w.c * (springV < 0f ? compressionDamping : reboundDamping);
                float n = Mathf.Max(0f, w.k * compression - damp * springV);
                n = Mathf.Min(n, w.load * (springV > 0f ? reboundForceLimit : 8f));   // 壓縮可以很硬，回彈不准把飛機推起來
                _rb.AddForceAtPosition(up * n, hit.point);

                // 側向摩擦：前輪方向跟著轉向角
                Vector3 wheelFwd = Vector3.ProjectOnPlane(transform.forward, hit.normal).normalized;
                if (w.nose) wheelFwd = Quaternion.AngleAxis(steer, hit.normal) * wheelFwd;
                Vector3 wheelRight = Vector3.Cross(hit.normal, wheelFwd);
                float vLat = Vector3.Dot(pv, wheelRight), vLon = Vector3.Dot(pv, wheelFwd);
                float mShare = w.load / Physics.gravity.magnitude;
                float lat = Mathf.Clamp(-vLat * mShare / dt * 0.5f, -wheelFriction * n, wheelFriction * n);
                float brake = w.nose ? 0f : _brake;
                // 防鎖死：煞車力最多照 1.5 倍靜載重算，不跟著落地衝擊 / 彈跳時的瞬間正向力（可到 8 倍）放大
                //（彈跳後重落地又踩滿煞車，曾經 1 秒內從 42 m/s 停住，約 4 G，實測）
                float lonMax = rollingResistance * n + brake * brakeFriction * Mathf.Min(n, w.load * 1.5f);
                float lon = Mathf.Clamp(-vLon * mShare / dt, -lonMax, lonMax);
                _rb.AddForceAtPosition(wheelRight * lat + wheelFwd * lon, hit.point);
            }
            return any;
        }

        readonly RaycastHit[] _hits = new RaycastHit[8];

        bool RayGround(Vector3 from, Vector3 dir, float len, out RaycastHit best)
        {
            best = default;
            int n = Physics.RaycastNonAlloc(from, dir, _hits, len, ~0, QueryTriggerInteraction.Ignore);
            float d = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (_hits[i].transform.IsChildOf(transform)) continue;
                if (_hits[i].distance < d) { d = _hits[i].distance; best = _hits[i]; }
            }
            if (d < float.MaxValue) return true;
            // 隨機世界：碰撞還沒烘好時用地形公式當地面（地形公式和碰撞網格是同一套，高度一致）
            var world = DSTWorld.Instance;
            if (world && world.IsRandom && world.Gen != null && dir.y < -0.5f)
            {
                float gy = world.GroundHeight(from.x, from.z);
                float dist = (from.y - gy) / -dir.y;
                if (dist >= 0f && dist <= len)
                {
                    best.point = from + dir * dist;
                    best.normal = world.Gen.Normal(from.x, from.z);
                    best.distance = dist;
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ 零件動畫
        void Animate(float dt)
        {
            float speed = AirSpeed;
            float pitch = _pitchIn, roll = _rollIn, yaw = _yawIn;

            // 全動式水平尾翼：抬頭 = 後緣往上（Unity 裡 + = 後緣往下）；也幫忙滾轉（差動）
            Turn(_stabL, -pitch * 18f + roll * 6f);
            Turn(_stabR, -pitch * 18f - roll * 6f);
            // 副翼：往右滾 = 左邊後緣往下、右邊往上
            Turn(_ailInL, roll * 18f); Turn(_ailOutL, roll * 18f);
            Turn(_ailInR, -roll * 18f); Turn(_ailOutR, -roll * 18f);
            // 方向舵：Unity 裡 + = 後緣往左 → 機頭往右要後緣往右
            Turn(_rudL, -yaw * 22f); Turn(_rudR, -yaw * 22f);
            // 襟翼：起落架放下又慢的時候放 25°
            _flap01 = Mathf.MoveTowards(_flap01, _gear01 > 0.5f && speed < 95f ? 1f : 0f, dt * 0.5f);
            Turn(_flapL, _flap01 * 25f); Turn(_flapR, _flap01 * 25f);
            // 座艙罩：沒人坐、停在地上的時候打開
            bool open = !_controlled && !(_rb && _rb.isKinematic && _netThrottle.Value > 0) && Grounded && speed < 1f;
            _canopy01 = Mathf.MoveTowards(_canopy01, open ? 1f : 0f, dt * 0.4f);
            Turn(_canopy, _canopy01 * 55f);
            // 進氣楔形板：越快壓越低（超音速才明顯）
            float ramp = Mathf.InverseLerp(90f, 200f, speed) * 12f;
            Turn(_rampL, ramp); Turn(_rampR, ramp);

            // 起落架：往前收（+）；艙門在收放途中打開、收好後關上（左 -、右 + 往內關）
            float up01 = 1f - _gear01;
            float legs = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.1f, 0.9f, up01));
            Turn(_noseGear, legs * 105f);
            Turn(_mainGearL, legs * 95f); Turn(_mainGearR, legs * 95f);
            // 模型沒有輪艙：轉完再往上收進機身（主輪進進氣道、前輪進機鼻下方），艙門最後關上（實測：只轉的話主輪中心 0.59 m，半顆露在機腹 0.73 m 下面）
            Lift(_noseGear, legs * 0.28f);
            Lift(_mainGearL, legs * 0.65f); Lift(_mainGearR, legs * 0.65f);
            float doorsClosed = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.9f, 1f, up01));
            Turn(_noseDoorL, -doorsClosed * 88f); Turn(_noseDoorR, doorsClosed * 88f);
            Turn(_outDoorL, -doorsClosed * 88f); Turn(_outDoorR, doorsClosed * 88f);
            Turn(_inDoorL, -doorsClosed * 88f); Turn(_inDoorR, doorsClosed * 88f);

            // 噴口：慢車開、軍用推力收、後燃器全開
            float thr = Throttle;
            float nozzle = !EngineOn ? 1f : thr < afterburnerFrom ? Mathf.Lerp(0.95f, 0.82f, thr / afterburnerFrom) : Mathf.Lerp(0.82f, 1.02f, Afterburner01);
            if (_nozzleL) _nozzleL.localScale = new Vector3(1f, nozzle, nozzle);
            if (_nozzleR) _nozzleR.localScale = new Vector3(1f, nozzle, nozzle);

            // 機輪：在地上就跟著滾
            if (Grounded && _wheelTs != null)
            {
                float vf = SpeedKmh / 3.6f;
                for (int i = 0; i < _wheelTs.Length; i++)
                    if (_wheelTs[i] && _wheelRadius[i] > 0.01f)
                        _wheelTs[i].localRotation *= Quaternion.AngleAxis(vf / _wheelRadius[i] * Mathf.Rad2Deg * dt, Vector3.right);
            }
        }

        void Lift(Part p, float meters)
        {
            if (p.t) p.t.localPosition = p.restPos + p.t.parent.InverseTransformVector(transform.up * meters);
        }

        static void Turn(Part p, float deg)
        {
            if (p.t) p.t.localRotation = p.rest * Quaternion.AngleAxis(deg, Vector3.right);
        }

        int EncodeControls()
        {
            int b(float x) => Mathf.Clamp(Mathf.RoundToInt((x + 1f) * 63f), 0, 126);
            return b(_pitchIn) | b(_rollIn) << 7 | b(_yawIn) << 14 | (_gearCmdDown ? 1 : 0) << 21 | (_brake > 0.5f ? 1 : 0) << 22 | (_chute ? 1 : 0) << 23;
        }

        void DecodeControls(int c)
        {
            float f(int x) => (x & 127) / 63f - 1f;
            _pitchIn = f(c); _rollIn = f(c >> 7); _yawIn = f(c >> 14);
            _gearCmdDown = ((c >> 21) & 1) != 0;
            _chute = ((c >> 23) & 1) != 0;
            _gear01 = Mathf.MoveTowards(_gear01, _gearCmdDown ? 1f : 0f, Time.fixedDeltaTime / gearTime);
        }

        // ------------------------------------------------------------------ 遺跡二：藏起來
        void UpdateHidden()
        {
            bool hide = DSTWorld.Instance && !DSTWorld.Instance.IsRandom;
            if (hide == _hidden) return;
            _hidden = hide;
            foreach (var r in _renderers) if (r) r.enabled = !hide;
            foreach (var c in _colliders) if (c) c.enabled = !hide;
            if (_rb && !_rb.isKinematic)
            {
                if (hide) { _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; }
                _rb.useGravity = !hide;
            }
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!_controlled || _hidden) return;
            float kmh = AirSpeed * 3.6f;
            string thr = Mathf.RoundToInt(_throttle * 100f) + "%" + (Afterburner01 > 0f ? " 後燃器" : "");
            string gear = _gear01 >= 0.99f ? "放下" : _gear01 <= 0.01f ? "收起" : "收放中";
            string warn = Stalling ? "　⚠ 失速" : AltitudeAGL < 60f && _gear01 < 0.5f && !Grounded && _rb.linearVelocity.y < -3f ? "　⚠ 太低" : "";
            string alt = Altitude > 1000f ? $"{Altitude / 1000f:0.0} km" : $"{AltitudeAGL:0} m";
            string mach = Mach >= 0.5f ? $"　M {Mach:0.00}" : "";
            DSTHud.TopPanel($"速度 {kmh:0} km/h{mach}　高度 {alt}　油門 {thr}　{GForce:0.0} G　起落架 {gear}{warn}");
        }
    }
}
