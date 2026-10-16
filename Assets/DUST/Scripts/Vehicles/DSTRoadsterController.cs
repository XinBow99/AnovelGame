using DUST.Player;
using DUST.World;
using Unity.Netcode;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Vehicles
{
    // Roadster（issue #8）第二階段：可以開的電動超跑 —— 真的剛體，四個輪子各自做射線懸吊（彈簧 + 壓縮 / 回彈阻尼 + 防傾桿），
    // 輪胎用摩擦圓（縱向驅動 / 煞車力 + 側滑角產生的側向力，合力不超過 μN），全部用 AddForceAtPosition 推車身，
    // 車身的俯仰、側傾、落地、翻車都是物理自己算出來的。
    // 動力：四輪驅動電動馬達（前後分配 frontShare），馬達扭力 × 齒比 ÷ 輪半徑 = 輪上推力；扭力受功率上限限制
    //（低速定扭力、高速定功率），再乘可調的扭力曲線。放開油門 = 動能回收減速（regenDecel）。
    // 半尺寸世界（長度 ×0.5 → 速度 ×√0.5、質量 ×1/8、功率 ×1/8·√0.5、加速度不變）：
    //   實車資料只當設計參考（Tesla 沒公布規格；MotorTrend 2026-10 估：後雙馬達約 1 MW、車重約 1,700 kg、極速 230+ mph）
    //   → 212.5 kg、88 kW；風阻用極速反推（P = c·v³ → 半尺寸 73 m/s ≈ 實車 103 m/s）。
    // 操作：W/↑ 油門、S/↓ 煞車（停住後按住 = 倒車）、A/D 轉向、Space 煞車、R 翻正。手把：RT/LT、左搖桿、A。
    // 推進器（後噴 / 下噴 / 側噴）、吸地風扇、主動尾翼在後面的階段加在 DSTThrusterSystem / DSTRoadsterAero。
    [RequireComponent(typeof(Rigidbody))]
    public class DSTRoadsterController : NetworkBehaviour, IDSTDrivable
    {
        [System.Serializable]
        public class WheelSpec
        {
            public string name;
            public Vector3 local;       // 靜載重下的輪心（車身座標，半尺寸）
            public float radius = 0.165f;
            public bool front, left;
        }

        public Transform centerOfMass;
        public WheelSpec[] wheels = new WheelSpec[0];
        public Transform yoke;                       // 方向盤（繞本地 Y = 轉向柱轉）
        public float yokeRatio = 5.5f;               // 前輪轉 1° → 方向盤轉 5.5°（線控轉向，鎖到鎖不到 ±170°）

        [Header("Powertrain (half-scale)")]
        public float maxPowerW = 88000f;             // 實車約 1 MW ×1/8×√0.5
        public float motorPeakTorque = 88f;          // N·m（半尺寸），× 齒比 ÷ 輪半徑 = 輪上推力 ~3.2 kN（1.5 g，起步受抓地力限制）
        public float gearRatio = 6f;
        public float motorMaxRpm = 30000f;
        public AnimationCurve torqueCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.85f, 1f), new Keyframe(1f, 0f));   // x = 馬達轉速 / 最高轉速
        [Range(0f, 1f)] public float frontShare = 0.4f;
        public float reverseMaxSpeed = 8f;           // m/s
        public float regenDecel = 2.5f;              // 放開油門的回收減速度（m/s²）
        public float brakeDecel = 14f;               // 踩滿煞車想要的減速度（實際受 μN 限制）
        [Range(0f, 1f)] public float brakeFront = 0.6f;

        [Header("Tyres / suspension")]
        public float tyreGrip = 1.3f;                // μ（高性能半熱熔胎）
        public float peakSlipDeg = 7f;               // 側向力在這個側滑角達到最大
        public float rollingResistance = 0.012f;
        public float sag = 0.05f;                    // 靜載重壓縮量（半尺寸；實車約 10 cm）→ 彈簧剛性
        public float compressionDamping = 0.35f;     // 阻尼比
        public float reboundDamping = 0.6f;
        public float antiRoll = 0.6f;                // × 彈簧剛性
        public float bumpStopFactor = 6f;            // 壓到底（2.5 × sag）之後的硬度倍數

        [Header("Steering")]
        public float maxSteerDeg = 30f;
        public float steerAtSpeedDeg = 5f;
        public float steerFalloffSpeed = 55f;        // m/s
        public float steerRate = 160f;               // 度/秒

        [Header("Aero")]
        public float dragCoef = 0.229f;              // N/(m/s)²：88 kW 時極速 ~73 m/s
        public float liftCoef = -0.03f;              // 負 = 車身下壓力（不含主動尾翼、吸地風扇）

        // ------------------------------------------------------------------ IDSTDrivable
        public bool PlayerControlled
        {
            get => _controlled;
            set
            {
                _controlled = value;
                if (!value) { _throttle = 0f; _brake = 0f; _steerIn = 0f; _hold = true; }
                else _hold = false;
            }
        }

        public float SpeedKmh => !_rb ? 0f : _rb.isKinematic ? _syncedSpeedKmh : Vector3.Dot(_rb.linearVelocity, transform.forward) * 3.6f;
        public string ControlsHint => "[F] 下車  [W] 油門  [S] 煞車 / 倒車  [A/D] 轉向  [Space] 煞車  [R] 翻正  [C] 視角";
        public string EnterHint => "[F] 上車";

        public bool CanExit(float maxExitSpeedKmh, out string reason)
        {
            reason = "先減速才能下車";
            return Mathf.Abs(SpeedKmh) <= maxExitSpeedKmh;
        }

        // 給特效 / HUD / 測試
        public float Throttle => _rb && _rb.isKinematic ? (_netState.Value & 255) / 255f : _throttle;
        public float Brake => _rb && _rb.isKinematic ? ((_netState.Value >> 8) & 255) / 255f : _brake;
        public float SteerDeg => _steerDeg;
        public bool Grounded { get; private set; }
        public int WheelsOnGround { get; private set; }
        public float DrivePowerW { get; private set; }
        public float LongAccel { get; private set; }     // m/s²（車身前後方向）
        public float LatAccel { get; private set; }
        public bool Reversing => _reverse;
        public string DebugWheels
        {
            get
            {
                var s = "";
                if (_w != null) foreach (var w in _w) s += $"{w.spec.name.Replace("RD_Wheel_", "")} c{w.comp:F3} N{w.load:F0}  ";
                return s;
            }
        }

        // 外部（測試 / AI）直接給輸入；打開 externalInput 時不讀鍵盤 / 手把
        [HideInInspector] public bool externalInput;
        /// <summary>brake = S / LT（停住後按住 0.4 秒 = 倒車）；hardBrake = Space / A（只煞車，不會倒車）</summary>
        public void SetDriveInput(float throttle, float brake, float steer, float hardBrake = 0f)
        {
            _throttle = Mathf.Clamp01(throttle);
            _brake = Mathf.Clamp01(brake);
            _hardBrake = Mathf.Clamp01(hardBrake);
            _steerIn = Mathf.Clamp(steer, -1f, 1f);
            _hold = false;
        }

        // 網路：駕駛那台寫（油門 / 煞車 / 轉向 / 倒車），其他人用來轉輪子、亮煞車燈
        readonly NetworkVariable<int> _netState = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        class W
        {
            public WheelSpec spec;
            public Transform t;
            public Quaternion offset;      // 車身座標下的靜止旋轉
            public float k, c, load;       // 彈簧、臨界阻尼、靜載重
            public float comp, prevComp;
            public bool contact;
            public float spin;             // 度
            public Vector3 normal;
        }
        W[] _w;
        Rigidbody _rb;
        bool _controlled, _hold = true, _reverse;
        float _throttle, _brake, _hardBrake, _steerIn, _steerDeg, _reverseHold;
        Vector3 _lastPosition, _prevVel;
        float _syncedSpeedKmh, _netTimer;
        Quaternion _yokeRest;
        Renderer _tailLamp;
        MaterialPropertyBlock _mpb;

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
            if (yoke) _yokeRest = yoke.localRotation;
            var tl = Find(transform, "RD_TailLamp");
            _tailLamp = tl ? tl.GetComponent<Renderer>() : null;
            BuildWheels();
        }

        // 靜載重依重心前後分配（重心標記，不用 Awake 時可能還是自動算的 Rigidbody.centerOfMass）
        void BuildWheels()
        {
            _w = new W[wheels.Length];
            Vector3 com = centerOfMass ? transform.InverseTransformPoint(centerOfMass.position) : _rb.centerOfMass;
            float zf = 0f, zr = 0f;
            foreach (var s in wheels) { if (s.front) zf = s.local.z; else zr = s.local.z; }
            float frontLoad = Mathf.Clamp01(Mathf.Abs(zf - zr) > 0.01f ? (com.z - zr) / (zf - zr) : 0.5f);
            float weight = _rb.mass * Physics.gravity.magnitude;
            for (int i = 0; i < wheels.Length; i++)
            {
                var s = wheels[i];
                var w = new W { spec = s, t = Find(transform, s.name) };
                if (w.t) w.offset = Quaternion.Inverse(transform.rotation) * w.t.rotation;
                w.load = weight * (s.front ? frontLoad : 1f - frontLoad) * 0.5f;
                w.k = w.load / sag;
                w.c = 2f * Mathf.Sqrt(w.k * w.load / Physics.gravity.magnitude);
                w.comp = w.prevComp = sag;
                _w[i] = w;
            }
        }

        public override void OnNetworkSpawn() => _lastPosition = transform.position;

        // ------------------------------------------------------------------ 輸入
        void Update()
        {
            if (_controlled && !externalInput) ReadPlayerInput();
            UpdateVisuals(Time.deltaTime);
        }

        void ReadPlayerInput()
        {
            float thr = 0f, brk = 0f, hard = 0f, st = 0f;
            bool reset = false;
#if ENABLE_INPUT_SYSTEM
            var kb = DUST.UI.DSTChat.Typing ? null : Keyboard.current;   // 聊天打字中不吃鍵盤
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) thr = 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) brk = 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) st += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) st -= 1f;
                if (kb.spaceKey.isPressed) hard = 1f;
                reset = kb.rKey.wasPressedThisFrame;
            }
            var gp = Gamepad.current;
            if (gp != null)
            {
                thr = Mathf.Max(thr, gp.rightTrigger.ReadValue());
                brk = Mathf.Max(brk, gp.leftTrigger.ReadValue());
                st += gp.leftStick.x.ReadValue();
                if (gp.buttonSouth.isPressed) hard = 1f;
                reset |= gp.buttonNorth.wasPressedThisFrame;
            }
#else
            thr = Mathf.Max(0f, Input.GetAxis("Vertical"));
            brk = Mathf.Max(0f, -Input.GetAxis("Vertical"));
            st = Input.GetAxis("Horizontal");
            hard = Input.GetKey(KeyCode.Space) ? 1f : 0f;
            reset = Input.GetKeyDown(KeyCode.R);
#endif
            SetDriveInput(thr, brk, st, hard);
            if (reset) ResetUpright();
        }

        // ------------------------------------------------------------------ 物理
        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (_rb.isKinematic)
            {
                // 別人在開：位置由網路同步，速度用位移估算
                float kmh = Vector3.Dot(transform.position - _lastPosition, transform.forward) / dt * 3.6f;
                _syncedSpeedKmh = Mathf.Lerp(_syncedSpeedKmh, kmh, 0.2f);
                _lastPosition = transform.position;
                int st = _netState.Value;
                _reverse = ((st >> 24) & 1) != 0;
                _steerDeg = ((sbyte)((st >> 16) & 255)) * 0.5f;
                return;
            }
            _lastPosition = transform.position;
            _rb.linearDamping = 0f;
            _rb.angularDamping = 0.05f;

            Vector3 v = _rb.linearVelocity;
            float vf = Vector3.Dot(v, transform.forward);
            float m = _rb.mass, g = Physics.gravity.magnitude;

            // 倒車：停住後按住 S 0.4 秒 = 倒車檔（Space 只煞車）；踩油門 = 回到前進
            bool wantReverse = _brake > 0.5f && _throttle < 0.05f && _hardBrake < 0.05f && Mathf.Abs(vf) < 0.6f && !_hold;
            _reverseHold = wantReverse ? _reverseHold + dt : 0f;
            if (_reverseHold > 0.4f) _reverse = true;
            if (_throttle > 0.05f) _reverse = false;
            float drive = _reverse ? -_brake : _throttle;          // 倒車時 S 是油門
            float brake = Mathf.Max(_reverse ? 0f : _brake, _hardBrake);

            // 轉向：越快角度越小
            float limit = Mathf.Lerp(maxSteerDeg, steerAtSpeedDeg, Mathf.Clamp01(Mathf.Abs(vf) / steerFalloffSpeed));
            _steerDeg = Mathf.MoveTowards(_steerDeg, _steerIn * limit, steerRate * dt);

            // 馬達：輪速 → 馬達轉速 → 可用扭力（定扭力 / 定功率 / 扭力曲線）
            float r = wheels.Length > 0 ? wheels[0].radius : 0.165f;
            float wheelOmega = Mathf.Abs(vf) / r;
            float motorOmega = wheelOmega * gearRatio;
            float rpm01 = motorOmega * 60f / (2f * Mathf.PI) / motorMaxRpm;
            float torque = motorPeakTorque * torqueCurve.Evaluate(Mathf.Clamp01(rpm01));
            if (motorOmega > 1f) torque = Mathf.Min(torque, maxPowerW / motorOmega);
            float totalDrive = drive * torque * gearRatio / r;      // N（四輪合計）
            if (_reverse && -vf > reverseMaxSpeed) totalDrive = 0f;
            DrivePowerW = Mathf.Abs(totalDrive * vf);

            // 回收 / 煞車：想要的減速力（四輪合計，之後每輪受摩擦圓限制）
            float regen = (Mathf.Abs(drive) < 0.02f && Mathf.Abs(vf) > 0.3f) ? regenDecel * m : 0f;   // 前進、倒車都回收（倒車放開也會停）
            float totalBrake = brake * brakeDecel * m + regen;
            if (_hold) totalBrake = Mathf.Max(totalBrake, brakeDecel * m);   // 沒人開：停好

            // 四輪
            WheelsOnGround = 0;
            Vector3 up = transform.up;
            for (int i = 0; i < _w.Length; i++)
            {
                var w = _w[i];
                var s = w.spec;
                Vector3 center = transform.TransformPoint(s.local);
                float reach = 0.3f + s.radius + sag;                  // 從輪心上方 0.3 m 往下打
                w.prevComp = w.comp;
                w.contact = RayGround(center + up * 0.3f, -up, reach, out var hit);
                w.comp = w.contact ? reach - hit.distance : 0f;
                if (!w.contact || w.comp <= 0f) { w.contact = false; w.comp = 0f; continue; }
                WheelsOnGround++;
                w.normal = hit.normal;

                // 懸吊：彈簧 + 壓縮 / 回彈不同阻尼 + 壓到底的硬限位
                float compV = (w.comp - w.prevComp) / dt;              // > 0 = 正在壓縮
                float damp = w.c * (compV > 0f ? compressionDamping : reboundDamping);
                float n = w.k * w.comp + damp * compV;
                float bump = w.comp - sag * 2.5f;
                if (bump > 0f) n += w.k * bumpStopFactor * bump;
                n = Mathf.Max(0f, n);

                // 防傾桿：同軸左右壓縮差
                var other = Opposite(i);
                if (other != null) n += (w.comp - other.comp) * w.k * antiRoll;
                n = Mathf.Max(0f, n);
                _rb.AddForceAtPosition(up * n, hit.point);

                // 輪胎：輪子方向（前輪跟著轉向角）
                Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, hit.normal).normalized;
                if (s.front) fwd = Quaternion.AngleAxis(_steerDeg, hit.normal) * fwd;
                Vector3 right = Vector3.Cross(hit.normal, fwd);
                Vector3 pv = _rb.GetPointVelocity(hit.point);
                float vLon = Vector3.Dot(pv, fwd), vLat = Vector3.Dot(pv, right);
                float muN = tyreGrip * n;

                // 縱向：驅動 + 煞車 / 回收 + 滾動阻力
                float share = (s.front ? frontShare : 1f - frontShare) * 0.5f;
                float fx = totalDrive * share;
                float bShare = (s.front ? brakeFront : 1f - brakeFront) * 0.5f;
                float fb = totalBrake * bShare + rollingResistance * n;
                float mShare = w.load / g;
                // 煞車力不能把輪子推過頭（低速時用「把速度歸零所需的力」夾住，停住才不會前後抖）
                fx -= Mathf.Clamp(vLon * mShare / dt, -fb, fb);

                // 側向：側滑角 → 側向力（tanh 飽和）；低速時改成直接抵消側向速度（停著不會滑）
                float slip = Mathf.Atan2(vLat, Mathf.Max(Mathf.Abs(vLon), 1.5f));
                float fy = -muN * Tanh(slip / (peakSlipDeg * Mathf.Deg2Rad));
                if (Mathf.Abs(vLon) < 1.5f)
                    fy = Mathf.Lerp(Mathf.Clamp(-vLat * mShare / dt * 0.5f, -muN, muN), fy, Mathf.Abs(vLon) / 1.5f);

                // 摩擦圓：合力不超過 μN
                float f = Mathf.Sqrt(fx * fx + fy * fy);
                if (f > muN && f > 1e-4f) { fx *= muN / f; fy *= muN / f; }
                _rb.AddForceAtPosition(fwd * fx + right * fy, hit.point);

                w.spin += vLon / s.radius * Mathf.Rad2Deg * dt;
            }
            Grounded = WheelsOnGround > 0;
            // 離地的輪子：照最後的轉速慢慢停（落地時不會一下子停住）
            for (int i = 0; i < _w.Length; i++)
                if (!_w[i].contact) _w[i].spin += vf / _w[i].spec.radius * Mathf.Rad2Deg * dt * 0.98f;

            // 空氣：阻力 + 車身下壓力（v² ）
            float speed = v.magnitude;
            if (speed > 0.1f) _rb.AddForce(-v * speed * dragCoef);
            _rb.AddForce(up * liftCoef * vf * vf);

            Vector3 accel = (_rb.linearVelocity - _prevVel) / dt;
            _prevVel = _rb.linearVelocity;
            LongAccel = Mathf.Lerp(LongAccel, Vector3.Dot(accel, transform.forward), 0.2f);
            LatAccel = Mathf.Lerp(LatAccel, Vector3.Dot(accel, transform.right), 0.2f);

            // 網路：每 0.1 秒把油門 / 煞車 / 轉向 / 倒車送出去
            if (IsSpawned && IsOwner && (_netTimer -= dt) <= 0f)
            {
                _netTimer = 0.1f;
                int thr8 = Mathf.RoundToInt(Mathf.Abs(drive) * 255f), brk8 = Mathf.RoundToInt(brake * 255f);
                int st8 = (sbyte)Mathf.Clamp(Mathf.RoundToInt(_steerDeg * 2f), -127, 127) & 255;
                _netState.Value = thr8 | brk8 << 8 | st8 << 16 | (_reverse ? 1 : 0) << 24;
            }
        }

        static float Tanh(float x)
        {
            float e = Mathf.Exp(2f * Mathf.Clamp(x, -10f, 10f));
            return (e - 1f) / (e + 1f);
        }

        W Opposite(int i)
        {
            var s = _w[i].spec;
            foreach (var o in _w) if (o != _w[i] && o.spec.front == s.front && o.spec.left != s.left) return o;
            return null;
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
            // 隨機世界：碰撞還沒烘好時用地形公式當地面
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

        // ------------------------------------------------------------------ 外觀
        void UpdateVisuals(float dt)
        {
            if (_w == null) return;
            bool remote = _rb && _rb.isKinematic;
            float vf = SpeedKmh / 3.6f;
            foreach (var w in _w)
            {
                if (!w.t) continue;
                // 輪心跟著懸吊上下（遠端沒有懸吊資料：放在靜止位置）
                float lift = remote ? 0f : (w.contact ? w.comp - sag : -sag);
                if (remote) w.spin += vf / w.spec.radius * Mathf.Rad2Deg * dt;
                var pos = transform.TransformPoint(w.spec.local + Vector3.up * lift);
                var rot = transform.rotation * (w.spec.front ? Quaternion.AngleAxis(_steerDeg, Vector3.up) : Quaternion.identity)
                          * Quaternion.AngleAxis(w.spin, Vector3.right) * w.offset;
                w.t.SetPositionAndRotation(pos, rot);
            }
            if (yoke) yoke.localRotation = _yokeRest * Quaternion.AngleAxis(-_steerDeg * yokeRatio, Vector3.up);
            // 煞車燈
            if (_tailLamp)
            {
                _mpb ??= new MaterialPropertyBlock();
                _tailLamp.GetPropertyBlock(_mpb);
                float k = Brake > 0.1f || (_reverse && Throttle > 0.05f) ? 1f : 0.3f;
                _mpb.SetColor("_EmissiveColor", tailEmission * k);
                _tailLamp.SetPropertyBlock(_mpb);
            }
        }

        [ColorUsage(false, true)] public Color tailEmission = new Color(1f, 0.03f, 0.02f) * 40f;

        public void ResetUpright()
        {
            var fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            transform.SetPositionAndRotation(transform.position + Vector3.up * 0.6f, Quaternion.LookRotation(fwd, Vector3.up));
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!_controlled) return;
            float kmh = Mathf.Abs(SpeedKmh);
            string gear = _reverse ? "R" : kmh < 0.5f && _throttle < 0.05f ? "P" : "D";
            DSTHud.TopPanel($"速度 {kmh:0} km/h　檔位 {gear}　輸出 {DrivePowerW / 1000f:0} kW　{LongAccel / 9.81f:+0.00;-0.00} G");
        }
    }
}
