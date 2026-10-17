using Unity.Netcode;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Vehicles
{
    // Roadster（issue #8）第三階段：冷氣推進器（cold-gas thrusters）+ 高壓氣瓶 + 飛行輔助。
    //
    // 每個噴嘴 = 一個安裝點（車身座標）+ 噴氣方向 + 最大推力 + 閥門開度。推力 = −噴氣方向 × 推力，
    // 用 AddForceAtPosition 施加在噴嘴出口：線加速度和力矩都由物理引擎自己算（不直接改速度 / 轉角）。
    //
    // 配置照 MotorTrend（2026-10）的拆解文章：後方 4 個（往前推）；Rocket 版再加 4 個「就在輪子後面、朝下」的噴嘴
    //（升力 / 俯仰 / 滾轉）+ 車頭 2 個朝左右（偏航）= 10 個，跟 issue 的建議一樣。位置 / 方向從 roadster_layout.json 來。
    //
    // 氣瓶：文章說 Rocket 版 COPV 合計約 600 L、最高 10,000 psi（690 bar）的氮氣混合氣。氮氣在 690 bar、常溫約 0.48 kg/L
    //（壓縮因子 ~1.65，估算）→ 實車約 288 kg → 半尺寸 ×1/8 = 36 kg。消耗 ṁ = 推力 / 有效排氣速度；冷氮氣理論上限
    // ~720 m/s，實際取 600 m/s → 半尺寸 ×√0.5 = 424 m/s。氣剩不到 reserveFraction 時壓力不夠，推力跟著掉。
    // 壓縮機：停止噴氣 compressorDelay 秒後慢慢補氣（用電，HUD 顯示功率）；不會瞬間補滿。
    // 以上都是設計估算，不是 Tesla 公布的規格。
    //
    // 推力（實車 → 半尺寸 ×1/8）：後 4 × 8 kN（合計 ~1.9 g）、下 4 × 10 kN（~2.4 g，跳得過卡車）、側 2 × 5 kN。
    // 全開後噴 ~4 秒、懸停 ~7 秒（遊戲時間）就把氣用完 —— 不是無限飛行。
    //
    // 分配：想要的升力 L、俯仰力矩 τx、滾轉力矩 τz → 4 個下噴嘴（3 條方程 4 個未知數的最小範數解），
    // 再用「姿態優先」把升力縮到每個噴嘴都在 0..最大推力內；偏航力矩 → 車頭左右噴嘴。
    // 飛行輔助（V 切換，預設開）：PD 角速度控制，沒有輸入時把車身轉回水平；只能用噴嘴推得出來的力矩、
    // 受剩餘氣量限制，不會覆寫姿態。氣用完就照慣性和重力掉下去。
    //
    // 操作（避開既有按鍵：F 是上下車、C 是視角）：Shift 後噴加速、E 升力、Q / R 空中偏航、空中 W/S 俯仰（W = 低頭）、
    // A/D 滾轉、V 飛行輔助開關。手把：RB 加速、X 升力、空中左搖桿 俯仰 / 滾轉、十字鍵左右 偏航、十字鍵上 輔助。
    [RequireComponent(typeof(Rigidbody))]
    public class DSTThrusterSystem : NetworkBehaviour
    {
        public enum Group { Rear, Down, Side }

        [System.Serializable]
        public class Thruster
        {
            public string name;
            public Group group;
            public Vector3 localPos;        // 噴嘴出口（車身座標）
            public Vector3 localDir;        // 噴氣方向（推力 = 反方向）
            public float maxThrust = 1000f; // N（半尺寸）
            public float radius = 0.018f;
            [System.NonSerialized] public float command, output;   // 0..1
        }

        public Thruster[] thrusters = new Thruster[0];

        [Header("Gas (half-scale; design estimates)")]
        public float gasCapacityKg = 36f;
        public float exhaustVelocity = 424f;          // m/s
        [Range(0f, 0.5f)] public float reserveFraction = 0.15f;   // 低於這個比例推力線性掉到 0
        public float compressorRateKg = 0.42f;        // kg/s（約 85 秒補滿）
        public float compressorPowerW = 9000f;        // 補氣時用電（顯示用）
        public float compressorDelay = 1f;
        public float valveTime = 0.04f;               // 閥門開關時間常數（s）

        [Header("Flight assist (PD on body rates)")]
        public bool flightAssist = true;
        public Vector3 maxRateDeg = new Vector3(150f, 90f, 270f);   // 俯仰 / 偏航 / 滾轉 想要的最大角速度（滾一圈 ~1.3 s：發表會的 kickflip）
        public float rateGain = 8f;                    // 1/s：角速度跟上指令
        public float levelGain = 3f;                   // 1/s：沒輸入時轉回水平
        public float climbSpeed = 4f;                  // E 按滿時想要的爬升速度（m/s）

        // ------------------------------------------------------------------ 狀態（HUD / 特效 / 測試）
        public float Gas => _gas;
        public float GasFraction => gasCapacityKg > 0f ? _gas / gasCapacityKg : 0f;
        public float TotalThrust { get; private set; }
        public float RearThrust { get; private set; }
        public bool CompressorOn { get; private set; }
        public float Available => Mathf.Clamp01(GasFraction / Mathf.Max(reserveFraction, 1e-3f));
        public bool Assist => flightAssist;
        public event System.Action<int> ValveOpened;   // 噴嘴從關到開（特效：閥門聲）
        public float Output(int i) => i >= 0 && i < thrusters.Length ? thrusters[i].output : 0f;

        // 外部（測試 / AI）直接給輸入：boost / lift 0..1，pitch 正 = 抬頭、roll 正 = 右滾、yaw 正 = 右轉
        public void SetThrustInput(float boost, float lift, float pitch, float roll, float yaw)
        {
            _boost = Mathf.Clamp01(boost); _lift = Mathf.Clamp01(lift);
            _pitch = Mathf.Clamp(pitch, -1f, 1f); _roll = Mathf.Clamp(roll, -1f, 1f); _yaw = Mathf.Clamp(yaw, -1f, 1f);
        }

        public void Refill() { _gas = gasCapacityKg; }

        // 網路：每個噴嘴 3 位元（0..7），氣量 1 byte + 輔助 1 bit
        readonly NetworkVariable<long> _netOut = new NetworkVariable<long>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        // 初始值 = 氣瓶滿 + 輔助開（初始值寫 255 時輔助位元是 0：換駕駛那一下車子短暫是 Kinematic，
        // 會把它讀成「輔助關」→ 懸停翻車，實測）
        readonly NetworkVariable<ushort> _netGas = new NetworkVariable<ushort>(255 | 256, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        Rigidbody _rb;
        DSTRoadsterController _car;
        float _gas, _dryMass, _idle, _netTimer, _airTime;
        float _boost, _lift, _pitch, _roll, _yaw;
        int[] _down = new int[0], _side = new int[0], _rear = new int[0];
        // 下噴嘴分配矩陣：t = Pinv · [L, τx, τz]
        float[,] _pinv;
        Vector3 _comLocal;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _car = GetComponent<DSTRoadsterController>();
            _gas = gasCapacityKg;
            _dryMass = _rb.mass - gasCapacityKg;    // Prefab 的質量 = 氣瓶滿的時候
            var down = new System.Collections.Generic.List<int>();
            var side = new System.Collections.Generic.List<int>();
            var rear = new System.Collections.Generic.List<int>();
            for (int i = 0; i < thrusters.Length; i++)
                (thrusters[i].group == Group.Down ? down : thrusters[i].group == Group.Side ? side : rear).Add(i);
            _down = down.ToArray(); _side = side.ToArray(); _rear = rear.ToArray();
        }

        void Start()
        {
            // 重心在 DSTRoadsterController.Awake 才設好
            _comLocal = _rb.centerOfMass;
            BuildMixer();
        }

        // 下噴嘴（推力沿車身 +Y）：升力 = Σt、τx = Σ(−z·t)、τz = Σ(x·t)（相對重心）
        void BuildMixer()
        {
            int n = _down.Length;
            if (n < 3) { _pinv = null; return; }
            var A = new float[3, n];
            for (int k = 0; k < n; k++)
            {
                var p = thrusters[_down[k]].localPos - _comLocal;
                A[0, k] = 1f; A[1, k] = -p.z; A[2, k] = p.x;
            }
            // Pinv = Aᵀ (A Aᵀ)⁻¹
            var M = new Matrix4x4();
            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
            {
                float s = 0f;
                for (int k = 0; k < n; k++) s += A[r, k] * A[c, k];
                M[r, c] = s;
            }
            M[3, 3] = 1f;
            var Mi = M.inverse;
            _pinv = new float[n, 3];
            for (int k = 0; k < n; k++)
            for (int c = 0; c < 3; c++)
            {
                float s = 0f;
                for (int r = 0; r < 3; r++) s += A[r, k] * Mi[r, c];
                _pinv[k, c] = s;
            }
        }

        // ------------------------------------------------------------------ 輸入
        void Update()
        {
            if (_car && _car.PlayerControlled && !_car.externalInput) ReadPlayerInput();
            else if (_car && !_car.PlayerControlled && !_car.externalInput) SetThrustInput(0f, 0f, 0f, 0f, 0f);
        }

        void ReadPlayerInput()
        {
            float boost = 0f, lift = 0f, pitch = 0f, roll = 0f, yaw = 0f;
            bool toggle = false;
            bool air = _car.WheelsOnGround == 0;
#if ENABLE_INPUT_SYSTEM
            var kb = DUST.UI.DSTChat.Typing ? null : Keyboard.current;
            if (kb != null)
            {
                if (kb.leftShiftKey.isPressed) boost = 1f;
                if (kb.eKey.isPressed) lift = 1f;
                if (kb.rKey.isPressed) yaw += 1f;
                if (kb.qKey.isPressed) yaw -= 1f;
                if (air)
                {
                    if (kb.sKey.isPressed) pitch += 1f;     // 往後 = 抬頭
                    if (kb.wKey.isPressed) pitch -= 1f;
                    if (kb.dKey.isPressed) roll += 1f;
                    if (kb.aKey.isPressed) roll -= 1f;
                }
                toggle |= kb.vKey.wasPressedThisFrame;
            }
            var gp = Gamepad.current;
            if (gp != null)
            {
                boost = Mathf.Max(boost, gp.rightShoulder.isPressed ? 1f : 0f);
                lift = Mathf.Max(lift, gp.buttonWest.isPressed ? 1f : 0f);
                if (gp.dpad.right.isPressed) yaw += 1f;
                if (gp.dpad.left.isPressed) yaw -= 1f;
                if (air)
                {
                    pitch += -gp.leftStick.y.ReadValue();
                    roll += gp.leftStick.x.ReadValue();
                }
                toggle |= gp.dpad.up.wasPressedThisFrame;
            }
#endif
            if (toggle) flightAssist = !flightAssist;
            SetThrustInput(boost, lift, pitch, roll, yaw);
        }

        // ------------------------------------------------------------------ 物理
        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (_rb.isKinematic)
            {
                // 別人在開：照網路同步的開度畫特效
                long o = _netOut.Value;
                for (int i = 0; i < thrusters.Length && i < 21; i++)
                {
                    float v = ((o >> (3 * i)) & 7) / 7f;
                    if (thrusters[i].output < 0.01f && v > 0.01f) ValveOpened?.Invoke(i);
                    thrusters[i].output = v;
                }
                if (IsSpawned && !IsOwner)        // 只有別人開的車才照網路的狀態（自己換駕駛那一下不要被蓋掉）
                {
                    _gas = (_netGas.Value & 255) / 255f * gasCapacityKg;
                    flightAssist = (_netGas.Value & 256) != 0;
                }
                return;
            }
            if (_pinv == null) BuildMixer();

            bool air = _car && _car.WheelsOnGround == 0;
            _airTime = air ? _airTime + dt : 0f;
            float avail = Available;
            foreach (var t in thrusters) t.command = 0f;

            // 後噴：加速
            foreach (int i in _rear) thrusters[i].command = _boost;

            // 姿態：正在用升力、或真的飛起來了（高速過坡輪子離地一下不算，免得一直噴氣）
            bool input = Mathf.Abs(_pitch) + Mathf.Abs(_roll) + Mathf.Abs(_yaw) > 0.05f;
            bool attitude = _lift > 0.01f || (_airTime > 0.25f && (flightAssist || input));
            Vector3 torque = Vector3.zero;     // 想要的力矩（車身座標：x 正 = 低頭、y 正 = 右轉、z 正 = 左滾）
            if (attitude)
            {
                Vector3 w = transform.InverseTransformDirection(_rb.angularVelocity);
                // 想要的角速度（rad/s）：玩家輸入；輔助開而且沒輸入時，轉回水平、偏航停住
                Vector3 want = new Vector3(-_pitch * maxRateDeg.x, _yaw * maxRateDeg.y, -_roll * maxRateDeg.z) * Mathf.Deg2Rad;
                if (flightAssist)
                {
                    Vector3 axis = transform.InverseTransformDirection(Vector3.Cross(transform.up, Vector3.up));   // 轉回水平要繞的軸（長度 = sin 角度）
                    float ang = Mathf.Asin(Mathf.Clamp(axis.magnitude, 0f, 1f));
                    if (Vector3.Dot(transform.up, Vector3.up) < 0f) ang = Mathf.PI - ang;   // 倒過來了
                    Vector3 level = axis.sqrMagnitude > 1e-8f ? axis.normalized * ang * levelGain : Vector3.zero;
                    if (Mathf.Abs(_pitch) < 0.05f) want.x = level.x;
                    if (Mathf.Abs(_roll) < 0.05f) want.z = level.z;
                    if (Mathf.Abs(_yaw) < 0.05f) want.y = 0f;
                }
                else
                {
                    // 輔助關：沒輸入就不出力矩（照慣性轉）
                    if (Mathf.Abs(_pitch) < 0.05f) want.x = w.x;
                    if (Mathf.Abs(_roll) < 0.05f) want.z = w.z;
                    if (Mathf.Abs(_yaw) < 0.05f) want.y = w.y;
                }
                Vector3 alpha = (want - w) * rateGain;
                Vector3 I = _rb.inertiaTensor;
                torque = Vector3.Scale(I, alpha);
            }

            // 升力：輔助開 = 想要的爬升速度（E 按滿 +climbSpeed）；輔助關 = 直接開滿
            float lift = 0f;
            if (_lift > 0.01f)
            {
                if (flightAssist)
                {
                    float vy = _rb.linearVelocity.y;
                    float up = Vector3.Dot(transform.up, Vector3.up);
                    float tilt = Mathf.Max(0.3f, up);
                    float need = _rb.mass * (Physics.gravity.magnitude + (climbSpeed * _lift - vy) * 3f) / tilt;
                    // 車身翻過去時下噴嘴朝上：再噴就是把車往地上壓（翻滾時撞地甩成 680°/s，實測）→ 升力淡掉
                    lift = Mathf.Max(0f, need) * Mathf.Clamp01((up - 0.1f) / 0.3f);
                }
                else lift = float.MaxValue;
            }
            MixDown(lift, torque.x, torque.z);

            // 偏航：車頭左右噴嘴（推力沿 ±X，作用點在車頭 → τy = z·F）
            if (attitude && Mathf.Abs(torque.y) > 1e-3f)
                foreach (int i in _side)
                {
                    var t = thrusters[i];
                    var p = t.localPos - _comLocal;
                    float fx = -t.localDir.x;                 // 這個噴嘴推車身的方向（+1 = 往右）
                    float tauPerN = p.z * fx;                 // 1 N 產生的偏航力矩
                    if (tauPerN * torque.y > 0f) t.command = Mathf.Clamp01(torque.y / tauPerN / Mathf.Max(t.maxThrust * avail, 1f));
                }

            // 閥門 → 實際推力 → 力 + 用氣
            float total = 0f, rear = 0f, used = 0f;
            float k = 1f - Mathf.Exp(-dt / Mathf.Max(valveTime, 1e-3f));
            for (int i = 0; i < thrusters.Length; i++)
            {
                var t = thrusters[i];
                float cmd = _gas > 0f ? t.command : 0f;
                if (t.output < 0.01f && cmd > 0.05f) ValveOpened?.Invoke(i);
                t.output = Mathf.Lerp(t.output, cmd, k);
                if (t.output < 0.002f) { t.output = 0f; continue; }
                float f = t.output * t.maxThrust * avail;
                Vector3 dirW = transform.TransformDirection(t.localDir);
                _rb.AddForceAtPosition(-dirW * f, transform.TransformPoint(t.localPos));
                total += f;
                if (t.group == Group.Rear) rear += f;
                used += f / exhaustVelocity * dt;
            }
            TotalThrust = total;
            RearThrust = rear;
            _gas = Mathf.Max(0f, _gas - used);

            // 壓縮機：停止噴氣一陣子後補氣
            _idle = total > 1f ? 0f : _idle + dt;
            CompressorOn = _idle > compressorDelay && _gas < gasCapacityKg;
            if (CompressorOn) _gas = Mathf.Min(gasCapacityKg, _gas + compressorRateKg * dt);
            _rb.mass = _dryMass + _gas;

            // 網路
            if (IsSpawned && IsOwner && (_netTimer -= dt) <= 0f)
            {
                _netTimer = 0.05f;
                long o = 0;
                for (int i = 0; i < thrusters.Length && i < 21; i++)
                    o |= (long)Mathf.Clamp(Mathf.RoundToInt(thrusters[i].output * avail * 7f), 0, 7) << (3 * i);
                _netOut.Value = o;
                _netGas.Value = (ushort)(Mathf.RoundToInt(GasFraction * 255f) | (flightAssist ? 256 : 0));
            }
        }

        // 下噴嘴：先算姿態力矩那一份（升力 0），再疊上升力那一份，升力縮到每個噴嘴都在 0..最大推力（姿態優先）
        void MixDown(float lift, float tauX, float tauZ)
        {
            int n = _down.Length;
            if (n == 0 || _pinv == null) return;
            float avail = Mathf.Max(Available, 1e-3f);
            var att = new float[n];
            var unit = new float[n];
            for (int k = 0; k < n; k++)
            {
                att[k] = _pinv[k, 1] * tauX + _pinv[k, 2] * tauZ;
                unit[k] = _pinv[k, 0];
            }
            // 姿態那一份本身可能需要「負推力」：加升力把它墊到 0 以上；太大就整份縮小
            float lo = 0f, hi = float.MaxValue;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                lo = 0f; hi = float.MaxValue;
                for (int k = 0; k < n; k++)
                {
                    float max = thrusters[_down[k]].maxThrust * avail;
                    if (unit[k] <= 1e-6f) continue;
                    lo = Mathf.Max(lo, -att[k] / unit[k]);
                    hi = Mathf.Min(hi, (max - att[k]) / unit[k]);
                }
                if (lo <= hi) break;
                for (int k = 0; k < n; k++) att[k] *= 0.5f;      // 推不出那麼大的力矩：減半再試
            }
            if (lo > hi) return;
            float L = Mathf.Clamp(lift, lo, hi);
            // 沒要升力、只要姿態時，不要為了墊高而硬噴：低於 lo 的部分只在空中允許（地上沒按 E 不噴）
            for (int k = 0; k < n; k++)
            {
                var t = thrusters[_down[k]];
                float f = att[k] + unit[k] * L;
                t.command = Mathf.Clamp01(f / (t.maxThrust * avail));
            }
        }
    }
}
