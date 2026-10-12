using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Vehicles
{
    // 四輪載具控制：WheelCollider 物理 + 視覺同步。
    // 操作：W/S 油門與煞車（停住後按 S 倒車）、A/D 轉向、Space 手煞車、R 翻正。手把：RT/LT、左搖桿、A 鍵。
    [RequireComponent(typeof(Rigidbody))]
    public class DSTVehicleController : MonoBehaviour, IDSTDrivable
    {
        [System.Serializable]
        public class Axle
        {
            public WheelCollider left;
            public WheelCollider right;
            public Transform leftVisual;
            public Transform rightVisual;
            public bool steer;
            public bool motor = true;
            public bool handbrake;
            public float antiRoll = 8000f;
        }

        [Header("Wheels")]
        public Axle front = new Axle { steer = true, antiRoll = 9000f };
        public Axle rear = new Axle { handbrake = true, antiRoll = 7000f };

        [Header("Chassis")]
        public Transform centerOfMass;
        public Transform steeringWheel;
        public Vector3 steeringWheelAxis = Vector3.up;
        public float steeringWheelRatio = 9f;

        [Header("Engine / Brakes")]
        public float maxMotorTorque = 750f;    // 每個驅動輪（四驅）
        public float maxBrakeTorque = 3000f;
        public float handbrakeTorque = 5000f;
        public float topSpeedKmh = 120f;
        public float reverseTopSpeedKmh = 30f;

        [Header("Steering")]
        public float maxSteerAngle = 34f;
        public float steerAngleAtSpeed = 10f;
        public float steerFalloffSpeedKmh = 100f;
        public float steerRateDegPerSec = 160f;

        [Header("Stability")]
        public float downforce = 2.5f;          // N per (m/s)^2
        public float straightLineAssist = 1.5f; // 沒轉向時抑制偏航（WheelCollider 直線會微微拉偏）

        [Header("Control")]
        public bool playerControlled = false;
        public bool holdBrakeWhenEmpty = true;   // 沒人開的時候拉手煞車

        // 連線時別人在開的車是 Kinematic（位置由網路同步），速度改用位移估算
        public float SpeedKmh => !_rb ? 0f : _rb.isKinematic ? _syncedSpeedKmh : Vector3.Dot(_rb.linearVelocity, transform.forward) * 3.6f;
        public float Throttle { get; private set; }
        public float Steer { get; private set; }
        public bool Handbrake { get; private set; }
        /// <summary>方向盤目前轉了幾度（正 = 右轉），給騎乘動作的前腳跟著轉</summary>
        public float SteeringWheelAngle => _currentSteer * steeringWheelRatio;

        public bool PlayerControlled
        {
            get => playerControlled;
            set
            {
                playerControlled = value;
                if (!value) SetInput(0f, 0f, true);
            }
        }

        public string ControlsHint => "[F] 下車    [C] 切換視角    [Space] 手煞車";
        public string EnterHint => "[F] 上車";

        public bool CanExit(float maxExitSpeedKmh, out string reason)
        {
            reason = "先減速才能下車";
            return Mathf.Abs(SpeedKmh) <= maxExitSpeedKmh;
        }

        Rigidbody _rb;
        float _currentSteer;
        Quaternion[] _visualOffsets;
        Quaternion _steeringWheelRest;
        bool _initialised;
        Vector3 _lastPosition;
        float _syncedSpeedKmh;

        void Awake() => Init();

        public void Init()
        {
            _rb = GetComponent<Rigidbody>();
            if (centerOfMass)
                _rb.centerOfMass = transform.InverseTransformPoint(centerOfMass.position);
            var visuals = Visuals();
            _visualOffsets = new Quaternion[visuals.Length];
            for (int i = 0; i < visuals.Length; i++)
                _visualOffsets[i] = visuals[i] ? Quaternion.Inverse(transform.rotation) * visuals[i].rotation : Quaternion.identity;
            if (steeringWheel)
                _steeringWheelRest = steeringWheel.localRotation;
            _initialised = true;
        }

        Transform[] Visuals() => new[] { front.leftVisual, front.rightVisual, rear.leftVisual, rear.rightVisual };
        WheelCollider[] Colliders() => new[] { front.left, front.right, rear.left, rear.right };

        // 外部（AI、測試）直接給輸入
        public void SetInput(float throttle, float steer, bool handbrake)
        {
            Throttle = Mathf.Clamp(throttle, -1f, 1f);
            Steer = Mathf.Clamp(steer, -1f, 1f);
            Handbrake = handbrake;
        }

        void Update()
        {
            if (playerControlled)
                ReadPlayerInput();
            else if (holdBrakeWhenEmpty)
                SetInput(0f, 0f, true);
            UpdateVisuals();
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (_rb.isKinematic)
            {
                // 不是這台電腦在跑這台車的物理
                float kmh = Vector3.Dot(transform.position - _lastPosition, transform.forward) / dt * 3.6f;
                _syncedSpeedKmh = Mathf.Lerp(_syncedSpeedKmh, kmh, 0.2f);
            }
            else
                PhysicsStep(dt);
            _lastPosition = transform.position;
        }

        void ReadPlayerInput()
        {
            float v = 0f, h = 0f;
            bool hb = false, reset = false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) h += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) h -= 1f;
                hb = kb.spaceKey.isPressed;
                reset = kb.rKey.wasPressedThisFrame;
            }
            var gp = Gamepad.current;
            if (gp != null)
            {
                v += gp.rightTrigger.ReadValue() - gp.leftTrigger.ReadValue();
                h += gp.leftStick.x.ReadValue();
                hb |= gp.buttonSouth.isPressed;
                reset |= gp.buttonNorth.wasPressedThisFrame;
            }
#else
            v = Input.GetAxis("Vertical");
            h = Input.GetAxis("Horizontal");
            hb = Input.GetKey(KeyCode.Space);
            reset = Input.GetKeyDown(KeyCode.R);
#endif
            SetInput(v, h, hb);
            if (reset)
                ResetUpright();
        }

        public void PhysicsStep(float dt)
        {
            if (!_initialised)
                Init();
            float speed = Vector3.Dot(_rb.linearVelocity, transform.forward); // m/s，前進為正
            float kmh = Mathf.Abs(speed) * 3.6f;

            // 油門 / 煞車 / 倒車
            float motor = 0f, brake = 0f;
            if (Throttle > 0.01f)
            {
                if (speed < -0.5f) brake = Throttle;
                else motor = Throttle * TorqueCurve(kmh, topSpeedKmh);
            }
            else if (Throttle < -0.01f)
            {
                if (speed > 0.5f) brake = -Throttle;
                else motor = Throttle * TorqueCurve(kmh, reverseTopSpeedKmh);
            }
            else if (kmh < 2f)
            {
                brake = 0.15f; // 放開油門時停穩，不滑動
            }

            // 轉向（速度越快角度越小）
            float steerLimit = Mathf.Lerp(maxSteerAngle, steerAngleAtSpeed, Mathf.Clamp01(kmh / steerFalloffSpeedKmh));
            _currentSteer = Mathf.MoveTowards(_currentSteer, Steer * steerLimit, steerRateDegPerSec * dt);

            ApplyAxle(front, motor, brake, dt);
            ApplyAxle(rear, motor, brake, dt);

            _rb.AddForce(-transform.up * downforce * speed * speed);

            if (Mathf.Abs(Steer) < 0.05f && Mathf.Abs(speed) > 3f && !Handbrake)
            {
                float yawRate = Vector3.Dot(_rb.angularVelocity, transform.up);
                _rb.AddTorque(-transform.up * yawRate * straightLineAssist * _rb.mass, ForceMode.Force);
            }
        }

        static float TorqueCurve(float kmh, float topKmh)
        {
            float x = Mathf.Clamp01(kmh / Mathf.Max(1f, topKmh));
            return 1f - x * x;
        }

        void ApplyAxle(Axle a, float motor, float brake, float dt)
        {
            foreach (var wc in new[] { a.left, a.right })
            {
                if (!wc) continue;
                if (a.steer) wc.steerAngle = _currentSteer;
                wc.motorTorque = a.motor ? motor * maxMotorTorque : 0f;
                float bt = brake * maxBrakeTorque;
                if (a.handbrake && Handbrake) bt = Mathf.Max(bt, handbrakeTorque);
                wc.brakeTorque = bt;
            }
            AntiRoll(a);
        }

        void AntiRoll(Axle a)
        {
            if (!a.left || !a.right) return;
            float travelL = 1f, travelR = 1f;
            bool gl = a.left.GetGroundHit(out var hitL);
            bool gr = a.right.GetGroundHit(out var hitR);
            if (gl) travelL = (-a.left.transform.InverseTransformPoint(hitL.point).y - a.left.radius) / a.left.suspensionDistance;
            if (gr) travelR = (-a.right.transform.InverseTransformPoint(hitR.point).y - a.right.radius) / a.right.suspensionDistance;
            float force = (travelL - travelR) * a.antiRoll;
            if (gl) _rb.AddForceAtPosition(a.left.transform.up * -force, a.left.transform.position);
            if (gr) _rb.AddForceAtPosition(a.right.transform.up * force, a.right.transform.position);
        }

        public void UpdateVisuals()
        {
            if (!_initialised) Init();
            var cols = Colliders();
            var vis = Visuals();
            for (int i = 0; i < cols.Length; i++)
            {
                if (!cols[i] || !vis[i]) continue;
                cols[i].GetWorldPose(out var pos, out var rot);
                vis[i].SetPositionAndRotation(pos, rot * _visualOffsets[i]);
            }
            if (steeringWheel)
                steeringWheel.localRotation = _steeringWheelRest * Quaternion.AngleAxis(_currentSteer * steeringWheelRatio, steeringWheelAxis);
        }

        public void ResetUpright()
        {
            var fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            transform.SetPositionAndRotation(transform.position + Vector3.up * 1.0f, Quaternion.LookRotation(fwd, Vector3.up));
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
    }
}
