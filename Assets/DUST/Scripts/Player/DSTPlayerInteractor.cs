using DUST.Vehicles;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Player
{
    // 看著車（或就站在車旁）時按 F（手把 X）上車；左鍵（手把 RT）揮拳
    public class DSTPlayerInteractor : MonoBehaviour
    {
        public Camera viewCamera;
        public float reach = 3.0f;
        public float proximity = 1.6f;

        [Header("Punch")]
        public float punchReach = 1.2f;
        public float punchDamage = 2f;
        public float punchCooldown = 0.45f;

        DSTVehicleEntry _target;
        DSTPlayerController _player;
        DSTNetPlayer _net;
        float _nextPunch;
        float _hitMarkerUntil;

        void Awake()
        {
            _player = GetComponent<DSTPlayerController>();
            _net = GetComponent<DSTNetPlayer>();
        }

        void Update()
        {
            _target = FindTarget();
            if (_target != null && InteractPressed())
            {
                ConsumeInteract();
                _target.Enter(_player);
            }
            if (AttackPressed() && Time.time >= _nextPunch)
                Punch();
        }

        static bool AttackPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return (Mouse.current != null && Cursor.lockState == CursorLockMode.Locked && Mouse.current.leftButton.wasPressedThisFrame) ||
                   (Gamepad.current != null && Gamepad.current.rightTrigger.wasPressedThisFrame);
#else
            return Input.GetMouseButtonDown(0);
#endif
        }

        void Punch()
        {
            _nextPunch = Time.time + punchCooldown;
            if (!viewCamera) return;
            var ray = new Ray(viewCamera.transform.position, viewCamera.transform.forward);
            // 半徑小小的 SphereCast，比單純射線好打中細長的動物
            if (Physics.SphereCast(ray, 0.08f, out var hit, punchReach, ~0, QueryTriggerInteraction.Ignore))
            {
                var d = hit.collider.GetComponentInParent<DUST.Core.IDSTDamageable>();
                if (d != null && d.IsAlive)
                {
                    if (_net) _net.Punch(d, punchDamage);
                    else d.TakeDamage(punchDamage, transform);
                    _hitMarkerUntil = Time.time + 0.15f;
                }
            }
        }

        // 同一次按鍵只處理一次：下車那一幀角色的互動剛被打開，不能又被拿去上車（原本那台或旁邊那台）
        static int _consumedFrame = -1;
        public static void ConsumeInteract() => _consumedFrame = Time.frameCount;

        public static bool InteractPressed()
        {
            if (_consumedFrame == Time.frameCount) return false;
#if ENABLE_INPUT_SYSTEM
            return (Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame) ||
                   (Gamepad.current != null && Gamepad.current.buttonWest.wasPressedThisFrame);
#else
            return Input.GetKeyDown(KeyCode.F);
#endif
        }

        DSTVehicleEntry FindTarget()
        {
            if (viewCamera)
            {
                var ray = new Ray(viewCamera.transform.position, viewCamera.transform.forward);
                if (Physics.Raycast(ray, out var hit, reach, ~0, QueryTriggerInteraction.Ignore))
                {
                    var e = hit.collider.GetComponentInParent<DSTVehicleEntry>();
                    if (e != null && e.CanEnter) return e;
                }
            }
            // 沒對準也可以：貼在車旁時
            DSTVehicleEntry best = null;
            float bestD = proximity;
            foreach (var c in Physics.OverlapSphere(transform.position + Vector3.up * 0.9f, proximity, ~0, QueryTriggerInteraction.Ignore))
            {
                var e = c.GetComponentInParent<DSTVehicleEntry>();
                if (e == null || !e.CanEnter) continue;
                float d = Vector3.Distance(transform.position, c.ClosestPoint(transform.position));
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        void OnGUI()
        {
            DSTHud.Crosshair(Time.time < _hitMarkerUntil);
            if (_target == null) return;
            DSTHud.CenterPrompt("[F] 上車");
        }
    }

    // 極簡提示文字（之後換成正式 UI）
    public static class DSTHud
    {
        static GUIStyle _style;

        static GUIStyle Style()
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 22, fontStyle = FontStyle.Bold };
                _style.normal.textColor = Color.white;
            }
            return _style;
        }

        public static void Crosshair(bool hit)
        {
            float s = hit ? 7f : 4f;
            var r = new Rect(Screen.width * 0.5f - s * 0.5f, Screen.height * 0.5f - s * 0.5f, s, s);
            var c = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), Texture2D.whiteTexture);
            GUI.color = hit ? new Color(1f, 0.35f, 0.25f) : Color.white;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = c;
        }

        public static void CenterPrompt(string text) => Draw(new Rect(0, Screen.height * 0.5f + 40, Screen.width, 40), text);
        public static void BottomHint(string text) => Draw(new Rect(0, Screen.height - 60, Screen.width, 40), text);

        static void Draw(Rect r, string text)
        {
            var s = Style();
            var shadow = new Rect(r.x + 2, r.y + 2, r.width, r.height);
            var c = s.normal.textColor;
            s.normal.textColor = new Color(0, 0, 0, 0.7f);
            GUI.Label(shadow, text, s);
            s.normal.textColor = c;
            GUI.Label(r, text, s);
        }
    }
}
