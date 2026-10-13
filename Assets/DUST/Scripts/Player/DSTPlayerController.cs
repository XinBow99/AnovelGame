using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Player
{
    // 鬃狼的移動：WASD 移動、Shift 跑、Space 跳、Ctrl 蹲、滑鼠視角（C 切換第一 / 第三人稱）。手把：左搖桿移動、右搖桿視角、A 跳、B 蹲、L3 跑。
    // 按住 Alt（手把 R3）自由視角：只轉鏡頭、身體不跟著轉，放開後鏡頭轉回來。
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
        public float headForward = 0f;           // 四足：眼睛在身體中心前方

        [Header("Creative")]
        public float flySpeed = 9f;
        public float flySprintMultiplier = 3f;

        public bool IsGrounded => _cc && _cc.isGrounded;
        public bool IsCrouching => _crouching;
        public bool IsSprinting { get; private set; }
        public float Pitch => _pitch;

        // 作弊 / 創造模式（DSTCommands 設定）。創造模式：連按兩下 Space 切換飛行；飛行時 Space 上升、Ctrl 下降。
        public bool Creative { get; set; }
        public bool Flying { get; private set; }
        public bool NoClip { get; private set; }
        public float SpeedMultiplier { get; set; } = 1f;

        CharacterController _cc;
        DSTPlayerView _view;
        Vector3 _velocity;
        float _pitch;
        bool _crouching;
        float _currentHeight;
        float _lastSpaceTap = -1f;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _view = GetComponent<DSTPlayerView>();
            _currentHeight = standHeight;
        }

        /// <summary>這一幀的視角輸入（度）：滑鼠（游標鎖住時）+ 手把右搖桿。追車鏡頭、上下車鏡頭也用這個。</summary>
        public static Vector2 ReadLookInput(float mouseSensitivity = 0.1f, float gamepadLookSpeed = 160f)
        {
            var look = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null && Cursor.lockState == CursorLockMode.Locked)
                look += Mouse.current.delta.ReadValue() * mouseSensitivity;
            if (Gamepad.current != null)
                look += Gamepad.current.rightStick.ReadValue() * gamepadLookSpeed * Time.deltaTime;
#else
            if (Cursor.lockState == CursorLockMode.Locked)
                look = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 2f;
#endif
            return look;
        }

        public static bool FreeLookHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return (Keyboard.current != null && (Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed)) ||
                   (Gamepad.current != null && Gamepad.current.rightStickButton.isPressed);
#else
            return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
#endif
        }

        // 滑鼠鎖定 / Esc 暫停由 DSTNetSession 統一管理（上車時這個元件會關掉，不能在這裡放開滑鼠）
        void OnEnable() => _velocity = Vector3.zero;

        // 上車、上下車動作時元件會關掉：飛行 / 穿牆一律取消（碰撞由 DSTNetPlayer 依是否在車上決定）
        void OnDisable()
        {
            bool wasNoClip = NoClip;
            Flying = false;
            NoClip = false;
            if (wasNoClip && _cc)
            {
                var net = GetComponent<DSTNetPlayer>();
                _cc.enabled = !(net && net.InVehicle);
            }
        }

        void Update()
        {
            Vector2 move = Vector2.zero;
            bool sprint = false, jump = false, crouchHeld = false, upHeld = false;
            bool typing = DUST.UI.DSTChat.Typing;   // 聊天輸入中：鍵盤是給文字框的
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && !typing)
            {
                if (kb.wKey.isPressed) move.y += 1;
                if (kb.sKey.isPressed) move.y -= 1;
                if (kb.dKey.isPressed) move.x += 1;
                if (kb.aKey.isPressed) move.x -= 1;
                sprint = kb.leftShiftKey.isPressed;
                jump = kb.spaceKey.wasPressedThisFrame;
                upHeld = kb.spaceKey.isPressed;
                crouchHeld = kb.leftCtrlKey.isPressed;   // C 是切換第一 / 第三人稱（DSTPlayerView）
            }
            var gp = Gamepad.current;
            if (gp != null)
            {
                move += gp.leftStick.ReadValue();
                sprint |= gp.leftStickButton.isPressed;
                jump |= gp.buttonSouth.wasPressedThisFrame;
                upHeld |= gp.buttonSouth.isPressed;
                crouchHeld |= gp.buttonEast.isPressed;
            }
#else
            if (!typing)
            {
                move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
                sprint = Input.GetKey(KeyCode.LeftShift);
                jump = Input.GetKeyDown(KeyCode.Space);
                upHeld = Input.GetKey(KeyCode.Space);
                crouchHeld = Input.GetKey(KeyCode.LeftControl);
            }
#endif
            var look = ReadLookInput(mouseSensitivity, gamepadLookSpeed);
            bool freeLook = _view && FreeLookHeld();
            if (_view) _view.FreeLook(freeLook, look);
            if (!freeLook) Look(look);

            // 創造模式：連按兩下 Space 切換飛行
            if (jump && Creative)
            {
                if (Time.time - _lastSpaceTap < 0.3f) { SetFlying(!Flying); _lastSpaceTap = -1f; jump = false; }
                else _lastSpaceTap = Time.time;
            }

            if (Flying) Fly(Vector2.ClampMagnitude(move, 1f), sprint, upHeld, crouchHeld, Time.deltaTime);
            else
            {
                // 速度倍率只套在玩家自己的輸入（上車前腳本走過去不受影響）
                _inputSpeedScale = SpeedMultiplier;
                Simulate(Vector2.ClampMagnitude(move, 1f), sprint, jump, crouchHeld, Time.deltaTime);
                _inputSpeedScale = 1f;
            }
        }
        float _inputSpeedScale = 1f;

        public void SetFlying(bool on)
        {
            Flying = on;
            _velocity = Vector3.zero;
            if (!on) SetNoClip(false);
        }

        /// <summary>穿牆：只在飛行時有效（關掉碰撞膠囊，直接移動位置）</summary>
        public void SetNoClip(bool on)
        {
            NoClip = on;
            if (on && !Flying) SetFlying(true);
            if (_cc) _cc.enabled = !on;
        }

        void Fly(Vector2 move, bool sprint, bool up, bool down, float dt)
        {
            IsSprinting = sprint;
            // 往看的方向飛（含仰角），Space / Ctrl 直上直下
            var look = Quaternion.Euler(_pitch, transform.eulerAngles.y, 0f);
            Vector3 wish = look * Vector3.forward * move.y + transform.right * move.x;
            wish += Vector3.up * ((up ? 1f : 0f) - (down ? 1f : 0f));
            wish = Vector3.ClampMagnitude(wish, 1f) * flySpeed * (sprint ? flySprintMultiplier : 1f) * SpeedMultiplier;
            _velocity = Vector3.Lerp(_velocity, wish, 1f - Mathf.Exp(-8f * dt));
            if (NoClip || !_cc.enabled) transform.position += _velocity * dt;
            else _cc.Move(_velocity * dt);
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
            float speed = (_crouching ? crouchSpeed : (IsSprinting ? sprintSpeed : walkSpeed)) * _inputSpeedScale;
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
            if (head) head.localPosition = new Vector3(0f, _currentHeight - eyeBelowTop, headForward);
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
