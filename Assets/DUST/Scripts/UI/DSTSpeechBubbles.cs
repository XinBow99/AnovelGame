using System.Collections.Generic;
using UnityEngine;

namespace DUST.UI
{
    // 頭上的對話泡泡（動物呢喃等）。用 IMGUI 畫，中文不用另外準備字型。
    // 不用擺在場景裡：第一次 Show 時自己建立。
    public class DSTSpeechBubbles : MonoBehaviour
    {
        public const float MaxDistance = 22f;

        sealed class Bubble { public Transform anchor; public float height; public string who, text; public Color color; public float born, life; }

        static DSTSpeechBubbles _instance;
        readonly List<Bubble> _bubbles = new List<Bubble>();
        GUIStyle _text, _name;

        public static void Show(Transform anchor, float height, string who, string text, Color color, float life = 3.5f)
        {
            if (!anchor) return;
            if (!_instance)
            {
                var go = new GameObject("DSTSpeechBubbles");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<DSTSpeechBubbles>();
            }
            // 同一隻動物只留最新的一句
            _instance._bubbles.RemoveAll(b => b.anchor == anchor);
            _instance._bubbles.Add(new Bubble { anchor = anchor, height = height, who = who, text = text, color = color, born = Time.time, life = life });
        }

        static Camera ViewCamera()
        {
            var main = Camera.main;
            if (main && main.isActiveAndEnabled) return main;
            Camera best = null;
            foreach (var c in Camera.allCameras) if (c.isActiveAndEnabled && c.targetTexture == null && (!best || c.depth > best.depth)) best = c;
            return best;
        }

        void OnGUI()
        {
            if (_bubbles.Count == 0) return;
            if (_text == null)
            {
                _text = new GUIStyle(GUI.skin.box) { fontSize = 17, wordWrap = false, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(10, 10, 5, 6) };
                _text.normal.textColor = Color.white;
                _name = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            }
            var cam = ViewCamera();
            if (!cam) return;
            float now = Time.time;
            _bubbles.RemoveAll(b => !b.anchor || now - b.born > b.life);
            // 近的先排位置（留在原處），遠的被擠到上面；畫的時候反過來，近的蓋在上面
            _bubbles.Sort((a, b) => (a.anchor.position - cam.transform.position).sqrMagnitude.CompareTo((b.anchor.position - cam.transform.position).sqrMagnitude));
            _layout.Clear();
            foreach (var b in _bubbles)
            {
                var world = b.anchor.position + Vector3.up * b.height;
                float dist = Vector3.Distance(cam.transform.position, world);
                if (dist > MaxDistance) continue;
                var sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0f) continue;
                // 螢幕座標（IMGUI 的 y 往下）
                var pos = GUIUtility.ScreenToGUIPoint(new Vector2(sp.x * Screen.width / cam.pixelWidth, Screen.height - sp.y * Screen.height / cam.pixelHeight));
                float age = now - b.born;
                float alpha = Mathf.Clamp01(age / 0.15f) * Mathf.Clamp01((b.life - age) / 0.5f) * Mathf.Clamp01((MaxDistance - dist) / 6f);
                int font = Mathf.RoundToInt(17 * Mathf.Lerp(1.15f, 0.75f, dist / MaxDistance));
                _text.fontSize = font;
                var size = _text.CalcSize(new GUIContent(b.text));
                float nameH = string.IsNullOrEmpty(b.who) ? 0f : 18f;
                // 泡泡 + 名字整塊的範圍
                var rect = new Rect(pos.x - size.x * 0.5f, pos.y - size.y - nameH - 4f - (1f - Mathf.Clamp01(age / 0.25f)) * 6f, size.x, size.y + nameH);
                for (int guard = 0; guard < 8; guard++)
                {
                    bool moved = false;
                    foreach (var o in _layout)
                        if (o.rect.Overlaps(rect)) { rect.y = o.rect.y - rect.height - 3f; moved = true; }
                    if (!moved) break;
                }
                _layout.Add(new Placed { b = b, rect = rect, alpha = alpha, font = font, nameH = nameH });
            }
            for (int i = _layout.Count - 1; i >= 0; i--)
            {
                var p = _layout[i];
                var c = GUI.color;
                _text.fontSize = p.font;
                var box = new Rect(p.rect.x, p.rect.y + p.nameH, p.rect.width, p.rect.height - p.nameH);
                GUI.color = new Color(1f, 1f, 1f, p.alpha);
                GUI.Box(box, p.b.text, _text);
                if (p.nameH > 0f)
                {
                    var nr = new Rect(p.rect.x - 60f, p.rect.y, p.rect.width + 120f, p.nameH);
                    GUI.color = new Color(0f, 0f, 0f, p.alpha * 0.8f);
                    GUI.Label(new Rect(nr.x + 1f, nr.y + 1f, nr.width, nr.height), p.b.who, _name);
                    GUI.color = new Color(Mathf.Lerp(p.b.color.r, 1f, 0.5f), Mathf.Lerp(p.b.color.g, 1f, 0.5f), Mathf.Lerp(p.b.color.b, 1f, 0.5f), p.alpha);
                    GUI.Label(nr, p.b.who, _name);
                }
                GUI.color = c;
            }
        }

        struct Placed { public Bubble b; public Rect rect; public float alpha, nameH; public int font; }
        readonly List<Placed> _layout = new List<Placed>();
    }
}
