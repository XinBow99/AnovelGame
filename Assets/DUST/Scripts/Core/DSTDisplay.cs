using UnityEngine;

namespace DUST.Core
{
    // 解析度下限 1920×1080：啟動時、或視窗被拉小時，自動拉回 1080p（螢幕放不下就以螢幕能放的最大視窗為準）。
    // 預設是視窗模式，不強制全螢幕；最大化或 Alt+Enter 切換全螢幕。
    public class DSTDisplay : MonoBehaviour
    {
        public const int MinWidth = 1920;
        public const int MinHeight = 1080;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Application.isEditor) return;   // Editor 的 Game 視窗大小不管
            var go = new GameObject("DSTDisplay");
            go.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(go);
            Debug.Log($"[DUST] Display: window {Screen.width}x{Screen.height} {Screen.fullScreenMode}, monitor {Screen.currentResolution.width}x{Screen.currentResolution.height}");
            go.AddComponent<DSTDisplay>().Enforce();
        }

        float _nextCheck;

        void Update()
        {
            // 拖拉視窗時每幀都在變，隔一下再檢查，避免跟使用者搶
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;
            Enforce();
        }

        // 視窗模式要多留標題列 / 邊框的空間
        const int WindowChromeW = 16, WindowChromeH = 80;

        void Enforce()
        {
            var display = Screen.currentResolution;   // 螢幕（桌面）解析度
            var mode = Screen.fullScreenMode;
            bool belowMin = Screen.width < MinWidth || Screen.height < MinHeight;

            if (mode == FullScreenMode.Windowed)
            {
                if (!belowMin) return;
                // 螢幕放不下 1080p 的視窗（例如 1080p 螢幕）→ 下限改成螢幕能放的最大視窗，不強制全螢幕；
                // 目標一定要放得下，不然 Windows 會把視窗縮回去，變成每半秒來回拉扯
                int minW = Mathf.Min(MinWidth, display.width - WindowChromeW);
                int minH = Mathf.Min(MinHeight, display.height - WindowChromeH);
                if (Screen.width < minW || Screen.height < minH)
                    Apply(Mathf.Max(Screen.width, minW), Mathf.Max(Screen.height, minH), mode);
                return;
            }

            // 全螢幕卻低於 1080p（舊設定殘留）→ 改用螢幕原生解析度；螢幕本身小於 1080p 就只能這樣
            if (Screen.width < Mathf.Min(MinWidth, display.width) || Screen.height < Mathf.Min(MinHeight, display.height))
                Apply(display.width, display.height, mode);
        }

        static void Apply(int w, int h, FullScreenMode mode)
        {
            Debug.Log($"[DUST] Display: {Screen.width}x{Screen.height} {Screen.fullScreenMode} → {w}x{h} {mode}");
            Screen.SetResolution(w, h, mode);
        }
    }
}
