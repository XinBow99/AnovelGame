using System.Collections.Generic;
using UnityEngine;

namespace DUST.Animals
{
    public enum SpeechMood : byte { Idle, Notice, Graze, Flee, Hurt, Death }

    // 每個物種自己的「語言」：從基因挑幾個音節、音高、語速。台詞 = 呢喃音節，偶爾夾一句聽得懂的短句。
    // 聲音用程式合成（諧波 + 母音共振峰 + 子音的雜訊），不用錄音檔。
    // 同一個 (物種, 情緒, 種子) 在每台電腦算出一樣的字和聲音，所以連線只要傳種子。
    public sealed class DSTVoice
    {
        enum Onset { None, Stop, Fric, Nasal, Roll }

        struct Syl
        {
            public string text; public Onset onset; public char vowel;
            public Syl(string t, Onset o, char v) { text = t; onset = o; vowel = v; }
        }

        static readonly Syl[] Table =
        {
            new Syl("咕", Onset.Stop, 'u'), new Syl("嚕", Onset.Roll, 'u'), new Syl("嘶", Onset.Fric, 'i'), new Syl("啾", Onset.Stop, 'i'),
            new Syl("呼", Onset.Fric, 'u'), new Syl("哼", Onset.Nasal, 'e'), new Syl("嗚", Onset.None, 'u'), new Syl("喀", Onset.Stop, 'a'),
            new Syl("噗", Onset.Stop, 'u'), new Syl("咻", Onset.Fric, 'i'), new Syl("嗯", Onset.Nasal, 'e'), new Syl("咿", Onset.None, 'i'),
            new Syl("唧", Onset.Stop, 'i'), new Syl("呱", Onset.Stop, 'a'), new Syl("呣", Onset.Nasal, 'u'), new Syl("哈", Onset.Fric, 'a'),
            new Syl("吱", Onset.Fric, 'i'), new Syl("咯", Onset.Stop, 'o'), new Syl("嗷", Onset.None, 'o'), new Syl("嚶", Onset.Nasal, 'i'),
        };

        static readonly Dictionary<SpeechMood, string[]> Phrases = new Dictionary<SpeechMood, string[]>
        {
            { SpeechMood.Idle, new[] { "好熱……", "沙子好燙。", "水在哪裡……", "風來了。", "好餓……", "今天也很安靜。" } },
            { SpeechMood.Notice, new[] { "……人類？", "誰？", "別過來。", "嗯？那是什麼……", "你也是一個人嗎？" } },
            { SpeechMood.Graze, new[] { "好吃。", "嚼……嚼……", "這根還行。" } },
            { SpeechMood.Flee, new[] { "快跑！", "危險！", "呀——！" } },
            { SpeechMood.Hurt, new[] { "好痛！", "住手！" } },
            { SpeechMood.Death, new[] { "……" } },
        };
        static readonly string[] NightIdle = { "好黑……", "月亮出來了。", "好冷……" };

        static readonly Dictionary<int, DSTVoice> _cache = new Dictionary<int, DSTVoice>();

        public static DSTVoice For(DSTGenome g)
        {
            if (!_cache.TryGetValue(g.key, out var v)) _cache[g.key] = v = new DSTVoice(g);
            return v;
        }

        readonly int[] _syl;           // 這個物種會用的音節（Table 的索引）
        public readonly float pitch;   // 基頻（Hz）
        readonly float _rate, _gruff, _formant, _phraseChance;

        DSTVoice(DSTGenome g)
        {
            var r = new System.Random(g.key ^ 0x51ED270B);
            int n = 4 + r.Next(3);
            var pool = new List<int>();
            for (int i = 0; i < Table.Length; i++) pool.Add(i);
            _syl = new int[n];
            for (int i = 0; i < n; i++) { int k = r.Next(pool.Count); _syl[i] = pool[k]; pool.RemoveAt(k); }
            float small = 1f - Mathf.InverseLerp(0.35f, 1.3f, g.size);
            pitch = Mathf.Lerp(110f, 520f, small) * (0.85f + (float)r.NextDouble() * 0.3f);
            _formant = Mathf.Lerp(0.85f, 1.35f, small);
            _rate = (g.temperament == Temperament.Shy ? 1.25f : 1f) * (0.85f + (float)r.NextDouble() * 0.35f);
            _gruff = (float)r.NextDouble() * 0.35f + (1f - small) * 0.15f;
            _phraseChance = 0.25f + (float)r.NextDouble() * 0.15f;
        }

        // ------------------------------------------------------------------ 台詞
        public struct Line { public string text; public int[] syllables; public float pitchMul, rateMul; public bool question, exclaim; }

        public Line Make(SpeechMood mood, int seed, float hour)
        {
            var r = new System.Random(seed);
            var line = new Line { pitchMul = 1f, rateMul = 1f };
            int count = mood switch
            {
                SpeechMood.Flee => 2 + r.Next(2),
                SpeechMood.Hurt => 1 + r.Next(2),
                SpeechMood.Death => 2,
                _ => 2 + r.Next(4),
            };
            if (mood == SpeechMood.Flee || mood == SpeechMood.Hurt) { line.pitchMul = 1.3f; line.rateMul = 1.4f; line.exclaim = true; }
            if (mood == SpeechMood.Death) { line.pitchMul = 0.75f; line.rateMul = 0.6f; }
            if (mood == SpeechMood.Notice) line.question = true;

            // 呢喃：同一個音節常會重複（咕嚕嚕、啾啾）
            var syl = new List<int>();
            var sb = new System.Text.StringBuilder();
            int last = -1;
            for (int i = 0; i < count; i++)
            {
                int s = (last >= 0 && r.NextDouble() < 0.35) ? last : _syl[r.Next(_syl.Length)];
                last = s;
                syl.Add(s);
                sb.Append(Table[s].text);
                if (i < count - 1 && r.NextDouble() < 0.2) { sb.Append("…"); syl.Add(-1); }
            }
            sb.Append(line.exclaim ? "！" : line.question ? "？" : "…");

            // 偶爾說一句聽得懂的
            if (mood != SpeechMood.Death && r.NextDouble() < _phraseChance)
            {
                var list = mood == SpeechMood.Idle && (hour >= 19f || hour < 5.5f) ? NightIdle : Phrases[mood];
                string p = list[r.Next(list.Length)];
                sb.Append(" ").Append(p);
                // 聽得懂的句子在牠的語言裡也是一串聲音
                syl.Add(-1);
                int extra = Mathf.Clamp(p.Length / 2, 1, 5);
                for (int i = 0; i < extra; i++) syl.Add(_syl[r.Next(_syl.Length)]);
            }
            line.text = sb.ToString();
            line.syllables = syl.ToArray();
            return line;
        }

        // ------------------------------------------------------------------ 聲音合成
        const int Rate = 22050;

        static Vector2 Formants(char v) => v switch
        {
            'a' => new Vector2(800f, 1250f),
            'i' => new Vector2(300f, 2300f),
            'u' => new Vector2(330f, 800f),
            'o' => new Vector2(520f, 900f),
            _ => new Vector2(480f, 1850f),   // e
        };

        public AudioClip Synth(Line line, int seed)
        {
            var r = new System.Random(seed * 31 + 7);
            var data = new List<float>(Rate);
            float f0 = pitch * line.pitchMul;
            int voiced = 0;
            foreach (var s in line.syllables) if (s >= 0) voiced++;
            int idx = 0;
            float phase = 0f;
            foreach (var si in line.syllables)
            {
                if (si < 0) { Silence(data, 0.12f / line.rateMul); continue; }
                var syl = Table[si];
                float dur = (0.13f + (float)r.NextDouble() * 0.06f) / (_rate * line.rateMul);
                // 音調：問句尾巴上揚、驚叫往下掉，平常隨機起伏
                float t01 = voiced > 1 ? idx / (float)(voiced - 1) : 0f;
                float contour = line.question ? Mathf.Lerp(0.95f, 1.35f, t01 * t01) : line.exclaim ? Mathf.Lerp(1.15f, 0.9f, t01) : 1f + ((float)r.NextDouble() - 0.5f) * 0.25f;
                Syllable(data, syl, dur, f0 * contour, r, ref phase);
                Silence(data, (0.03f + (float)r.NextDouble() * 0.04f) / _rate);
                idx++;
            }
            // 正規化
            float peak = 1e-4f;
            foreach (var x in data) peak = Mathf.Max(peak, Mathf.Abs(x));
            var arr = data.ToArray();
            for (int i = 0; i < arr.Length; i++) arr[i] *= 0.55f / peak;
            var clip = AudioClip.Create("Voice", arr.Length, 1, Rate, false);
            clip.SetData(arr, 0);
            return clip;
        }

        static void Silence(List<float> d, float sec) { int n = (int)(sec * Rate); for (int i = 0; i < n; i++) d.Add(0f); }

        void Syllable(List<float> d, Syl s, float dur, float f0, System.Random r, ref float phase)
        {
            // 子音
            float noise = 0f, prev = 0f;
            if (s.onset == Onset.Stop) { Burst(d, 0.012f, 0.6f, r); Silence(d, 0.008f); }
            else if (s.onset == Onset.Fric) Burst(d, 0.07f / _rate, 0.35f, r);

            var f = Formants(s.vowel) * _formant;
            if (s.onset == Onset.Nasal) f = new Vector2(260f, 1000f) * _formant;
            int n = (int)(dur * Rate);
            float vib = 5f + (float)r.NextDouble() * 2f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate, u = i / (float)n;
                // 鼻音前半段之後轉成母音
                var ff = s.onset == Onset.Nasal ? Vector2.Lerp(f, Formants(s.vowel) * _formant, Mathf.SmoothStep(0.3f, 0.6f, u)) : f;
                float hz = f0 * (1f + 0.02f * Mathf.Sin(2f * Mathf.PI * vib * t)) * (1f - 0.08f * u);
                phase += 2f * Mathf.PI * hz / Rate;
                float v = 0f;
                for (int h = 1; h <= 10; h++)
                {
                    float fh = h * hz;
                    float g = Mathf.Exp(-Sq((fh - ff.x) / 220f)) + 0.6f * Mathf.Exp(-Sq((fh - ff.y) / 320f)) + 0.08f;
                    v += Mathf.Sin(phase * h) * g / h;
                }
                // 捲舌音：開頭抖一抖
                if (s.onset == Onset.Roll && u < 0.45f) v *= 0.55f + 0.45f * Mathf.Sin(2f * Mathf.PI * 28f * t);
                // 沙啞：加一點雜訊
                noise = ((float)r.NextDouble() * 2f - 1f);
                prev = Mathf.Lerp(prev, noise, 0.35f);
                v += prev * _gruff * 0.6f;
                float env = Mathf.Min(1f, t / 0.012f) * Mathf.Min(1f, (n - i) / (0.045f * Rate));
                d.Add(v * env);
            }
        }

        static void Burst(List<float> d, float sec, float amp, System.Random r)
        {
            int n = (int)(sec * Rate);
            float last = 0f;
            for (int i = 0; i < n; i++)
            {
                float x = (float)r.NextDouble() * 2f - 1f;
                float hp = x - last; last = x;              // 高頻一點（嘶聲）
                d.Add(hp * amp * Mathf.Sin(Mathf.PI * i / n));
            }
        }

        static float Sq(float x) => x * x;
    }
}
