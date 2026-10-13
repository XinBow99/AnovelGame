using System.Collections.Generic;
using DUST.Net;
using DUST.Player;
using DUST.World;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace DUST.UI
{
    // 遊戲內聊天 + 指令列：T / Enter 開始打字、/ 直接打指令（/help 看全部）。Enter 送出、Esc 取消、↑↓ 叫出之前打過的。
    // 打字時 DSTChat.Typing = true，走路、開車、地圖等按鍵都會先看這個，不會邊打字邊亂跑。
    // 不用擺在場景裡：遊戲一開始自己建立（DontDestroyOnLoad）。
    public class DSTChat : MonoBehaviour
    {
        public static bool Typing { get; private set; }
        static int _closedFrame = -1;
        /// <summary>這一幀的按鍵是給聊天框的（打字中，或是剛用 Enter / Esc 關掉）</summary>
        public static bool ConsumesKeys => Typing || _closedFrame == Time.frameCount;

        struct Line { public string who; public Color color; public string text; public float time; }
        static readonly List<Line> _lines = new List<Line>();
        static readonly List<string> _history = new List<string>();
        const int MaxLines = 60, ShowLines = 8;
        const float FadeAfter = 10f;
        const string InputName = "DSTChatInput";

        static DSTChat _instance;
        string _input = "";
        int _historyIndex;
        bool _focusPending, _moveCaretToEnd;
        int _openFrame;
        char _swallow;                 // 開聊天的那個鍵（t、/）也會送一個字元進文字框，要吃掉
        bool _composing;               // 中文輸入法還在選字：這時的 Enter 是確認選字，不是送出
        GUIStyle _style, _field;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            _lines.Clear();
            _history.Clear();
            Typing = false;
            if (_instance) return;
            var go = new GameObject("DSTChat");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DSTChat>();
        }

        public static void Receive(string who, Color color, string text) => Add(who, color, text);
        public static void System(string text) => Add(null, new Color(1f, 0.82f, 0.35f), text);
        public static void Clear() => _lines.Clear();

        static void Add(string who, Color color, string text)
        {
            _lines.Add(new Line { who = who, color = color, text = text, time = Time.unscaledTime });
            if (_lines.Count > MaxLines) _lines.RemoveAt(0);
        }

#if ENABLE_INPUT_SYSTEM
        void OnEnable() { if (Keyboard.current != null) Keyboard.current.onIMECompositionChange += OnComposition; }
        void OnDisable() { if (Keyboard.current != null) Keyboard.current.onIMECompositionChange -= OnComposition; }
        void OnComposition(IMECompositionString s) => _composing = s.Count > 0;
#endif

        void Update()
        {
            if (DSTNetPlayer.Local == null) { if (Typing) Close(); return; }
            if (Typing || DSTMap.IsOpen || DSTNetSession.Paused) return;
            if (OpenPressed(out char key)) Open(key);
        }

        static bool OpenPressed(out char key)
        {
            key = '\0';
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return false;
            if (kb.slashKey.wasPressedThisFrame || kb.numpadDivideKey.wasPressedThisFrame) { key = '/'; return true; }
            if (kb.tKey.wasPressedThisFrame) { key = 't'; return true; }
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) { key = '\n'; return true; }
#endif
            return false;
        }

        void Open(char key)
        {
            Typing = true;
            _input = key == '/' ? "/" : "";
            _swallow = key;
            _openFrame = Time.frameCount;
            _focusPending = true;
            _moveCaretToEnd = true;
            _historyIndex = _history.Count;
            _composing = false;
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.SetIMEEnabled(true);
#endif
        }

        void Close()
        {
            Typing = false;
            _closedFrame = Time.frameCount;
            _input = "";
            GUIUtility.keyboardControl = 0;
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.SetIMEEnabled(false);
#endif
        }

        void Submit()
        {
            string text = _input.Trim();
            Close();
            if (text.Length == 0) return;
            if (_history.Count == 0 || _history[_history.Count - 1] != text) _history.Add(text);
            if (text.StartsWith("/")) DSTCommands.Run(text);
            else if (DSTNetPlayer.Local) DSTNetPlayer.Local.SendChat(text);
        }

        // ------------------------------------------------------------------ 畫面
        void Styles()
        {
            if (_style != null) return;
            _style = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = false, richText = false };
            _style.normal.textColor = Color.white;
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 18 };
        }

        void OnGUI()
        {
            if (DSTNetPlayer.Local == null) return;
            Styles();
            DrawBadge();
            DrawLines();
            if (!Typing) return;

            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                // 開聊天的那個鍵產生的字元
                if (_swallow != '\0' && Time.frameCount - _openFrame <= 1 && char.ToLowerInvariant(e.character) == _swallow) { e.Use(); _swallow = '\0'; return; }
                if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && !_composing)
                {
                    if (Time.frameCount != _openFrame) Submit();
                    e.Use();
                    return;
                }
                if (e.keyCode == KeyCode.Escape) { Close(); e.Use(); return; }
                if (e.keyCode == KeyCode.UpArrow || e.keyCode == KeyCode.DownArrow)
                {
                    if (_history.Count > 0)
                    {
                        _historyIndex = Mathf.Clamp(_historyIndex + (e.keyCode == KeyCode.UpArrow ? -1 : 1), 0, _history.Count);
                        _input = _historyIndex < _history.Count ? _history[_historyIndex] : "";
                        _moveCaretToEnd = true;
                    }
                    e.Use();
                    return;
                }
            }

            float w = Mathf.Min(720f, Screen.width - 40f);
            var r = new Rect(20f, Screen.height - 200f, w, 32f);
            GUI.SetNextControlName(InputName);
            _input = GUI.TextField(r, _input, DSTNetPlayer.MaxChatLength, _field);
            if (_focusPending) { GUI.FocusControl(InputName); _focusPending = false; }
            else if (_moveCaretToEnd && GUI.GetNameOfFocusedControl() == InputName)
            {
                var te = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
                te.MoveTextEnd();
                _moveCaretToEnd = false;
            }
            // 打字時游標位置給輸入法候選字窗用
            var imePos = GUIUtility.GUIToScreenPoint(new Vector2(r.x + 8f, r.yMax));
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.SetIMECursorPosition(imePos);
#endif
        }

        void DrawLines()
        {
            float now = Time.unscaledTime;
            float y = Screen.height - 210f;   // 在左下角地點提示（DSTNotice）上面
            int shown = 0;
            for (int i = _lines.Count - 1; i >= 0 && shown < (Typing ? ShowLines * 2 : ShowLines); i--)
            {
                var line = _lines[i];
                float age = now - line.time;
                float alpha = Typing ? 1f : Mathf.Clamp01(FadeAfter - age);
                if (alpha <= 0f) break;
                y -= 24f;
                shown++;
                float x = 22f;
                if (line.who != null)
                {
                    string who = line.who + "：";
                    x = Label(x, y, who, line.color, alpha);
                    Label(x, y, line.text, Color.white, alpha);
                }
                else Label(x, y, line.text, line.color, alpha);
            }
        }

        float Label(float x, float y, string text, Color color, float alpha)
        {
            var size = _style.CalcSize(new GUIContent(text));
            var r = new Rect(x, y, size.x, 24f);
            var c = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.65f * alpha);
            GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, _style);
            GUI.color = new Color(color.r, color.g, color.b, alpha);
            GUI.Label(r, text, _style);
            GUI.color = c;
            return x + size.x;
        }

        // 左上角：目前開著的作弊狀態
        void DrawBadge()
        {
            var pc = DSTNetPlayer.Local.Controller;
            if (!pc) return;
            var parts = new List<string>();
            if (pc.Creative) parts.Add("創造模式");
            if (pc.Flying) parts.Add(pc.NoClip ? "飛行（穿牆）" : "飛行");
            if (!Mathf.Approximately(pc.SpeedMultiplier, 1f)) parts.Add($"速度 ×{pc.SpeedMultiplier:0.##}");
            if (parts.Count == 0) return;
            Label(20f, 16f, string.Join("　", parts), new Color(0.55f, 0.9f, 1f), 1f);
        }
    }
}
