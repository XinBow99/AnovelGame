using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Player
{
    // 自己角色的鏡頭：第一人稱（鏡頭在鬃狼眼睛）／第三人稱（鏡頭在身後）。走路時按 C（手把 Y）切換；
    // 上下車動作播放時一律暫時切第三人稱，看得到整個動作。
    public class DSTPlayerView : MonoBehaviour
    {
        public Camera viewCamera;               // 掛在 Head（第一人稱的位置）底下
        public float distance = 1.35f;
        public float height = 0.45f;
        public float lookHeight = 0.32f;
        public float actionDistance = 1.7f;     // 上下車時拉遠一點
        public float damping = 10f;

        public bool ThirdPerson { get; private set; }
        public bool Forced => _forceTarget != null;

        DSTPlayerController _pc;
        DSTWolfRig _rig;
        Transform _forceTarget;
        float _orbitYaw;
        bool _snap = true;
        Vector3 _fpLocalPos;

        void Awake()
        {
            _pc = GetComponent<DSTPlayerController>();
            _rig = GetComponent<DSTWolfRig>();
            if (viewCamera) _fpLocalPos = viewCamera.transform.localPosition;
        }

        void OnEnable() => _snap = true;

        /// <summary>上下車：鏡頭改看 target（鬃狼的 Hips），不論玩家選哪種視角；vehicle = 載具（鏡頭待在載具外、鬃狼那一側）</summary>
        public void ForceThirdPerson(Transform target, Transform vehicle = null)
        {
            if (_forceTarget == null) _snap = true;
            _forceTarget = target;
            _forceVehicle = vehicle;
            if (target) _orbitYaw = ActionYaw();
        }
        Transform _forceVehicle;

        // 鏡頭的水平角度：大部分看「載具中心 → 鬃狼」（站在外面看進來），一點點看鬃狼的面向
        float ActionYaw()
        {
            var hips = _forceTarget.position;
            var face = _rig && _rig.Hips && _rig.Head ? Vector3.ProjectOnPlane(_rig.Head.position - _rig.Hips.position, Vector3.up) : transform.forward;
            if (face.sqrMagnitude < 1e-4f) face = transform.forward;
            if (!_forceVehicle) return Quaternion.LookRotation(face).eulerAngles.y + 35f;
            var outward = Vector3.ProjectOnPlane(hips - _forceVehicle.position, Vector3.up);
            if (outward.sqrMagnitude < 0.04f) outward = -_forceVehicle.right;     // 坐在車子中間：從左邊（駕駛側）看
            // 鏡頭在 focus 後方 → 「往內看」的方向就是 -outward
            var look = -outward.normalized * 0.75f + face.normalized * 0.25f;
            return Quaternion.LookRotation(look).eulerAngles.y;
        }

        public void ReleaseForce()
        {
            _forceTarget = null;
            _snap = true;
        }

        void Update()
        {
            if (Forced || !_pc || !_pc.enabled) return;
#if ENABLE_INPUT_SYSTEM
            if ((Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame) ||
                (Gamepad.current != null && Gamepad.current.buttonNorth.wasPressedThisFrame))
                ThirdPerson = !ThirdPerson;
#else
            if (Input.GetKeyDown(KeyCode.C)) ThirdPerson = !ThirdPerson;
#endif
        }

        void LateUpdate()
        {
            if (!viewCamera || !viewCamera.enabled) return;
            var cam = viewCamera.transform;
            bool third = Forced || ThirdPerson;
            if (_rig) _rig.SetShadowOnly(!third);
            if (!third)
            {
                cam.localPosition = _fpLocalPos;
                cam.localRotation = Quaternion.identity;
                return;
            }

            Vector3 focus;
            float pitch = _pc ? _pc.Pitch : 15f, dist = distance;
            if (Forced)
            {
                focus = _forceTarget.position + Vector3.up * 0.12f;
                dist = actionDistance;
                pitch = 18f;
                // 動作時鏡頭慢慢繞到角色側後方
                _orbitYaw = Mathf.LerpAngle(_orbitYaw, ActionYaw(), 1f - Mathf.Exp(-1.2f * Time.deltaTime));
            }
            else
            {
                focus = transform.position + Vector3.up * lookHeight;
                _orbitYaw = transform.eulerAngles.y;
                pitch = Mathf.Clamp(pitch + 12f, -35f, 70f);
            }

            var rot = Quaternion.Euler(pitch, _orbitYaw, 0f);
            var want3 = focus - rot * Vector3.forward * dist + Vector3.up * (Forced ? 0.1f : height * 0.3f);
            // 不要穿牆
            var dir = want3 - focus;
            if (Physics.SphereCast(focus, 0.08f, dir.normalized, out var hit, dir.magnitude, ~0, QueryTriggerInteraction.Ignore)
                && !hit.transform.IsChildOf(transform) && !(_rig && hit.transform.IsChildOf(_rig.model)))
                want3 = focus + dir.normalized * Mathf.Max(0.25f, hit.distance - 0.05f);

            if (_snap) { cam.position = want3; _snap = false; }
            else cam.position = Vector3.Lerp(cam.position, want3, 1f - Mathf.Exp(-damping * Time.deltaTime));
            cam.rotation = Quaternion.LookRotation(focus - cam.position);
        }
    }
}
