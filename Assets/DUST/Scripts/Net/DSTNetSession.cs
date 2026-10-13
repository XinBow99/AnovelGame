using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using DUST.World;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DUST.Net
{
    // 連線選單：開房（拿到 6 碼邀請碼）、輸入邀請碼加入、單人遊玩。
    // 走 Unity Relay，不用開 port；開房的人就是主機（Host）。遊戲中按 Esc 可以看邀請碼 / 離開。
    [RequireComponent(typeof(NetworkManager))]
    public class DSTNetSession : MonoBehaviour
    {
        public int maxPlayers = 4;
        public Camera lobbyCamera;          // 還沒進場時用的鏡頭

        static DSTNetSession _instance;

        NetworkManager _nm;
        ISession _session;
        string _codeInput = "";
        string _seedInput = "";
        int _worldChoice;                   // 0 = 隨機世界、1 = 遺跡二（開房的人決定）
        string _status = "";
        bool _busy;
        bool _usedRelay;
        GUIStyle _title, _label, _labelLeft, _button, _field;

        void Awake()
        {
            _instance = this;
            _nm = GetComponent<NetworkManager>();
        }

        void Start()
        {
            _nm.OnClientDisconnectCallback += OnClientDisconnect;
            SetLobbyCamera(true);
        }

        void OnDestroy()
        {
            if (_nm) _nm.OnClientDisconnectCallback -= OnClientDisconnect;
            if (_instance == this) _instance = null;
        }

        public static void SetLobbyCamera(bool on)
        {
            if (_instance && _instance.lobbyCamera) _instance.lobbyCamera.enabled = on;
        }

        bool InGame => _nm && _nm.IsListening;
        string Code => _session?.Code;

        // ------------------------------------------------------------------ 滑鼠 / 暫停
        // 走路、開車都一樣：進場鎖滑鼠，Esc 放開（顯示暫停面板），點面板以外的地方回到遊戲
        bool _playing;

        public static bool Paused => Cursor.lockState != CursorLockMode.Locked;

        static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        void Update()
        {
            bool playing = InGame && DUST.Player.DSTNetPlayer.Local != null;
            if (playing != _playing)
            {
                _playing = playing;
                LockCursor(playing);
            }
            // 地圖開著時 Esc 是關地圖（DSTMap 自己處理），不要順便打開暫停選單
            if (playing && EscPressed() && !DSTMap.IsOpen && DSTMap.ClosedFrame != Time.frameCount) LockCursor(false);
        }

        static bool EscPressed()
        {
            if (DUST.UI.DSTChat.ConsumesKeys) return false;   // 聊天框在用鍵盤
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Escape);
#endif
        }

        // ------------------------------------------------------------------ 連線
        static async Task SignIn()
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                var options = new InitializationOptions();
                // 每次啟動用不同的匿名身分，同一台電腦開兩個視窗測試（Multiplayer Play Mode）才不會撞號
                options.SetProfile("p" + UnityEngine.Random.Range(0, 100000000).ToString("D8"));
                await UnityServices.InitializeAsync(options);
            }
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        async void Host()
        {
            await Run("建立房間中…", async () =>
            {
                await SignIn();
                ApplyWorldChoice();
                var options = new SessionOptions { MaxPlayers = maxPlayers, IsPrivate = true }.WithRelayNetwork();
                _usedRelay = true;
                _session = await MultiplayerService.Instance.CreateSessionAsync(options);
            });
        }

        async void Join(string code)
        {
            code = code.Trim().ToUpperInvariant();
            if (code.Length == 0) { _status = "請輸入邀請碼"; return; }
            await Run("加入中…", async () =>
            {
                await SignIn();
                _usedRelay = true;
                _session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
            });
        }

        void Solo()
        {
            // 用過 Relay 之後 Transport 還記著 Relay 的資料，換一個乾淨的本機 Transport
            if (_usedRelay)
            {
                var old = GetComponent<UnityTransport>();
                var fresh = gameObject.AddComponent<UnityTransport>();
                _nm.NetworkConfig.NetworkTransport = fresh;
                if (old) Destroy(old);
                _usedRelay = false;
            }
            GetComponent<UnityTransport>().SetConnectionData("127.0.0.1", 7777, "127.0.0.1");
            ApplyWorldChoice();
            if (!_nm.StartHost()) _status = "無法啟動單人模式";
        }

        void ApplyWorldChoice()
        {
            DSTWorld.RequestRandom = _worldChoice == 0;
            DSTWorld.RequestSeed = DSTWorldGen.ParseSeed(_seedInput);
        }

        async void Leave()
        {
            await Run("離開中…", LeaveInternal);
            _status = "";
        }

        async Task LeaveInternal()
        {
            var s = _session;
            _session = null;
            if (s != null)
            {
                try { await s.LeaveAsync(); }
                catch (Exception e) { Debug.LogWarning("[DUST] Leave session: " + e.Message); }
            }
            if (_nm.IsListening) _nm.Shutdown();
        }

        async void OnClientDisconnect(ulong clientId)
        {
            // 客戶端自己被斷線（房主關掉、網路掉了）→ 回到選單
            if (_nm.IsServer || clientId != _nm.LocalClientId) return;
            await LeaveInternal();
            _status = "與房主的連線中斷了";
        }

        async Task Run(string message, Func<Task> action)
        {
            if (_busy) return;
            _busy = true;
            _status = message;
            try
            {
                await action();
                _status = "";
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _status = "失敗：" + e.Message;
                if (_nm.IsListening) _nm.Shutdown();
                _session = null;
            }
            finally { _busy = false; }
        }

        // ------------------------------------------------------------------ 介面
        void Styles()
        {
            if (_title != null) return;
            _title = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _labelLeft = new GUIStyle(_label) { alignment = TextAnchor.MiddleLeft };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 20 };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 24, alignment = TextAnchor.MiddleCenter };
        }

        void OnGUI()
        {
            Styles();
            if (!InGame) { MainMenu(); return; }

            // 主機左上角常駐邀請碼
            if (_nm.IsHost && !string.IsNullOrEmpty(Code))
                GUI.Label(new Rect(12, 8, 600, 28), $"邀請碼  {Code}    玩家 {_nm.ConnectedClientsIds.Count}/{maxPlayers}", _labelLeft);

            // Esc 放開滑鼠時顯示暫停面板
            if (_playing && Paused && !DSTMap.IsOpen) PauseMenu();
        }

        void MainMenu()
        {
            var r = Centered(420, 540);
            GUI.Box(r, GUIContent.none);
            GUILayout.BeginArea(new Rect(r.x + 24, r.y + 20, r.width - 48, r.height - 40));
            GUILayout.Label("DUST", _title, GUILayout.Height(48));
            GUILayout.Space(10);
            GUI.enabled = !_busy;
            _worldChoice = GUILayout.Toolbar(_worldChoice, WorldNames, _button, GUILayout.Height(38));
            if (_worldChoice == 0)
            {
                GUILayout.Label("種子碼（空白 = 隨機）", _label);
                _seedInput = GUILayout.TextField(_seedInput, 24, _field, GUILayout.Height(38));
            }
            else GUILayout.Space(62);
            GUILayout.Space(8);
            if (GUILayout.Button("開房（邀請朋友）", _button, GUILayout.Height(48))) Host();
            GUILayout.Space(16);
            GUILayout.Label("朋友的邀請碼", _label);
            _codeInput = GUILayout.TextField(_codeInput, 12, _field, GUILayout.Height(42));
            if (GUILayout.Button("加入", _button, GUILayout.Height(44))) Join(_codeInput);
            GUILayout.Space(16);
            if (GUILayout.Button("單人遊玩", _button, GUILayout.Height(40))) Solo();
            GUI.enabled = true;
            GUILayout.Space(8);
            GUILayout.Label(_status, _label);
            GUILayout.EndArea();
        }

        void PauseMenu()
        {
            var world = DSTWorld.Instance;
            bool showSeed = world && world.IsRandom;
            var r = Centered(380, showSeed ? 300 : 220);
            // 點面板外面才回到遊戲；面板裡的按鈕要等放開滑鼠才算按下，不能在按下時就把面板關掉
            var e = Event.current;
            if (e.type == EventType.MouseDown && !r.Contains(e.mousePosition))
            {
                LockCursor(true);
                e.Use();
                return;
            }
            GUI.Box(r, GUIContent.none);
            GUILayout.BeginArea(new Rect(r.x + 24, r.y + 16, r.width - 48, r.height - 32));
            if (!string.IsNullOrEmpty(Code))
            {
                GUILayout.Label("邀請碼：" + Code, _title, GUILayout.Height(44));
                if (GUILayout.Button("複製邀請碼", _button, GUILayout.Height(40))) GUIUtility.systemCopyBuffer = Code;
            }
            else
                GUILayout.Label(_nm.IsHost ? "單人模式" : "連線中", _title, GUILayout.Height(44));
            if (showSeed)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("種子碼  " + world.Seed, _labelLeft, GUILayout.Height(36));
                if (GUILayout.Button("複製", _button, GUILayout.Width(80), GUILayout.Height(36))) GUIUtility.systemCopyBuffer = world.Seed.ToString();
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8);
            GUI.enabled = !_busy;
            if (GUILayout.Button(_nm.IsHost && _session != null ? "關閉房間" : "離開", _button, GUILayout.Height(40))) Leave();
            GUI.enabled = true;
            GUILayout.Label("點一下畫面回到遊戲", _label);
            GUILayout.EndArea();
        }

        static readonly string[] WorldNames = { "隨機世界", "遺跡二" };

        static Rect Centered(float w, float h) => new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
    }
}
