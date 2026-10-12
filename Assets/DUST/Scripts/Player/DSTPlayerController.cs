using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Player
{
    // 第一人稱移動：WASD 移動、Shift 跑、Space 跳、Ctrl 蹲、滑鼠視角。手把：左搖桿移動、右搖桿視角、A 跳、B 蹲、L3 跑。
    // Esc 放開滑鼠游標，點一下畫面再鎖回來。
    [RequireComponent(typeof(CharacterController))]
    public class DSTPlayerController : MonoBehaviour
    {
        public Transform head;

        [Header("Move")]
        public float walkSpeed = 4.2f;
        public float sprintSpeed = 6.8f;
        public float crouchSpeed = 2.2f;
        public float groundAcceleration = 14f;
        public float airAcceleration = 3f;
        public float jumpHeight = 1.0f;
        public float gravity = -19.6f;

        [Header("Look")]
        public float mouseSensitivity = 0.1f;     // 度 / 像素
        public float gamepadLookSpeed = 160f;     // 度 / 秒
        public float minPitch = -85f;
        public float maxPitch = 85f;

        [Header("Body")]
        public float standHeight = 1.8f;
        public float crouchHeight = 1.15f;
        public float eyeBelowTop = 0.12f;

        public bool IsGrounded => _cc && _cc.isGrounded;
        public bool IsCrouching => _crouching;
        public bool IsSprinting { get; private set; }
        public float Pitch => _pitch;

        CharacterController _cc;
        Vector3 _velocity;
        float _pitch;
        bool _crouching;
        float _currentHeight;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _currentHeight = standHeight;
        }

        // 滑鼠鎖定 / Esc 暫停由 DSTNetSession 統一管理（上車時這個元件會關掉，不能在這裡放開滑鼠）
        void OnEnable() => _velocity = Vector3.zero;

        void Update()
        {
            Vector2 move = Vector2.zero, look = Vector2.zero;
            bool sprint = false, jump = false, crouchHeld = false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed) move.y += 1;
                if (kb.sKey.isPressed) move.y -= 1;
                if (kb.dKey.isPressed) move.x += 1;
                if (kb.aKey.isPressed) move.x -= 1;
                sprint = kb.leftShiftKey.isPressed;
                jump = kb.spaceKey.wasPressedThisFrame;
                crouchHeld = kb.leftCtrlKey.isPressed || kb.cKey.isPressed;
            }
            if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
                look += mouse.delta.ReadValue() * mouseSensitivity;
            var gp = Gamepad.current;
            if (gp != null)
            {
                move += gp.leftStick.ReadValue();
                look += gp.rightStick.ReadValue() * gamepadLookSpeed * Time.deltaTime;
                sprint |= gp.leftStickButton.isPressed;
                jump |= gp.buttonSouth.wasPressedThisFrame;
                crouchHeld |= gp.buttonEast.isPressed;
            }
#else
            move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            look = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 2f;
            sprint = Input.GetKey(KeyCode.LeftShift);
            jump = Input.GetKeyDown(KeyCode.Space);
            crouchHeld = Input.GetKey(KeyCode.LeftControl);
#endif
            Look(look);
            Simulate(Vector2.ClampMagnitude(move, 1f), sprint, jump, crouchHeld, Time.deltaTime);
        }

        public void Look(Vector2 delta)
        {
            transform.Rotate(0f, delta.x, 0f, Space.Self);
            _pitch = Mathf.Clamp(_pitch - delta.y, minPitch, maxPitch);
            if (head) head.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        // 公開出來給測試或過場直接驅動
        public void Simulate(Vector2 move, bool sprint, bool jump, bool crouchHeld, float dt)
        {
            UpdateCrouch(crouchHeld, dt);

            IsSprinting = !_crouching && sprint && move.y > 0.1f;
            float speed = _crouching ? crouchSpeed : (IsSprinting ? sprintSpeed : walkSpeed);
            Vector3 wish = (transform.right * move.x + transform.forward * move.y) * speed;
            Vector3 horiz = new Vector3(_velocity.x, 0f, _velocity.z);
            float accel = _cc.isGrounded ? groundAcceleration : airAcceleration;
            horiz = Vector3.MoveTowards(horiz, wish, accel * speed * dt);
            _velocity.x = horiz.x;
            _velocity.z = horiz.z;

            if (_cc.isGrounded)
            {
                if (_velocity.y < 0f) _velocity.y = -2f;          // 貼地，下坡不會彈起來
                if (jump && !_crouching) _velocity.y = Mathf.Sqrt(2f * jumpHeight * -gravity);
            }
            _velocity.y += gravity * dt;

            var flags = _cc.Move(_velocity * dt);
            if ((flags & CollisionFlags.Above) != 0 && _velocity.y > 0f) _velocity.y = 0f;
        }

        void UpdateCrouch(bool held, float dt)
        {
            if (held) _crouching = true;
            else if (_crouching)
            {
                // 頭上有東西就不能站起來
                float r = _cc.radius;
                Vector3 bottom = transform.position + Vector3.up * (r + 0.05f);
                Vector3 top = transform.position + Vector3.up * (standHeight - r);
                if (!Physics.CheckCapsule(bottom, top, r * 0.95f, ~0, QueryTriggerInteraction.Ignore) || !HitsOnlySelf(bottom, top, r * 0.95f))
                    _crouching = false;
            }
            float target = _crouching ? crouchHeight : standHeight;
            _currentHeight = Mathf.MoveTowards(_currentHeight, target, 6f * dt);
            _cc.height = _currentHeight;
            _cc.center = Vector3.up * (_currentHeight * 0.5f);
            if (head) head.localPosition = Vector3.up * (_currentHeight - eyeBelowTop);
        }

        bool HitsOnlySelf(Vector3 a, Vector3 b, float r)
        {
            foreach (var c in Physics.OverlapCapsule(a, b, r, ~0, QueryTriggerInteraction.Ignore))
                if (c.transform != transform && !c.transform.IsChildOf(transform)) return false;
            return true;
        }

        public void Teleport(Vector3 position, float yaw)
        {
            _cc.enabled = false;
            transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            _cc.enabled = true;
            _velocity = Vector3.zero;
            _pitch = 0f;
            if (head) head.localRotation = Quaternion.identity;
        }
    }
}
