using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Vehicles
{
    // 無人機（三角翼、尾部推進式螺旋槳）：街機式飛行，可以懸停、垂直起降，地圖不大不需要跑道。
    // 操作：W/S 推力（加速 / 減速倒退）、A/D 轉向（機身自動側傾）、Space 爬升、Ctrl 下降、R 翻正。
    // 手把：RT/LT 推力、左搖桿轉向、A 爬升、B 下降。
    // 沒人駕駛時引擎熄火，靠重力落地（在空中下機會直接摔下來，所以要先降落）。
    [RequireComponent(typeof(Rigidbody))]
    public class DSTDroneController : MonoBehaviour, IDSTDrivable
    {
        [Header("Parts")]
        public Transform propeller;
        public Transform centerOfMass;

        [Header("Flight")]
        public float maxForwardSpeed = 18f;     // m/s（半尺寸世界，約等於實機 130 km/h 的感覺）
        public float maxReverseSpeed = 4f;
        public float forwardAccel = 6f;
        public float climbSpeed = 4f;
        public float verticalAccel = 6f;
        public float yawRateDeg = 70f;
        public float lateralDamping = 3f;       // 抵銷側滑，轉彎像飛機不像冰上
        public float maxBankDeg = 30f;
        public float maxPitchDeg = 12f;
        public float attitudeResponse = 4f;
        public float ceiling = 45f;             // 相對起飛點的最高高度，免得飛出地圖
        public Bounds flightArea;               // 水平飛行範圍（地圖外沒有地面，飛出去會一路掉下去）；size 為 0 = 不限制
        public float edgeSoftness = 2f;         // 靠近邊界時，往外的速度上限 = 距離 × 這個值

        [Header("Ground")]
        public float groundedHeight = 0.35f;
        public float propIdleRpm = 900f;
        public float propMaxRpm = 3200f;

        public bool PlayerControlled
        {
            get => _controlled;
            set
            {
                _controlled = value;
                if (value) _ceilingY = Mathf.Max(_ceilingY, transform.position.y + ceiling);
                else { _thrust = 0f; _steer = 0f; _lift = 0f; }
            }
        }

        public float SpeedKmh => !_rb ? 0f : _rb.isKinematic ? _syncedSpeedKmh : Vector3.Dot(_rb.linearVelocity, transform.forward) * 3.6f;
        public bool IsGrounded { get; private set; }
        public string ControlsHint => "[F] 下機    [W/S] 推力    [A/D] 轉向    [Space/Ctrl] 升降    [C] 切換視角    [滑鼠] 環顧";
        public string EnterHint => "[F] 上機";

        public bool CanExit(float maxExitSpeedKmh, out string reason)
        {
            if (!IsGrounded) { reason = "先降落才能下機"; return false; }
            reason = "先減速才能下機";
            return Mathf.Abs(SpeedKmh) <= maxExitSpeedKmh;
        }

        Rigidbody _rb;
        bool _controlled;
        float _thrust, _steer, _lift;   // 玩家輸入，-1..1
        float _yaw;
        float _ceilingY;
        float _propRpm;
        Vector3 _lastPosition;
        float _syncedSpeedKmh;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (centerOfMass) _rb.centerOfMass = transform.InverseTransformPoint(centerOfMass.position);
            _yaw = transform.eulerAngles.y;
            _ceilingY = transform.position.y + ceiling;
            _lastPosition = transform.position;
        }

        // 外部（AI、測試）直接給輸入；打開 externalInput 時不讀鍵盤 / 手把
        [HideInInspector] public bool externalInput;

        public void SetInput(float thrust, float steer, float lift)
        {
            _thrust = Mathf.Clamp(thrust, -1f, 1f);
            _steer = Mathf.Clamp(steer, -1f, 1f);
            _lift = Mathf.Clamp(lift, -1f, 1f);
        }

        void Update()
        {
            if (_controlled && !externalInput) ReadPlayerInput();
            SpinPropeller(Time.deltaTime);
        }

        void ReadPlayerInput()
        {
            float t = 0f, s = 0f, l = 0f;
            bool reset = false;
#if ENABLE_INPUT_SYSTEM
            var kb = DUST.UI.DSTChat.Typing ? null : Keyboard.current;   // 聊天打字中不吃鍵盤
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) t += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) t -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) s += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) s -= 1f;
                if (kb.spaceKey.isPressed) l += 1f;
                if (kb.leftCtrlKey.isPressed) l -= 1f;
                reset = kb.rKey.wasPressedThisFrame;
            }
            var gp = Gamepad.current;
            if (gp != null)
            {
                t += gp.rightTrigger.ReadValue() - gp.leftTrigger.ReadValue();
                s += gp.leftStick.x.ReadValue();
                if (gp.buttonSouth.isPressed) l += 1f;
                if (gp.buttonEast.isPressed) l -= 1f;
            }
#else
            t = Input.GetAxis("Vertical");
            s = Input.GetAxis("Horizontal");
            l = (Input.GetKey(KeyCode.Space) ? 1f : 0f) - (Input.GetKey(KeyCode.LeftControl) ? 1f : 0f);
            reset = Input.GetKeyDown(KeyCode.R);
#endif
            _thrust = Mathf.Clamp(t, -1f, 1f);
            _steer = Mathf.Clamp(s, -1f, 1f);
            _lift = Mathf.Clamp(l, -1f, 1f);
            if (reset) ResetUpright();
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            IsGrounded = CheckGrounded();

            if (_rb.isKinematic)
            {
                // 別人在飛：位置由網路同步，速度用位移估算（給螺旋槳和動物警戒用）
                float kmh = Vector3.Dot(transform.position - _lastPosition, transform.forward) / dt * 3.6f;
                _syncedSpeedKmh = Mathf.Lerp(_syncedSpeedKmh, kmh, 0.2f);
                _lastPosition = transform.position;
                return;
            }
            _lastPosition = transform.position;

            if (!_controlled)
            {
                // 熄火：只剩重力，飛行姿態慢慢回正，落地後不要一直滑
                _rb.useGravity = true;
                _rb.linearDamping = IsGrounded ? 2f : 0.1f;
                _rb.angularDamping = IsGrounded ? 4f : 0.5f;
                _yaw = transform.eulerAngles.y;
                _rb.linearVelocity = KeepInside(_rb.linearVelocity);
                return;
            }

            _rb.useGravity = false;     // 引擎開著就抵銷重力（街機式懸停）
            _rb.linearDamping = 0f;
            _rb.angularDamping = 2f;

            // 速度：前後由推力決定、上下由升降鍵決定、側滑被吃掉
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 v = _rb.linearVelocity;
            float vF = Vector3.Dot(v, fwd), vR = Vector3.Dot(v, right), vU = v.y;

            float wantF = _thrust >= 0f ? _thrust * maxForwardSpeed : _thrust * maxReverseSpeed;
            float wantU = _lift * climbSpeed;
            if (transform.position.y > _ceilingY && wantU > 0f) wantU = 0f;
            if (IsGrounded && wantU < 0f) wantU = 0f;

            vF = Mathf.MoveTowards(vF, wantF, forwardAccel * dt);
            vU = Mathf.MoveTowards(vU, wantU, verticalAccel * dt);
            vR = Mathf.Lerp(vR, 0f, 1f - Mathf.Exp(-lateralDamping * dt));
            _rb.linearVelocity = KeepInside(fwd * vF + right * vR + Vector3.up * vU);

            // 姿態：轉向時側傾、爬升 / 下降時抬頭 / 低頭（純視覺，飛行方向由上面的速度決定）
            float speed01 = Mathf.Clamp01(Mathf.Abs(vF) / maxForwardSpeed);
            _yaw += _steer * yawRateDeg * Mathf.Lerp(0.6f, 1f, speed01) * dt;
            float bank = -_steer * maxBankDeg * Mathf.Lerp(0.3f, 1f, speed01);
            float pitch = -_lift * maxPitchDeg;
            if (IsGrounded && _lift <= 0f) { bank = 0f; pitch = 0f; }
            var want = Quaternion.Euler(pitch, _yaw, bank);
            _rb.MoveRotation(Quaternion.Slerp(_rb.rotation, want, 1f - Mathf.Exp(-attitudeResponse * dt)));
            _rb.angularVelocity = Vector3.zero;
        }

        // 越靠近邊界，往外飛的速度上限越低，貼到邊界時降到 0（像撞到看不見的軟牆）
        Vector3 KeepInside(Vector3 v)
        {
            if (flightArea.size.x <= 0f || flightArea.size.z <= 0f) return v;
            Vector3 p = _rb.position;
            if (v.x > 0f) v.x = Mathf.Min(v.x, Mathf.Max(0f, flightArea.max.x - p.x) * edgeSoftness);
            if (v.x < 0f) v.x = Mathf.Max(v.x, -Mathf.Max(0f, p.x - flightArea.min.x) * edgeSoftness);
            if (v.z > 0f) v.z = Mathf.Min(v.z, Mathf.Max(0f, flightArea.max.z - p.z) * edgeSoftness);
            if (v.z < 0f) v.z = Mathf.Max(v.z, -Mathf.Max(0f, p.z - flightArea.min.z) * edgeSoftness);
            return v;
        }

        readonly RaycastHit[] _hits = new RaycastHit[8];

        // 從機腹往下打，略過自己的碰撞體
        bool CheckGrounded()
        {
            int n = Physics.RaycastNonAlloc(transform.position + Vector3.up * 0.3f, Vector3.down, _hits, 0.3f + groundedHeight, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (!_hits[i].transform.IsChildOf(transform)) return true;
            return false;
        }

        void SpinPropeller(float dt)
        {
            if (!propeller) return;
            bool engineOn = _controlled || (_rb.isKinematic && Mathf.Abs(_syncedSpeedKmh) > 1f);
            float throttle = _controlled ? Mathf.Max(Mathf.Abs(_thrust), Mathf.Abs(_lift) * 0.6f) : Mathf.Clamp01(Mathf.Abs(SpeedKmh) / (maxForwardSpeed * 3.6f));
            float want = engineOn ? Mathf.Lerp(propIdleRpm, propMaxRpm, throttle) : 0f;
            _propRpm = Mathf.MoveTowards(_propRpm, want, 2500f * dt);
            propeller.Rotate(0f, 0f, _propRpm * 6f * dt, Space.Self);   // rpm → 度/秒
        }

        public void ResetUpright()
        {
            _yaw = transform.eulerAngles.y;
            transform.SetPositionAndRotation(transform.position + Vector3.up * 0.5f, Quaternion.Euler(0f, _yaw, 0f));
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
    }
}
