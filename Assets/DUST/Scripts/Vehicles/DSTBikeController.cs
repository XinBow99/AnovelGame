using DUST.Player;
using DUST.World;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Vehicles
{
    // MeBike（公共自行車）：街機式腳踏車。W 踩踏板、S / Space 煞車、A/D 轉向；過彎會往內倒、上下坡會跟著地面傾斜。
    // 剛體只負責撞東西與重力，方向和速度每個物理幀直接給（腳踏車用真的物理很難騎）。
    // 租車：上車＝租借（計時、計費），在站點旁下車會自動停進車柱＝還車。
    [RequireComponent(typeof(Rigidbody))]
    public class DSTBikeController : MonoBehaviour, IDSTDrivable
    {
        [Header("Parts")]
        public Transform visual;          // 整台車的外觀（傾斜用）；剛體本身只轉水平方向
        public Transform steer;           // 龍頭（含前輪）
        public Transform frontWheel, rearWheel, crank;
        public GameObject rider;          // 有人騎才顯示

        [Header("Ride")]
        public float maxSpeed = 4.5f;     // m/s（半尺寸世界，速度乘 √0.5 換算，約等於真實 23 km/h）
        public float pedalAccel = 2.4f;
        public float brakeDecel = 6f;
        public float coastDecel = 0.5f;
        public float maxSteer = 32f;
        public float wheelBase = 0.54f;
        public float wheelRadius = 0.168f;

        public bool PlayerControlled
        {
            get => _controlled;
            set
            {
                bool was = _controlled;
                _controlled = value;
                if (value && !was) { _returnPending = false; OnRent(); }
                // 有鬃狼動作時，下車動作還在播（模型掛在車上）：等 FinishReturn 再停回車柱，
                // 不然車子一開始下車就瞬移進車柱，鬃狼會跳到隔壁停著的車上
                if (!value && was) { if (_hasActions) _returnPending = true; else OnReturn(); }
            }
        }

        public float SpeedKmh => !_rb ? 0f : _rb.isKinematic ? _syncedSpeedKmh : _speed * 3.6f;
        /// <summary>曲柄角度（度），鬃狼踩踏板的動作跟著它同步</summary>
        public float CrankAngle => _crankAngle;
        /// <summary>龍頭輸入 -1..1（正 = 右轉）</summary>
        public float SteerInput => _steer;
        public string ControlsHint => "[F] 還車 / 下車    [W] 踩踏板    [S] 煞車    [A/D] 轉向    [C] 切換視角    [滑鼠] 環顧";
        public string EnterHint => "[F] 租借 MeBike";

        public bool CanExit(float maxExitSpeedKmh, out string reason)
        {
            reason = "先煞車才能下車";
            return Mathf.Abs(SpeedKmh) <= maxExitSpeedKmh;
        }

        Rigidbody _rb;
        DSTVehicleEntry _entry;
        Collider[] _ownColliders;
        Renderer[] _riderRenderers;
        bool _controlled;
        bool _hasActions;
        float _speed, _steer, _throttle, _brake, _yaw;
        float _wheelAngle, _crankAngle, _lean;
        Vector3 _groundNormal = Vector3.up;
        bool _grounded;
        Vector3 _lastPosition;
        float _syncedSpeedKmh;
        readonly RaycastHit[] _hits = new RaycastHit[8];

        // 租借計費（跟台灣公共自行車差不多：前 30 分鐘 5 元，之後每 30 分鐘 10 元）
        float _rentStart = -1f;
        bool _pullOutPending;
        bool _returnPending;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _entry = GetComponent<DSTVehicleEntry>();
            _hasActions = GetComponent<DSTRideActions>() != null;
            _ownColliders = GetComponentsInChildren<Collider>();
            _riderRenderers = rider ? rider.GetComponentsInChildren<Renderer>() : new Renderer[0];
            _yaw = transform.eulerAngles.y;
            _lastPosition = transform.position;
            _rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        }

        void Start() => DSTMeBikeStation.IgnoreDockCollisions(_ownColliders);

        // 外部（測試、AI）直接給輸入；打開 externalInput 時不讀鍵盤 / 手把
        [HideInInspector] public bool externalInput;
        float _extThrottle, _extSteer, _extBrake;
        public void SetInput(float throttle, float steer, float brake) { _extThrottle = throttle; _extSteer = steer; _extBrake = brake; }

        // ------------------------------------------------------------------ 輸入
        void ReadInput()
        {
            if (externalInput)
            {
                _throttle = _extThrottle; _brake = _extBrake;
                _steer = Mathf.MoveTowards(_steer, Mathf.Clamp(_extSteer, -1f, 1f), Time.fixedDeltaTime * 4f);
                return;
            }
            float steer = 0f, throttle = 0f, brake = 0f;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed) throttle = 1f;
                if (kb.sKey.isPressed || kb.spaceKey.isPressed) brake = 1f;
                if (kb.aKey.isPressed) steer -= 1f;
                if (kb.dKey.isPressed) steer += 1f;
            }
            var gp = Gamepad.current;
            if (gp != null)
            {
                throttle = Mathf.Max(throttle, gp.rightTrigger.ReadValue());
                brake = Mathf.Max(brake, gp.leftTrigger.ReadValue());
                steer += gp.leftStick.ReadValue().x;
            }
#endif
            _throttle = throttle;
            _brake = brake;
            _steer = Mathf.MoveTowards(_steer, Mathf.Clamp(steer, -1f, 1f), Time.fixedDeltaTime * 4f);
        }

        // ------------------------------------------------------------------ 物理
        void FixedUpdate()
        {
            if (_rb.isKinematic)
            {
                // 別人在騎：速度用位移估（車輪、踏板動畫用）
                _syncedSpeedKmh = Vector3.Distance(transform.position, _lastPosition) / Time.fixedDeltaTime * 3.6f;
                _lastPosition = transform.position;
                return;
            }
            _lastPosition = transform.position;

            if (_controlled) ReadInput();
            else { _throttle = 0f; _brake = 1f; _steer = Mathf.MoveTowards(_steer, 0f, Time.fixedDeltaTime * 2f); }

            // 從車柱牽出來：租車一開始就做（鬃狼要走到牽出來之後的位置才上車），要等這台電腦拿到物理
            if (_pullOutPending)
            {
                _pullOutPending = false;
                _yaw = _rb.rotation.eulerAngles.y;
                var back = -(Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward);
                _yaw += 180f;
                _speed = 0f;
                Place(_rb.position + back * 1.0f, Quaternion.Euler(0f, _yaw, 0f));   // 先牽出 1 m 再轉頭，才不會擠到隔壁的車
                return;
            }

            // 停著沒人騎：整台交給物理（剛體才睡得著，不會一直被改速度而慢慢飄走；被撞也是自然被推）
            if (!_controlled && Mathf.Abs(_speed) < 0.05f)
            {
                _speed = 0f;
                _yaw = _rb.rotation.eulerAngles.y;
                _lean = Mathf.Lerp(_lean, 0f, Time.fixedDeltaTime * 3f);
                return;
            }

            ProbeGround();
            var fwd = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;

            // 被撞停 / 撞牆：以實際速度為準
            float actual = Vector3.Dot(_rb.linearVelocity, fwd);
            if (actual < _speed - 0.5f) _speed = Mathf.Max(actual, 0f);

            float dt = Time.fixedDeltaTime;
            if (_grounded)
            {
                // 坡度：上坡踩比較吃力、下坡會自己滑
                var slopeFwd = Vector3.ProjectOnPlane(fwd, _groundNormal).normalized;
                float slope = slopeFwd.y;
                float accel = _throttle * pedalAccel * (1f - Mathf.Clamp01(_speed / maxSpeed)) - Physics.gravity.magnitude * slope * 0.6f;
                float next;
                // 停著還按煞車：慢慢往後牽
                if (_brake > 0f && _controlled && _throttle <= 0f && _speed < 0.3f) next = _speed + (-0.8f - _speed) * 3f * dt;
                // 煞車只會往 0 收（Mathf.Sign(0) 是 1，用它算會讓停著的車前後抖、慢慢滑走）
                else if (_brake > 0f) next = Mathf.MoveTowards(_speed + accel * dt, 0f, brakeDecel * _brake * dt);
                else
                {
                    next = _speed + accel * dt;
                    if (_throttle <= 0f) next = Mathf.MoveTowards(next, 0f, coastDecel * dt);
                }
                _speed = Mathf.Clamp(next, -1.5f, maxSpeed * 1.3f);

                // 轉向：速度越快能轉的角度越小
                float steerAngle = _steer * maxSteer / (1f + Mathf.Abs(_speed) * 0.18f);
                float yawRate = _speed * Mathf.Tan(steerAngle * Mathf.Deg2Rad) / wheelBase;
                _yaw += yawRate * Mathf.Rad2Deg * dt;

                Vector3 v = slopeFwd * _speed;
                float intoGround = Vector3.Dot(_rb.linearVelocity, _groundNormal);
                v += _groundNormal * Mathf.Min(0f, intoGround);       // 往下掉的速度留著（貼地）
                _rb.linearVelocity = v;
                _lean = Mathf.Lerp(_lean, Mathf.Clamp(-Mathf.Atan(_speed * yawRate / 9.81f) * Mathf.Rad2Deg, -28f, 28f), dt * 6f);
            }
            else
            {
                _lean = Mathf.Lerp(_lean, 0f, dt * 3f);
            }
            _rb.MoveRotation(Quaternion.Euler(0f, _yaw, 0f));
            _rb.angularVelocity = Vector3.zero;
        }

        void ProbeGround()
        {
            var origin = transform.position + Vector3.up * 0.4f;
            int n = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, 0.65f, ~0, QueryTriggerInteraction.Ignore);
            _grounded = false;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (System.Array.IndexOf(_ownColliders, _hits[i].collider) >= 0) continue;
                if (_hits[i].distance < best) { best = _hits[i].distance; _groundNormal = _hits[i].normal; _grounded = true; }
            }
            if (!_grounded) _groundNormal = Vector3.up;
        }

        // ------------------------------------------------------------------ 外觀
        void Update()
        {
            if (_rb.isKinematic) _yaw = transform.eulerAngles.y;   // 別人在騎，跟著同步的方向
            float speed = SpeedKmh / 3.6f;
            float dt = Time.deltaTime;

            _wheelAngle += speed / wheelRadius * Mathf.Rad2Deg * dt;
            if (frontWheel) frontWheel.localRotation = Quaternion.Euler(_wheelAngle, 0f, 0f);
            if (rearWheel) rearWheel.localRotation = Quaternion.Euler(_wheelAngle, 0f, 0f);
            // 踩踏板時曲柄才轉（滑行時停住）
            bool pedaling = _rb.isKinematic ? speed > 0.5f : _throttle > 0.1f && _speed > 0.1f;
            if (pedaling) _crankAngle += speed / wheelRadius / 2.6f * Mathf.Rad2Deg * dt;
            if (crank) crank.localRotation = Quaternion.Euler(_crankAngle, 0f, 0f);
            if (steer) steer.localRotation = Quaternion.Euler(0f, _steer * maxSteer * 0.7f, 0f);

            if (visual)
            {
                var localNormal = Quaternion.Inverse(transform.rotation) * _groundNormal;
                var pitch = Quaternion.FromToRotation(Vector3.up, Vector3.ProjectOnPlane(localNormal, Vector3.right).normalized);
                visual.localRotation = Quaternion.Slerp(visual.localRotation, pitch * Quaternion.Euler(0f, 0f, _lean), dt * 10f);
            }

            // 舊的人形騎士：有鬃狼動作系統時不用（鬃狼模型本身會騎上來）
            // 有人騎才顯示騎士；自己用駕駛座視角時藏起來（不然鏡頭在頭裡面）
            bool ridden = _entry && _entry.HasDriver && !_hasActions;
            if (rider && rider.activeSelf != ridden) rider.SetActive(ridden);
            if (ridden && _riderRenderers.Length > 0)
            {
                var cam = FindAnyObjectByType<DSTChaseCamera>();
                bool firstPerson = _entry.IsLocalDriver && cam && cam.cockpit;
                var mode = firstPerson ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : UnityEngine.Rendering.ShadowCastingMode.On;
                foreach (var r in _riderRenderers) if (r.shadowCastingMode != mode) r.shadowCastingMode = mode;
            }
        }

        // ------------------------------------------------------------------ 租借 / 還車（只有騎的人那台電腦會跑）
        void OnRent()
        {
            _rentStart = Time.time;
            // 停在車柱上（車頭朝車柱）：牽出來、轉向，等這台電腦拿到物理之後再做（有動作系統時上車前就做過了）
            if (!_pullOutPending) _pullOutPending = DSTMeBikeStation.IsDocked(transform.position);
            _yaw = transform.eulerAngles.y;
            _speed = 0f;
            DSTNotice.Show("租借成功，開始計時", "MeBike 公共自行車");
        }

        /// <summary>鬃狼要上車了：如果還停在車柱上，先牽出來轉向（要等拿到物理才會真的動）</summary>
        public void PullOutIfDocked()
        {
            if (DSTMeBikeStation.IsDocked(transform.position)) _pullOutPending = true;
        }

        /// <summary>還在等牽出來（走過去的目標還不準）</summary>
        public bool PullOutPending => _pullOutPending;

        /// <summary>鬃狼下車動作做完：還車（停回車柱、計費）</summary>
        public void FinishReturn()
        {
            if (!_returnPending) return;
            _returnPending = false;
            OnReturn();
        }

        void OnReturn()
        {
            if (_rentStart < 0f) return;
            float minutes = (Time.time - _rentStart) / 60f;
            _rentStart = -1f;
            string fee = $"NT${Fare(minutes)}";
            string time = FormatTime(minutes * 60f);
            // 停在站點旁：自動停進空的車柱
            if (DSTMeBikeStation.TryFindFreeDock(transform.position, 3.5f, this, out var pos, out var rot))
            {
                Place(pos, rot);
                DSTNotice.Show($"還車成功　騎乘 {time}　扣款 {fee}", "MeBike 已停回站點");
            }
            else
                DSTNotice.Show($"騎乘 {time}　扣款 {fee}（未在站點，之後會被載回站點）", "MeBike 已停放");
        }

        // 直接把車擺到某處（還車、主機調度）。停著的車方向是鎖住的，要搬車一定要走這裡
        public void Place(Vector3 pos, Quaternion rot)
        {
            _speed = 0f;
            _yaw = rot.eulerAngles.y;
            if (!_rb.isKinematic) _rb.linearVelocity = Vector3.zero;
            _rb.position = pos;
            _rb.rotation = rot;
            transform.SetPositionAndRotation(pos, rot);
            if (TryGetComponent<Unity.Netcode.Components.NetworkTransform>(out var nt) && nt.IsSpawned && nt.CanCommitToTransform)
                nt.Teleport(pos, rot, transform.localScale);
        }

        public static int Fare(float minutes)
        {
            if (minutes <= 30f) return 5;
            return 5 + Mathf.CeilToInt((minutes - 30f) / 30f) * 10;
        }

        static string FormatTime(float seconds)
        {
            int s = Mathf.FloorToInt(seconds);
            return $"{s / 60:00}:{s % 60:00}";
        }

        void OnGUI()
        {
            if (_rentStart < 0f || !_entry || !_entry.IsLocalDriver) return;
            float sec = Time.time - _rentStart;
            DSTHud.TopPanel($"MeBike　騎乘 {FormatTime(sec)}　NT${Fare(sec / 60f)}　{SpeedKmh:0} km/h");
        }
    }
}
