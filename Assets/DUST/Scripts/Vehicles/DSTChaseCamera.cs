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
        public float recenterDelay = 10f;       // 滑鼠停多久開始轉回去
        public float recenterTime = 1.2f;       // 轉回去大約花多久（緩起緩停）
        public float recenterMaxSpeed = 90f;    // 度 / 秒；太快會暈
        public float recenterMinSpeed = 2f;     // 車子比這慢（停著）就不轉回去，讓人慢慢看

        float _yaw;
        float _defaultDistance = -1f, _defaultHeight;
        float _freeYaw, _freePitch, _lastLook = -99f;
        float _focusY, _camDist;
        bool _snapFocus = true;

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
            _snapFocus = true; _camDist = 0f;
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
            if (!_cam) _cam = GetComponent<Camera>();
            if (_cam)
            {
                if (_defaultNear < 0f) _defaultNear = _cam.nearClipPlane;
                _cam.nearClipPlane = cockpit && cockpitEye ? 0.05f : _defaultNear;   // 座艙裡儀表板、操縱桿很近
                _cam.farClipPlane = 250000f;  // 高空（米格-25）：遠景地形 128 km、水平線霧帶 150 km、地平面 200 km；HDRP 用反向深度，近 0.05 也沒有精度問題
            }
            SetMotionBlur(!(cockpit && cockpitEye));   // 座艙裡鏡頭跟著機身動，動態模糊會把儀表板整片糊掉（實測）
            if (cockpit && cockpitEye)
            {
                // 駕駛座：轉頭看（左右 ±120°、上下 ±60°）。基準方向跟著眼睛標記（米格-25 往下看 8° 才看得到儀表板）
                var head = Quaternion.LookRotation(cockpitEye.forward, cockpitEye.up) * Quaternion.Euler(Mathf.Clamp(_freePitch, -60f, 60f), Mathf.Clamp(_freeYaw, -120f, 120f), 0f);
                transform.SetPositionAndRotation(cockpitEye.position, head);
                return;
            }

            // 倒車時不要把鏡頭甩到車頭
            float wantYaw = t.eulerAngles.y;
            _yaw = Mathf.LerpAngle(_yaw, wantYaw, 1f - Mathf.Exp(-rotationDamping * Time.deltaTime));
            var rot = Quaternion.Euler(0f, _yaw + _freeYaw, 0f);
            // 上下：繞著看的點轉（正 = 鏡頭抬高往下看）
            var offset = rot * (Quaternion.Euler(_freePitch, 0f, 0f) * new Vector3(0f, height - lookHeight, -distance));
            // 鏡頭和車子保持固定距離（不再用位置追趕：車越快落後越多，加速時像拉遠鏡頭，會暈）。
            // 只有高度稍微平滑，壓過顛簸時不會跟著抖
            var tp = t.position;
            _focusY = _snapFocus ? tp.y : Mathf.Lerp(_focusY, tp.y, 1f - Mathf.Exp(-positionDamping * 1.5f * Time.deltaTime));
            _snapFocus = false;
            var from = new Vector3(tp.x, _focusY + lookHeight, tp.z);

            // 不要穿牆：從車往鏡頭打射線（有剛體的東西不擋，例如站上其他腳踏車）；被擋住時馬上拉近，離開後慢慢退回去
            var dir = offset.normalized;
            float want = offset.magnitude;
            if (Physics.SphereCast(from, 0.25f, dir, out var hit, want, ~0, QueryTriggerInteraction.Ignore)
                && !hit.transform.IsChildOf(t) && !hit.collider.attachedRigidbody)
                want = Mathf.Max(1.0f, hit.distance - 0.2f);
            _camDist = want < _camDist || _camDist <= 0f ? want : Mathf.Lerp(_camDist, want, 1f - Mathf.Exp(-2f * Time.deltaTime));

            transform.position = from + dir * _camDist;
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
        bool? _blurOn;

        void SetMotionBlur(bool on)
        {
            if (_blurOn == on || !_cam) return;
            _blurOn = on;
            var hd = GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
            if (!hd) return;
            hd.customRenderingSettings = true;
            hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)UnityEngine.Rendering.HighDefinition.FrameSettingsField.MotionBlur] = true;
            hd.renderingPathCustomFrameSettings.SetEnabled(UnityEngine.Rendering.HighDefinition.FrameSettingsField.MotionBlur, on);
        }
        Camera _cam;
        float _defaultNear = -1f;

        bool Moving()
        {
            var rb = target ? target.GetComponentInParent<Rigidbody>() : null;
            return !rb || rb.linearVelocity.sqrMagnitude > recenterMinSpeed * recenterMinSpeed;
        }
    }
}
