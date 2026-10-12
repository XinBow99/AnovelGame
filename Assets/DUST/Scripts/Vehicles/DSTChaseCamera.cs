using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Vehicles
{
    // 追車攝影機；C 鍵（手把 Y）切換駕駛座視角
    public class DSTChaseCamera : MonoBehaviour
    {
        public DSTVehicleController target;
        public Transform cockpitEye;
        public float distance = 6.5f;
        public float height = 2.2f;
        public float lookHeight = 1.2f;
        public float positionDamping = 6f;
        public float rotationDamping = 4f;
        public bool cockpit;

        float _yaw;

        // 每次啟用（上車）都直接放到車後方，不要從上次的位置慢慢飛過來
        void OnEnable()
        {
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
            if ((Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame) ||
                (Gamepad.current != null && Gamepad.current.buttonNorth.wasPressedThisFrame))
                cockpit = !cockpit;
#else
            if (Input.GetKeyDown(KeyCode.C)) cockpit = !cockpit;
#endif
            var t = target.transform;
            if (cockpit && cockpitEye)
            {
                transform.SetPositionAndRotation(cockpitEye.position, Quaternion.LookRotation(t.forward, t.up));
                return;
            }

            // 倒車時不要把鏡頭甩到車頭
            float wantYaw = t.eulerAngles.y;
            _yaw = Mathf.LerpAngle(_yaw, wantYaw, 1f - Mathf.Exp(-rotationDamping * Time.deltaTime));
            var rot = Quaternion.Euler(0f, _yaw, 0f);
            var want = t.position + Vector3.up * height - rot * Vector3.forward * distance;

            // 不要穿牆：從車往鏡頭打射線
            var from = t.position + Vector3.up * lookHeight;
            var dir = want - from;
            if (Physics.SphereCast(from, 0.25f, dir.normalized, out var hit, dir.magnitude, ~0, QueryTriggerInteraction.Ignore)
                && !hit.transform.IsChildOf(t))
                want = from + dir.normalized * Mathf.Max(1.0f, hit.distance - 0.2f);

            transform.position = Vector3.Lerp(transform.position, want, 1f - Mathf.Exp(-positionDamping * Time.deltaTime));
            transform.rotation = Quaternion.LookRotation(t.position + Vector3.up * lookHeight - transform.position);
        }
    }
}
