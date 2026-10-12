using UnityEngine;

namespace DUST.Core
{
    // 解析度下限 1920×1080：啟動時、或視窗被拉小時，自動拉回 1080p（螢幕本身比較小就以螢幕為上限）。
    // 全螢幕用螢幕原生解析度；Alt+Enter 切換全螢幕 / 視窗。
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
                bool fits = display.width >= MinWidth + WindowChromeW && display.height >= MinHeight + WindowChromeH;
                if (fits)
                    Apply(Mathf.Max(Screen.width, MinWidth), Mathf.Max(Screen.height, MinHeight), mode);
                else
                    // 螢幕放不下 1080p 的視窗（例如 1080p 螢幕）→ 直接全螢幕，不然 Windows 會一直把視窗縮小
                    Apply(display.width, display.height, FullScreenMode.FullScreenWindow);
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
