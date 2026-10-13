using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Player
{
    // 自己角色的鏡頭：第一人稱（鏡頭在鬃狼眼睛）／第三人稱（鏡頭在身後）。走路時按 C（手把 Y）切換；
    // 按住 Alt 自由視角（DSTPlayerController 把滑鼠交給這裡，身體不轉）；
    // 上下車動作播放時一律暫時切第三人稱，看得到整個動作，滑鼠可以繞著角色轉。
    public class DSTPlayerView : MonoBehaviour
    {
        public Camera viewCamera;               // 掛在 Head（第一人稱的位置）底下
        public float distance = 1.35f;
        public float height = 0.45f;
        public float lookHeight = 0.32f;
        public float actionDistance = 1.7f;     // 上下車時拉遠一點
        public float damping = 10f;
        public float freeLookReturn = 8f;       // 放開 Alt 後鏡頭轉回來的速度

        public bool ThirdPerson { get; private set; }
        public bool Forced => _forceTarget != null;

        DSTPlayerController _pc;
        DSTWolfRig _rig;
        Transform _forceTarget;
        float _orbitYaw;
        bool _snap = true;
        Vector3 _fpLocalPos;
        // 自由視角（Alt）與上下車時滑鼠繞的角度，都是加在原本鏡頭方向上的偏移
        float _freeYaw, _freePitch;
        bool _freeHeld;
        float _forceYaw, _forcePitch;
        readonly RaycastHit[] _hits = new RaycastHit[16];

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
            if (_forceTarget == null) { _snap = true; _forceYaw = 0f; _forcePitch = 0f; }
            _forceTarget = target;
            _forceVehicle = vehicle;
            if (target) _orbitYaw = ActionYaw();
        }
        Transform _forceVehicle;

        /// <summary>走路時按住 Alt：滑鼠只轉鏡頭（由 DSTPlayerController 每幀呼叫）</summary>
        public void FreeLook(bool held, Vector2 look)
        {
            _freeHeld = held;
            if (!held) return;
            _freeYaw = Mathf.Clamp(_freeYaw + look.x, -150f, 150f);
            _freePitch = Mathf.Clamp(_freePitch - look.y, -60f, 60f);
        }

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
            if ((Keyboard.current != null && !DUST.UI.DSTChat.ConsumesKeys && Keyboard.current.cKey.wasPressedThisFrame) ||
                (Gamepad.current != null && Gamepad.current.buttonNorth.wasPressedThisFrame))
                ThirdPerson = !ThirdPerson;
#else
            if (Input.GetKeyDown(KeyCode.C)) ThirdPerson = !ThirdPerson;
#endif
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            // 沒按住 Alt（或角色不吃輸入，例如上下車中）：自由視角慢慢轉回正前方
            if (!_freeHeld || !_pc || !_pc.enabled)
            {
                _freeHeld = false;
                float k = 1f - Mathf.Exp(-freeLookReturn * dt);
                _freeYaw = Mathf.Lerp(_freeYaw, 0f, k);
                _freePitch = Mathf.Lerp(_freePitch, 0f, k);
            }

            if (!viewCamera || !viewCamera.enabled) return;
            var cam = viewCamera.transform;
            bool third = Forced || ThirdPerson;
            if (_rig) _rig.SetShadowOnly(!third);
            if (!third)
            {
                cam.localPosition = _fpLocalPos;
                cam.localRotation = Quaternion.Euler(_freePitch, _freeYaw, 0f);
                return;
            }

            Vector3 focus;
            float pitch = _pc ? _pc.Pitch : 15f, dist = distance, yaw;
            if (Forced)
            {
                focus = _forceTarget.position + Vector3.up * 0.12f;
                dist = actionDistance;
                // 動作時鏡頭慢慢繞到角色側後方；滑鼠可以再額外繞著看
                _orbitYaw = Mathf.LerpAngle(_orbitYaw, ActionYaw(), 1f - Mathf.Exp(-1.2f * dt));
                var look = DSTPlayerController.ReadLookInput(_pc ? _pc.mouseSensitivity : 0.1f, _pc ? _pc.gamepadLookSpeed : 160f);
                _forceYaw += look.x;
                _forcePitch = Mathf.Clamp(_forcePitch - look.y, -30f, 50f);
                yaw = _orbitYaw + _forceYaw;
                pitch = 18f + _forcePitch;
            }
            else
            {
                focus = transform.position + Vector3.up * lookHeight;
                yaw = transform.eulerAngles.y + _freeYaw;
                pitch = Mathf.Clamp(pitch + 12f + _freePitch, -35f, 70f);
            }

            var rot = Quaternion.Euler(pitch, yaw, 0f);
            var want3 = focus - rot * Vector3.forward * dist + Vector3.up * (Forced ? 0.1f : height * 0.3f);
            // 不要穿牆
            var dir = want3 - focus;
            if (CastBlocked(focus, dir, out float hitDist))
                want3 = focus + dir.normalized * Mathf.Max(0.25f, hitDist - 0.05f);

            if (_snap) { cam.position = want3; _snap = false; }
            else cam.position = Vector3.Lerp(cam.position, want3, 1f - Mathf.Exp(-damping * dt));
            cam.rotation = Quaternion.LookRotation(focus - cam.position);
        }

        // 只讓牆、地形、建築擋鏡頭：有剛體的東西（腳踏車、車子、箱子）不算，
        // 不然在 MeBike 站上下車時鏡頭會被旁邊停的車擠到鬃狼身上，看起來整隻變超大
        bool CastBlocked(Vector3 from, Vector3 dir, out float hitDist)
        {
            hitDist = float.MaxValue;
            int n = Physics.SphereCastNonAlloc(from, 0.08f, dir.normalized, _hits, dir.magnitude, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.distance <= 0f) continue;                       // 一開始就重疊（focus 在車子裡）
                if (h.collider.attachedRigidbody) continue;
                if (h.transform.IsChildOf(transform) || (_rig && h.transform.IsChildOf(_rig.model))) continue;
                if (h.distance < hitDist) hitDist = h.distance;
            }
            return hitDist < float.MaxValue;
        }
    }
}
