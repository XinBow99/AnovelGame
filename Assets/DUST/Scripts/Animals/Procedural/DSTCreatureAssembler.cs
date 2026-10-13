using System.Collections.Generic;
using UnityEngine;

namespace DUST.Animals
{
    public enum CreaturePose : byte { Normal, Graze, Alert, Flee, Dead }

    // 把基因拼成一隻動物：Blender 零件庫（SK_DST_CreatureKit：共用鬃狼骨架，所有零件都蒙皮在上面）每個欄位留一個，
    // 用骨頭調比例（移動子骨頭 = 拉長但不變粗），再上每個物種自己的毛色。動畫用鬃狼的動作（只留旋轉的版本，比例才不會被蓋掉）。
    public sealed class CreatureRig
    {
        public Transform root;           // 「Visual」：整隻的根（大小、墊高、死掉側倒都在這）
        public Animator animator;
        public Transform head;
        public Vector3 restPosition;
    }

    public static class DSTCreatureAssembler
    {
        const string Prefix = "KIT_";

        sealed class Mats { public Material coat, horn, eye; }
        static readonly Dictionary<int, Mats> _mats = new Dictionary<int, Mats>();

        public static CreatureRig Build(DSTGenome g, Transform parent, GameObject kit, RuntimeAnimatorController controller, Material template)
        {
            var inst = Object.Instantiate(kit, parent, false);
            inst.name = "Visual";
            // 零件庫面向 -Z（Blender 匯出），NavMeshAgent 以 +Z 為前方
            inst.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            inst.transform.localScale = Vector3.one * g.size;

            // ---- 零件：每個欄位只留選中的那個
            var want = new HashSet<string> { "Body_" + g.body, "Head_" + g.head };
            if (!string.IsNullOrEmpty(g.ears)) want.Add("Ears_" + g.ears);
            if (!string.IsNullOrEmpty(g.tail)) want.Add("Tail_" + g.tail);
            if (!string.IsNullOrEmpty(g.horns)) want.Add("Horns_" + g.horns);
            if (g.eyes) want.Add("Eyes_Canid");
            var m = MatsFor(g, template);
            var smrs = inst.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            string PartOf(SkinnedMeshRenderer s) => s.name.StartsWith(Prefix) ? s.name.Substring(Prefix.Length) : s.name;
            // 零件庫還沒有這個零件（例如舊版 kit）：身體 / 頭至少要留一個
            foreach (var slot in new[] { "Body_", "Head_" })
            {
                bool has = false;
                foreach (var s in smrs) if (want.Contains(PartOf(s)) && PartOf(s).StartsWith(slot)) has = true;
                if (!has) foreach (var s in smrs) if (PartOf(s).StartsWith(slot)) { want.Add(PartOf(s)); break; }
            }
            foreach (var smr in smrs)
            {
                string part = PartOf(smr);
                if (!want.Contains(part)) { Object.Destroy(smr.gameObject); continue; }
                smr.sharedMaterial = part.StartsWith("Eyes") ? m.eye : part.StartsWith("Horns") ? m.horn : m.coat;
                smr.updateWhenOffscreen = false;
            }

            // ---- 骨頭比例
            var bones = new Dictionary<string, Transform>();
            foreach (var t in inst.GetComponentsInChildren<Transform>(true)) bones[t.name] = t;
            void Stretch(float f, params string[] names) { foreach (var n in names) if (bones.TryGetValue(n, out var b)) b.localPosition *= f; }
            Stretch(g.legLen, "Forearm_L", "Forearm_R", "Hand_L", "Hand_R", "Shin_L", "Shin_R", "Foot_L", "Foot_R", "UpperArm_L", "UpperArm_R");
            Stretch(g.neckLen, "Neck2", "Head");
            Stretch(g.bodyLen, "Spine2", "Chest");
            Stretch(g.tailLen, "Tail2", "Tail3", "Tail4");
            if (bones.TryGetValue("Head", out var head)) head.localScale = Vector3.one * g.headSize;

            // 先套一幀待機動畫：遊戲裡站著的是待機姿勢，不是綁定姿勢（腿彎的程度不一樣）
            var anim = inst.GetComponent<Animator>();
            if (!anim) anim = inst.AddComponent<Animator>();
            anim.runtimeAnimatorController = controller;
            anim.applyRootMotion = false;
            anim.Rebind();
            anim.Update(0f);

            // 腿變長了：整隻墊高，讓「選中零件的皮膚最低點」剛好碰地（量實際網格，零件改造型也不用調）
            float minY = float.MaxValue;
            var baked = new Mesh();
            foreach (var smr in smrs)
            {
                if (!want.Contains(PartOf(smr))) continue;
                smr.BakeMesh(baked, false);
                var toParent = parent.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                foreach (var v in baked.vertices) minY = Mathf.Min(minY, toParent.MultiplyPoint3x4(v).y);
            }
            Object.Destroy(baked);
            if (minY < float.MaxValue) inst.transform.localPosition = new Vector3(0f, -minY, 0f);

            anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            return new CreatureRig { root = inst.transform, animator = anim, head = head, restPosition = inst.transform.localPosition };
        }

        // ------------------------------------------------------------------ 毛色（每個物種一套材質）
        static Mats MatsFor(DSTGenome g, Material template)
        {
            if (_mats.TryGetValue(g.key, out var m) && m.coat) return m;
            m = new Mats();
            m.coat = new Material(template) { name = "M_Gen_Coat_" + g.key };
            m.coat.SetTexture("_BaseColorMap", CoatTexture(g));
            m.coat.SetColor("_BaseColor", Color.white);
            m.horn = new Material(template) { name = "M_Gen_Horn_" + g.key };
            m.horn.SetTexture("_BaseColorMap", null);
            m.horn.SetColor("_BaseColor", Color.Lerp(g.hoof, new Color(0.32f, 0.27f, 0.2f), 0.5f));
            m.horn.SetFloat("_Smoothness", 0.45f);
            m.eye = new Material(template) { name = "M_Gen_Eye_" + g.key };
            m.eye.SetTexture("_BaseColorMap", null);
            m.eye.SetColor("_BaseColor", new Color(0.035f, 0.022f, 0.012f));
            m.eye.SetFloat("_Smoothness", 0.92f);
            _mats[g.key] = m;
            return m;
        }

        // UV（Blender 匯出時做的圓柱投影）：u 繞身體一圈（0 / 1 = 肚子中線、0.5 = 背脊），v = 頭（0）→ 尾（1）
        static Texture2D CoatTexture(DSTGenome g)
        {
            const int W = 128, H = 256;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = "T_Gen_Coat_" + g.key, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, anisoLevel = 4 };
            var px = new Color[W * H];
            var r = new System.Random(g.key ^ 0x2c1b3c6d);
            float ox = (float)r.NextDouble() * 100f, oy = (float)r.NextDouble() * 100f;
            float sc = g.patternScale;
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float u = x / (float)W, v = y / (float)H;
                float back = 1f - Mathf.Abs(u - 0.5f) * 2f;            // 背 1、肚 0
                // 肚子淡、背深（中間用平滑過渡）
                var c = Color.Lerp(g.belly, g.coat, Mathf.SmoothStep(0.15f, 0.55f, back));
                c = Color.Lerp(c, g.coat * 0.88f, Mathf.SmoothStep(0.8f, 1f, back) * 0.4f);    // 背脊再深一點
                // 大塊斑駁（u 方向的雜訊要首尾相接）
                float mott = Noise(ox, oy, u, v * 3f, 3f) * 0.14f - 0.07f;
                c = new Color(c.r * (1f + mott), c.g * (1f + mott), c.b * (1f + mott));
                float mk = 0f;
                switch (g.pattern)
                {
                    case Pattern.Stripes:
                        float wob = Noise(ox + 3f, oy, u, v * 2f, 2f) * 0.5f;
                        mk = Mathf.SmoothStep(0.58f, 0.78f, Mathf.Sin((v * 14f * sc + wob) * Mathf.PI * 2f) * 0.5f + 0.5f) * Mathf.SmoothStep(0.25f, 0.65f, back);
                        break;
                    case Pattern.Spots:
                        mk = Mathf.SmoothStep(0.6f, 0.68f, Noise(ox + 7f, oy, u, v * 9f * sc, 9f * sc)) * Mathf.SmoothStep(0.2f, 0.6f, back);
                        break;
                    case Pattern.Saddle:
                        mk = Mathf.SmoothStep(0.62f, 0.8f, back + (Noise(ox, oy + 5f, u, v * 4f, 1f) - 0.5f) * 0.25f) * Mathf.SmoothStep(0.25f, 0.4f, v) * (1f - Mathf.SmoothStep(0.7f, 0.85f, v));
                        break;
                }
                c = Color.Lerp(c, g.mark, mk * 0.85f);
                // 尾尖、鼻頭一帶（v 兩端）偏深
                float ends = Mathf.SmoothStep(0.9f, 1f, v) + Mathf.SmoothStep(0.06f, 0f, v);
                c = Color.Lerp(c, g.mark, Mathf.Clamp01(ends) * 0.7f);
                px[y * W + x] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
            }
            tex.SetPixels(px);
            tex.Apply(true, true);
            return tex;
        }

        // u 方向首尾相接的 Perlin（繞身體一圈不會有接縫）
        static float Noise(float ox, float oy, float u, float v, float uFreq)
        {
            float a = u * Mathf.PI * 2f;
            float rad = uFreq / (Mathf.PI * 2f) * 2f;
            return Mathf.PerlinNoise(ox + Mathf.Cos(a) * rad + v, oy + Mathf.Sin(a) * rad) * 0.5f +
                   Mathf.PerlinNoise(ox * 0.7f + v * 1.3f, oy * 1.3f + Mathf.Cos(a) * rad + Mathf.Sin(a) * rad * 0.5f) * 0.5f;
        }
    }
}
