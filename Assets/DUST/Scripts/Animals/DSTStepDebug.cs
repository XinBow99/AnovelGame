using DUST.Player;
using UnityEngine;

namespace DUST.Animals
{
    // /ssf 除錯畫面（只有主機看得到，AI 只在主機跑）：
    //   - 每隻動物最近一次的候選終點：點越大、越紅 = 權重越高；白線 = 抽中的那一步
    //   - 頭上的字：狀態、這裡的玩家指數 W、抽中那一步影響最大的三個特徵（β·x）
    //   - 右下角：玩家周圍 400 m 的玩家指數 W（越亮越高）
    public class DSTStepDebug : MonoBehaviour
    {
        public static bool Enabled;
        const float Range = 60f;
        const int MapCells = 25;
        const float MapPixel = 7f;

        GUIStyle _label;

        void OnGUI()
        {
            if (!Enabled || Event.current.type != EventType.Repaint) return;
            var cam = Camera.main;
            if (!cam) return;
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true, alignment = TextAnchor.LowerCenter };

            foreach (var a in DSTAnimal.All)
            {
                if (!a || !a.IsAlive) continue;
                if ((a.transform.position - cam.transform.position).sqrMagnitude > Range * Range) continue;
                DrawDecision(cam, a);
            }
            DrawMap();
        }

        void DrawDecision(Camera cam, DSTAnimal a)
        {
            var d = a.LastStep;
            if (d.count > 0 && Time.time - d.time < 15f)
            {
                float maxW = 0f;
                for (int i = 0; i < d.count; i++) maxW = Mathf.Max(maxW, d.weight[i]);
                for (int i = 0; i < d.count; i++)
                {
                    if (!Screen(cam, d.ends[i] + Vector3.up * 0.1f, out var p)) continue;
                    float t = maxW > 0f ? d.weight[i] / maxW : 0f;
                    float size = Mathf.Lerp(3f, 11f, t);
                    var c = Color.Lerp(new Color(0.3f, 0.6f, 1f, 0.7f), new Color(1f, 0.15f, 0.1f, 0.95f), t);
                    Rect(p, size, c);
                }
                if (d.chosen >= 0 && Screen(cam, d.origin + Vector3.up * 0.1f, out var o) && Screen(cam, d.ends[d.chosen] + Vector3.up * 0.1f, out var e))
                    Line(o, e, Color.white, 2f);
            }

            var head = a.transform.position + Vector3.up * 1.6f;
            if (!Screen(cam, head, out var hp)) return;
            string text = $"<b>{a.CurrentState}</b>{(a.IsCarnivore ? " <color=#f96>肉食</color>" : "")}  能量 {a.Energy:0.00}  血 {a.Health:0.0}  W={DSTPlayerActivity.W(a.transform.position):0.00}";
            if (a.TargetName != null) text += $"\n→ {a.TargetName}";
            if (d.chosen >= 0) text += "\n" + TopTerms(d, 3);
            var size2 = _label.CalcSize(new GUIContent(text));
            var r = new Rect(hp.x - size2.x * 0.5f, hp.y - size2.y, size2.x, size2.y);
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(r, text, _label);
        }

        // 抽中的那一步裡，β·x 絕對值最大的幾項
        static string TopTerms(DSTStepSelection.Decision d, int n)
        {
            int count = (int)DSTStepSelection.Term.Count;
            var used = new bool[count];
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < n; k++)
            {
                int best = -1; float bv = 0.01f;
                for (int t = 0; t < count; t++)
                {
                    float v = Mathf.Abs(d.beta[t] * d.x[d.chosen, t]);
                    if (!used[t] && v > bv) { bv = v; best = t; }
                }
                if (best < 0) break;
                used[best] = true;
                float c = d.beta[best] * d.x[d.chosen, best];
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(DSTStepSelection.TermNames[best]).Append(c >= 0f ? " <color=#7f7>+" : " <color=#f77>").Append(c.ToString("0.00")).Append("</color>");
            }
            return sb.Length > 0 ? sb.ToString() : "（沒有特徵起作用）";
        }

        void DrawMap()
        {
            var me = DSTNetPlayer.Local;
            if (!me) return;
            var p = me.transform.position;
            int cx = Mathf.FloorToInt(p.x / DSTPlayerActivity.Cell), cz = Mathf.FloorToInt(p.z / DSTPlayerActivity.Cell);
            int r = MapCells / 2;
            float size = MapCells * MapPixel;
            var origin = new Vector2(UnityEngine.Screen.width - size - 16f, UnityEngine.Screen.height - size - 16f);
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(origin.x - 4f, origin.y - 22f, size + 8f, size + 26f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(origin.x, origin.y - 20f, size, 18f), "玩家指數 W（每格 16 m）");
            for (int i = -r; i <= r; i++)
            for (int j = -r; j <= r; j++)
            {
                float w = DSTPlayerActivity.CellW(cx + i, cz + j) / 1.5f;
                if (w < 0.01f) continue;
                GUI.color = new Color(1f, 0.55f, 0.1f, Mathf.Clamp01(w));
                // 北（+z）在上
                GUI.DrawTexture(new Rect(origin.x + (i + r) * MapPixel, origin.y + (r - j) * MapPixel, MapPixel, MapPixel), Texture2D.whiteTexture);
            }
            // 動物
            foreach (var a in DSTAnimal.All)
            {
                if (!a || !a.IsAlive) continue;
                var d = (a.transform.position - p) / DSTPlayerActivity.Cell;
                if (Mathf.Abs(d.x) > r || Mathf.Abs(d.z) > r) continue;
                GUI.color = a.CurrentState == DSTAnimal.State.Flee ? Color.red : Color.cyan;
                GUI.DrawTexture(new Rect(origin.x + (d.x + r + 0.5f) * MapPixel - 2f, origin.y + (r - d.z + 0.5f) * MapPixel - 2f, 4f, 4f), Texture2D.whiteTexture);
            }
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(origin.x + (r + 0.5f) * MapPixel - 3f, origin.y + (r + 0.5f) * MapPixel - 3f, 6f, 6f), Texture2D.whiteTexture);
        }

        static bool Screen(Camera cam, Vector3 world, out Vector2 gui)
        {
            var s = cam.WorldToScreenPoint(world);
            gui = new Vector2(s.x, UnityEngine.Screen.height - s.y);
            return s.z > 0.1f;
        }

        static void Rect(Vector2 p, float size, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x - size * 0.5f, p.y - size * 0.5f, size, size), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        static void Line(Vector2 a, Vector2 b, Color c, float width)
        {
            var m = GUI.matrix;
            float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.color = c;
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, (b - a).magnitude, width), Texture2D.whiteTexture);
            GUI.matrix = m;
            GUI.color = Color.white;
        }
    }
}
