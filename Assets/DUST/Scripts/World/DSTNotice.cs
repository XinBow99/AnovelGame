using DUST.Player;
using UnityEngine;

namespace DUST.World
{
    // 左下角的提示（「你來到了…」、MeBike 租借 / 還車）：從左邊滑進來，幾秒後淡出。新的會蓋掉舊的。
    public class DSTNotice : MonoBehaviour
    {
        const float Seconds = 5f;

        static DSTNotice _instance;
        string _title, _sub;
        float _shownAt = -100f;
        GUIStyle _titleStyle, _subStyle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            var go = new GameObject("DSTNotice");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DSTNotice>();
        }

        public static void Show(string sub, string title)
        {
            if (!_instance) return;
            _instance._sub = sub;
            _instance._title = title;
            _instance._shownAt = Time.time;
        }

        public static void Clear()
        {
            if (_instance) _instance._title = null;
        }

        void OnGUI()
        {
            if (_title == null || DSTNetPlayer.Local == null) return;
            float age = Time.time - _shownAt;
            if (age > Seconds) return;
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
                _subStyle = new GUIStyle(_titleStyle) { fontSize = 16, fontStyle = FontStyle.Normal };
            }
            float a = Mathf.Clamp01(age / 0.3f) * Mathf.Clamp01((Seconds - age) / 1f);
            float slide = (1f - Mathf.Clamp01(age / 0.3f)) * -40f;
            float w = Mathf.Max(260f, Mathf.Max(_titleStyle.CalcSize(new GUIContent(_title)).x, _subStyle.CalcSize(new GUIContent(_sub)).x) + 48f);
            var box = new Rect(24 + slide, Screen.height - 150, w, 70);
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f * a);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = new Color(0.95f, 0.72f, 0.38f, a);
            GUI.DrawTexture(new Rect(box.x, box.y, 4, box.height), Texture2D.whiteTexture);
            GUI.color = old;
            _subStyle.normal.textColor = new Color(0.9f, 0.86f, 0.78f, a);
            GUI.Label(new Rect(box.x + 18, box.y + 6, box.width, 22), _sub, _subStyle);
            _titleStyle.normal.textColor = new Color(1f, 0.93f, 0.78f, a);
            GUI.Label(new Rect(box.x + 18, box.y + 28, box.width, 36), _title, _titleStyle);
        }
    }
}
