using DUST.Player;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Vehicles
{
    // 追車攝影機；C 鍵（手把 Y）切換駕駛座視角。
    // 滑鼠（手把右搖桿）自由繞著車子看 / 駕駛座裡轉頭；放著不動一下子就自己轉回車子後方。
    public class DSTChaseCamera : MonoBehaviour
    {
        public Transform target;          // 上車時由 DSTVehicleEntry 指定（皮卡、無人機共用這台鏡頭）
        public Transform cockpitEye;
        public float distance = 6.5f;
        public float height = 2.2f;
        public float lookHeight = 1.2f;
        public float positionDamping = 6f;
        public float rotationDamping = 4f;
        public bool cockpit;

        [Header("Free look")]
        public float mouseSensitivity = 0.1f;   // 度 / 像素（跟走路一樣）
        public float recenterDelay = 2.5f;      // 滑鼠停多久開始轉回去
        public float recenterTime = 1.2f;       // 轉回去大約花多久（緩起緩停）
        public float recenterMaxSpeed = 90f;    // 度 / 秒；太快會暈
        public float recenterMinSpeed = 2f;     // 車子比這慢（停著）就不轉回去，讓人慢慢看

        float _yaw;
        float _defaultDistance = -1f, _defaultHeight;
        float _freeYaw, _freePitch, _lastLook = -99f;

        // 每台載具可以有自己的追車距離；0 = 用預設
        public void SetFraming(float dist, float h)
        {
            if (_defaultDistance < 0f) { _defaultDistance = distance; _defaultHeight = height; }
            distance = dist > 0f ? dist : _defaultDistance;
            height = h > 0f ? h : _defaultHeight;
        }

        // 每次啟用（上車）都直接放到車後方，不要從上次的位置慢慢飛過來
        void OnEnable()
        {
            _freeYaw = 0f; _freePitch = 0f; _lastLook = -99f;
            if (!target) return;
            var t = target.transform;
            _yaw = t.eulerAngles.y;
            transform.position = t.position + Vector3.up * height - Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward * distance;
            transform.rotation = Quaternion.LookRotation(t.position + Vector3.up * lookHeight - transform.position);
        }

        void LateUpdate()
        {
            if (!target) return;
#if ENABLE_INPUT_SYSTEM
            if ((Keyboard.current != null && !DUST.UI.DSTChat.ConsumesKeys && Keyboard.current.cKey.wasPressedThisFrame) ||
                (Gamepad.current != null && Gamepad.current.buttonNorth.wasPressedThisFrame))
                cockpit = !cockpit;
#else
            if (Input.GetKeyDown(KeyCode.C)) cockpit = !cockpit;
#endif
            UpdateFreeLook();
            var t = target.transform;
            if (cockpit && cockpitEye)
            {
                // 駕駛座：轉頭看（左右 ±120°、上下 ±60°）
                var head = Quaternion.LookRotation(t.forward, t.up) * Quaternion.Euler(Mathf.Clamp(_freePitch, -60f, 60f), Mathf.Clamp(_freeYaw, -120f, 120f), 0f);
                transform.SetPositionAndRotation(cockpitEye.position, head);
                return;
            }

            // 倒車時不要把鏡頭甩到車頭
            float wantYaw = t.eulerAngles.y;
            _yaw = Mathf.LerpAngle(_yaw, wantYaw, 1f - Mathf.Exp(-rotationDamping * Time.deltaTime));
            var rot = Quaternion.Euler(0f, _yaw + _freeYaw, 0f);
            // 上下：繞著看的點轉（正 = 鏡頭抬高往下看）
            var offset = rot * (Quaternion.Euler(_freePitch, 0f, 0f) * new Vector3(0f, height - lookHeight, -distance));
            var from = t.position + Vector3.up * lookHeight;
            var want = from + offset;

            // 不要穿牆：從車往鏡頭打射線（有剛體的東西不擋，例如站上其他腳踏車）
            var dir = want - from;
            if (Physics.SphereCast(from, 0.25f, dir.normalized, out var hit, dir.magnitude, ~0, QueryTriggerInteraction.Ignore)
                && !hit.transform.IsChildOf(t) && !hit.collider.attachedRigidbody)
                want = from + dir.normalized * Mathf.Max(1.0f, hit.distance - 0.2f);

            // 滑鼠正在轉的時候跟手一點，不然會覺得鏡頭黏黏的
            float damp = Time.time - _lastLook < 0.3f ? positionDamping * 3f : positionDamping;
            transform.position = Vector3.Lerp(transform.position, want, 1f - Mathf.Exp(-damp * Time.deltaTime));
            transform.rotation = Quaternion.LookRotation(from - transform.position);
        }

        void UpdateFreeLook()
        {
            var look = DSTPlayerController.ReadLookInput(mouseSensitivity);
            if (look.sqrMagnitude > 1e-4f)
            {
                _freeYaw = Mathf.Repeat(_freeYaw + look.x + 180f, 360f) - 180f;
                _freePitch = Mathf.Clamp(_freePitch - look.y, cockpit ? -60f : -25f, 60f);
                _lastLook = Time.time;
            }
            else if (Time.time - _lastLook > recenterDelay && Moving())
            {
                // SmoothDamp + 速度上限：慢慢加速、慢慢停，不會一下子甩回車後
                _freeYaw = Mathf.SmoothDampAngle(_freeYaw, 0f, ref _yawVel, recenterTime * 0.5f, recenterMaxSpeed);
                _freePitch = Mathf.SmoothDamp(_freePitch, 0f, ref _pitchVel, recenterTime * 0.5f, recenterMaxSpeed);
                return;
            }
            _yawVel = _pitchVel = 0f;
        }

        float _yawVel, _pitchVel;

        bool Moving()
        {
            var rb = target ? target.GetComponentInParent<Rigidbody>() : null;
            return !rb || rb.linearVelocity.sqrMagnitude > recenterMinSpeed * recenterMinSpeed;
        }
    }
}
