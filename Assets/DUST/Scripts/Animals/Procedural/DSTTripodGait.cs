using System.Collections.Generic;
using UnityEngine;

namespace DUST.Animals
{
    // 沙陶翻盤獸（三輻射三足）的程式步態與動作，照 docs/tripod_A_spec.md 與 tools/tripod_bake_*.py：
    //   走路（翻面步）：選「朝外法線最接近要走方向」的邊，第三隻腳鏡射過那條邊；身體繞 u = 前進 × 上 翻 180°。
    //   跳躍（三腳一起）：蓄力下蹲 → 蹬伸 → 照 9.81 m/s² 的拋物線飛、腳收攏 → 落地緩衝 → 復原。不鏡射，所以不用翻面。
    //   蹲伏警戒：身體下沉、往威脅（AI 面向的方向）傾。
    // 跟 AI 的對應（DSTProcAnimal 傳姿勢進來）：
    //   一般 / 閒逛 → 翻面走（走得快就翻得快、不停頓 = 快走）
    //   逃跑 / 追獵 → 落後超過一步多就向前跳（GPT 的 LeapForward，時間壓短）
    //   剛進入警戒 → 原地彈跳一下（HopInPlace），之後蹲伏（CrouchAlert）
    //   站著不動很久 → 偶爾原地跳一下
    // 只動外觀，每台電腦依自己看到的移動各算各的（不用同步）；AI、導航、碰撞照舊用 DSTAnimal。
    [DisallowMultipleComponent]
    public class DSTTripodGait : MonoBehaviour
    {
        public CreaturePose Pose;

        const float MoveFrames = 14f / 24f;    // 一步 14 格（24 fps）
        const float BodyLift = 0.03f, FootLift = 0.10f;   // 公尺（模型原始大小，乘上 _scale）
        const float Gravity = 9.81f;

        Transform _owner, _body;
        readonly Transform[] _upper = new Transform[3], _lower = new Transform[3], _foot = new Transform[3];
        readonly float[] _l1 = new float[3], _l2 = new float[3];
        readonly Quaternion[] _upperOff = new Quaternion[3], _lowerOff = new Quaternion[3], _footRest = new Quaternion[3];
        readonly float[] _restAngle = new float[3], _yaw = new float[3];
        Quaternion _bodyRest;
        float _scale, _height, _footY, _poleR, _poleH, _stepLen;
        bool _ready;

        // 狀態：三個腳掌（世界座標，腳掌骨頭的位置）、身體累積的翻轉
        readonly Vector3[] _feet = new Vector3[3];
        Quaternion _face = Quaternion.identity;

        // 進行中的翻面步
        bool _stepping;
        int _swing;
        float _t, _dur, _yawChange;
        Vector3 _swingFrom, _landing, _startCenter, _move, _axis;
        Quaternion _startFace;
        readonly float[] _a0 = new float[3], _a1 = new float[3];

        // 進行中的跳躍
        bool _jumping;
        float _jt, _jPrep, _jPush, _jAir, _jLand, _jRecover, _jPitch;
        Vector3 _jDir, _jShift;
        readonly Vector3[] _jFrom = new Vector3[3], _jTo = new Vector3[3];

        // 姿勢
        Vector3 _lastPos;
        float _speed, _deadT, _alert, _graze, _idleFor, _nextIdleHop;
        CreaturePose _lastPose;
        Vector3 _deadAxis;

        static readonly RaycastHit[] _hits = new RaycastHit[8];

        // ------------------------------------------------------------------ 組裝
        public static CreatureRig Build(DSTGenome g, Transform parent, GameObject model, Material baked, Material template)
        {
            var inst = Instantiate(model, parent, false);
            inst.name = "Visual";
            inst.transform.localScale = Vector3.one * g.size * DSTGenome.TriradialModelScale;
            var anim = inst.GetComponent<Animator>();
            if (anim) Destroy(anim);                                  // 骨頭全部由程式擺
            var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr)
            {
                smr.sharedMaterials = MaterialsFor(g, baked, template, smr.sharedMaterials.Length);
                smr.updateWhenOffscreen = true;                       // 身體會走在骨架原點外面，邊界要跟著算
            }
            inst.AddComponent<DSTTripodGait>();
            Transform head = null;
            foreach (var t in inst.GetComponentsInChildren<Transform>(true)) if (t.name == "Body") head = t;
            return new CreatureRig { root = inst.transform, animator = null, head = head, restPosition = inst.transform.localPosition };
        }

        /// <summary>縮放完、放到位置上之後呼叫：記下靜止姿勢（腿長、骨頭的扭轉、腳掌位置）</summary>
        public void Init(Transform owner)
        {
            _owner = owner;
            var bones = new Dictionary<string, Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true)) bones[t.name] = t;
            _body = bones["Body"];
            float ground = transform.position.y;
            _scale = transform.lossyScale.y;
            _height = _body.position.y - ground;
            _bodyRest = _body.rotation;
            Vector3 c = Vector3.zero;
            for (int k = 0; k < 3; k++)
            {
                _upper[k] = bones[$"Leg{k}_Upper"]; _lower[k] = bones[$"Leg{k}_Lower"]; _foot[k] = bones[$"Foot{k}_IK"];
                var pole = bones[$"Pole{k}"].position;
                Vector3 hip = _upper[k].position, knee = _lower[k].position, foot = _foot[k].position;
                _l1[k] = (knee - hip).magnitude;
                _l2[k] = (foot - knee).magnitude;
                var bend = Perp(pole - hip, foot - hip);
                _upperOff[k] = Quaternion.Inverse(Quaternion.LookRotation(knee - hip, bend)) * _upper[k].rotation;
                _lowerOff[k] = Quaternion.Inverse(Quaternion.LookRotation(foot - knee, bend)) * _lower[k].rotation;
                _footRest[k] = _foot[k].rotation;
                _feet[k] = foot;
                c += foot;
                if (k == 0) { _poleR = Flat(pole - _body.position).magnitude; _poleH = pole.y - ground; _footY = foot.y - ground; }
            }
            c /= 3f;
            for (int k = 0; k < 3; k++) _restAngle[k] = Angle(_feet[k] - c);
            _stepLen = Flat(_feet[0] - c).magnitude;          // 鏡射一隻腳 = 重心移動一個外接圓半徑
            _lastPos = transform.position;
            _nextIdleHop = Random.Range(12f, 30f);
            _ready = true;
        }

        // ------------------------------------------------------------------ 每幀
        void LateUpdate()
        {
            if (!_ready) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            var here = transform.position;                   // 外觀根節點 = 這隻動物腳下的地面
            var d = Flat(here - _lastPos);
            _lastPos = here;
            float inst = d.magnitude / dt;
            if (inst > 20f) inst = 0f;
            _speed = Mathf.Lerp(_speed, inst, 1f - Mathf.Exp(-6f * dt));

            if (Pose == CreaturePose.Dead) { Collapse(dt, here.y); return; }

            bool fleeing = Pose == CreaturePose.Flee;
            // 剛進入警戒：先原地彈一下（受驚），之後蹲伏
            if (Pose == CreaturePose.Alert && _lastPose != CreaturePose.Alert && !_jumping && !_stepping) StartJump(Vector3.zero, 0f, false, here.y);
            _lastPose = Pose;

            var center = Centroid();
            var off = Flat(here - center);
            float lag = off.magnitude;
            // 只有真的傳送（生成、讀檔、/tp）才直接站過去；其他落後都用跳的追回來，不會往前閃現
            if (lag > Mathf.Max(8f, _stepLen * 25f)) Reset(here);
            else if (!_stepping && !_jumping)
            {
                // 逃跑 / 追獵：落後超過一步多就向前跳；翻面走追不上（落後超過 2.5 步）也改用跳的；其他時候翻面走
                if ((fleeing && lag > _stepLen * 1.2f) || lag > _stepLen * 2.5f) StartJump(off.normalized, lag, true, here.y);
                else if (lag > _stepLen * 0.45f) StartStep(off, here.y);
            }

            // 站著不動很久：偶爾原地跳一下（HopInPlace）
            _idleFor = _speed < 0.05f && !_stepping && !_jumping && Pose == CreaturePose.Normal ? _idleFor + dt : 0f;
            if (_idleFor > _nextIdleHop)
            {
                _idleFor = 0f; _nextIdleHop = Random.Range(15f, 40f);
                StartJump(Vector3.zero, 0f, false, here.y);
            }

            _alert = Mathf.MoveTowards(_alert, Pose == CreaturePose.Alert ? 1f : 0f, dt * 3f);
            _graze = Mathf.MoveTowards(_graze, Pose == CreaturePose.Graze ? 1f : 0f, dt * 2f);

            if (_jumping) { ApplyJump(dt, here.y); return; }
            float p = 0f;
            if (_stepping)
            {
                _t += dt / _dur;
                p = Smooth(Mathf.Min(1f, _t));
                if (_t >= 1f) FinishStep();
            }
            Apply(here.y, p);
        }

        // ------------------------------------------------------------------ 翻面步
        void StartStep(Vector3 want, float ground)
        {
            var dir = want.normalized;
            float best = -2f;
            for (int s = 0; s < 3; s++)
            {
                int a = (s + 1) % 3, b = (s + 2) % 3;
                var edge = Flat(_feet[b] - _feet[a]).normalized;
                var proj = _feet[a] + edge * Vector3.Dot(Flat(_feet[s] - _feet[a]), edge);
                var landing = proj * 2f - _feet[s];
                var move = Flat(landing - _feet[s]) / 3f;
                float score = Vector3.Dot(move.normalized, dir);
                if (score <= best) continue;
                best = score; _swing = s; _landing = landing; _move = move;
            }
            if (best < 0.2f) return;
            _landing.y = GroundAt(_landing, ground) + _footY;
            _swingFrom = _feet[_swing];
            _startCenter = Centroid();
            _axis = Vector3.Cross(_move.normalized, Vector3.up);          // 後緣往下、往前翻
            _startFace = _face;
            var endCenter = _startCenter + _move;
            for (int k = 0; k < 3; k++)
            {
                _a0[k] = Angle(_feet[k] - _startCenter);
                _a1[k] = Angle((k == _swing ? _landing : _feet[k]) - endCenter);
            }
            _yawChange = Mathf.DeltaAngle(_yaw[_swing], _a1[_swing] - _restAngle[_swing]);
            // 一步多久：照移動速度，走得快就翻得快（快走 = 一步 8 格、不停頓；最慢照規格 14 格）
            float slow = MoveFrames * Mathf.Sqrt(_scale);
            float fast = Pose == CreaturePose.Flee ? 0.2f : 8f / 24f;
            _dur = _speed > 0.05f ? Mathf.Clamp(_stepLen / _speed * 0.9f, fast, slow) : slow;
            _t = 0f;
            _stepping = true;
        }

        void FinishStep()
        {
            _feet[_swing] = _landing;
            _face = Quaternion.AngleAxis(180f, _axis) * _startFace;
            _yaw[_swing] += _yawChange;
            _stepping = false;
        }

        // ------------------------------------------------------------------ 跳躍（三腳一起）
        // 時間照 tools/tripod_bake_actions.py：原地 8/3/12/6/10 格、向前 12/4/18/6/12 格（24 fps）；逃跑時壓短
        void StartJump(Vector3 dir, float distance, bool urgent, float ground)
        {
            _stepping = false;
            _jDir = dir; _jShift = dir * distance;
            if (distance <= 0f)      { _jPrep = 8f / 24f; _jPush = 3f / 24f; _jAir = 12f / 24f; _jLand = 6f / 24f; _jRecover = 10f / 24f; _jPitch = 3f; }
            else if (!urgent)        { _jPrep = 12f / 24f; _jPush = 4f / 24f; _jAir = 18f / 24f; _jLand = 6f / 24f; _jRecover = 12f / 24f; _jPitch = 8f; }
            else
            {
                // 追趕跳：distance 傳進來的是「現在落後多少」。跳的距離再加上整個跳躍期間導航會往前走的距離，
                // 落地時剛好在 AI 的位置上，落後量不會一跳一跳累積（累積到門檻就會瞬移 = 往前閃現）
                _jPrep = 0.1f; _jPush = 0.06f; _jLand = 0.08f; _jRecover = 0.08f; _jPitch = 8f;
                _jAir = Mathf.Clamp(0.45f * Mathf.Sqrt(_scale), 0.3f, 0.6f);
                float total = _jPrep + _jPush + _jAir + _jLand + _jRecover;
                distance = Mathf.Min(distance + _speed * total, _stepLen * 14f);
                _jShift = dir * distance;
            }
            for (int k = 0; k < 3; k++)
            {
                _jFrom[k] = _feet[k];
                var to = _feet[k] + _jShift;
                to.y = GroundAt(to, ground) + _footY;
                _jTo[k] = to;
            }
            _jt = 0f;
            _jumping = true;
        }

        void ApplyJump(float dt, float ground)
        {
            _jt += dt;
            float s = _scale;
            var c0 = (_jFrom[0] + _jFrom[1] + _jFrom[2]) / 3f;
            float baseY = ground + _height;
            float lift; float tuck = 0f, air = 0f, pitch = 0f;
            Vector3 shift = Vector3.zero;
            float t = _jt;
            if (t < _jPrep) lift = -0.11f * s * Smooth(t / _jPrep);
            else if ((t -= _jPrep) < _jPush) lift = (-0.11f + 0.23f * Smooth(t / _jPush)) * s;
            else if ((t -= _jPush) < _jAir)
            {
                float u = t / _jAir;
                air = 0.5f * Gravity * _jAir * _jAir * u * (1f - u);
                tuck = Mathf.Pow(Mathf.Sin(Mathf.PI * u), 2f);
                shift = _jShift * u;
                lift = 0.12f * s + air;
                pitch = _jPitch * Mathf.Sin(Mathf.PI * u);
            }
            else if ((t -= _jAir) < _jLand) { shift = _jShift; lift = (0.12f - 0.25f * Smooth(t / _jLand)) * s; }
            else if ((t -= _jLand) < _jRecover) { shift = _jShift; lift = (-0.13f + 0.13f * Smooth(t / _jRecover)) * s; }
            else
            {
                for (int k = 0; k < 3; k++) _feet[k] = _jTo[k];
                _jumping = false;
                Apply(ground, 0f);
                return;
            }
            var center = c0 + shift;
            var tiltAxis = _jDir.sqrMagnitude > 0f ? Vector3.Cross(Vector3.up, _jDir) : Vector3.right;
            var face = Quaternion.AngleAxis(-pitch, tiltAxis) * _face;      // 空中前緣微微抬起
            _body.SetPositionAndRotation(new Vector3(center.x, baseY + lift, center.z), face * _bodyRest);
            for (int k = 0; k < 3; k++)
            {
                Vector3 target;
                if (air > 0f || tuck > 0f)
                {
                    // 空中：腳往中心收 30%、往上收
                    var from = _jFrom[k] + shift;
                    var c = c0 + shift;
                    target = Vector3.Lerp(from, new Vector3(c.x, from.y, c.z), 0.30f * tuck);
                    target.y = Mathf.Lerp(_jFrom[k].y, _jTo[k].y, shift.magnitude / Mathf.Max(1e-4f, _jShift.magnitude)) + air + 0.12f * s * tuck;
                }
                else target = shift == Vector3.zero ? _jFrom[k] : _jTo[k];
                var pole = new Vector3(center.x, ground + _poleH, center.z) + Dir(Angle(target - center)) * _poleR;
                Solve(k, target, pole);
                _foot[k].SetPositionAndRotation(target, Quaternion.AngleAxis(-_yaw[k], Vector3.up) * _footRest[k]);
            }
        }

        int _resets;    // 除錯：瞬移重設了幾次（應該只有生成 / 傳送時才會發生）

        void Reset(Vector3 here)
        {
            _resets++;
            _stepping = false; _jumping = false;
            var c = Centroid();
            var shift = Flat(here - c);
            for (int k = 0; k < 3; k++)
            {
                _feet[k] += shift;
                _feet[k].y = GroundAt(_feet[k], here.y) + _footY;
            }
        }

        // ------------------------------------------------------------------ 擺骨頭
        void Apply(float ground, float p)
        {
            Vector3 center = _stepping ? _startCenter + _move * p : Centroid();
            float lift = _stepping ? BodyLift * _scale * Mathf.Sin(Mathf.PI * p) : 0f;
            var face = _stepping ? Quaternion.AngleAxis(180f * p, _axis) * _startFace : _face;
            // 蹲伏警戒（CrouchAlert：沉 0.09 m、傾 9°，往 AI 面向的方向 = 威脅）；吃東西沉得更低
            float sink = _scale * (0.09f * _alert) + _height * 0.2f * _graze;
            var tiltAxis = Vector3.Cross(Vector3.up, Flat(_owner ? _owner.forward : transform.forward).normalized);
            var tilt = Quaternion.AngleAxis(9f * _alert, tiltAxis);
            _body.SetPositionAndRotation(new Vector3(center.x, ground + _height + lift - sink, center.z), tilt * face * _bodyRest);

            for (int k = 0; k < 3; k++)
            {
                var target = _feet[k];
                float yaw = _yaw[k];
                if (_stepping && k == _swing)
                {
                    target = Vector3.Lerp(_swingFrom, _landing, p);
                    target.y += FootLift * _scale * Mathf.Sin(Mathf.PI * p);
                    yaw += _yawChange * p;
                }
                float a = _stepping ? _a0[k] + Mathf.DeltaAngle(_a0[k], _a1[k]) * p : Angle(target - center);
                var pole = new Vector3(center.x, ground + _poleH, center.z) + Dir(a) * _poleR;
                Solve(k, target, pole);
                _foot[k].SetPositionAndRotation(target, Quaternion.AngleAxis(-yaw, Vector3.up) * _footRest[k]);
            }
        }

        // 兩節腿的解析解（跟 tripod_bake_walk.py 的 knee_position 一樣）；搆不到就夾在可達範圍內
        void Solve(int k, Vector3 target, Vector3 pole)
        {
            var hip = _upper[k].position;
            var to = target - hip;
            float l1 = _l1[k], l2 = _l2[k];
            float dist = Mathf.Clamp(to.magnitude, Mathf.Abs(l1 - l2) + 1e-4f, l1 + l2 - 1e-4f);
            var dir = to.sqrMagnitude > 1e-8f ? to.normalized : Vector3.down;
            var bend = Perp(pole - hip, dir);
            float along = (l1 * l1 - l2 * l2 + dist * dist) / (2f * dist);
            float h = Mathf.Sqrt(Mathf.Max(0f, l1 * l1 - along * along));
            var knee = hip + dir * along + bend * h;
            var ankle = hip + dir * dist;
            _upper[k].rotation = Quaternion.LookRotation(knee - hip, bend) * _upperOff[k];
            _lower[k].rotation = Quaternion.LookRotation(ankle - knee, bend) * _lowerOff[k];
        }

        // 死掉：圓盤往一邊倒、沉到地上，腳往中間收
        void Collapse(float dt, float ground)
        {
            if (_deadT == 0f)
            {
                if (_jumping) for (int k = 0; k < 3; k++) _feet[k] = _jTo[k];
                _stepping = false; _jumping = false;
                var c0 = Centroid();
                _deadAxis = Vector3.Cross(Flat(_feet[0] - c0).normalized, Vector3.up);
            }
            _deadT = Mathf.Min(1f, _deadT + dt / 0.7f);
            float e = Smooth(_deadT);
            var center = Centroid();
            var face = Quaternion.AngleAxis(75f * e, _deadAxis) * _face;
            _body.SetPositionAndRotation(new Vector3(center.x, ground + _height * (1f - 0.62f * e), center.z), face * _bodyRest);
            for (int k = 0; k < 3; k++)
            {
                var target = Vector3.Lerp(_feet[k], new Vector3(center.x, _feet[k].y, center.z), 0.35f * e);
                var pole = new Vector3(center.x, ground + _poleH, center.z) + Dir(Angle(_feet[k] - center)) * _poleR;
                Solve(k, target, pole);
                _foot[k].SetPositionAndRotation(target, Quaternion.AngleAxis(-_yaw[k], Vector3.up) * _footRest[k]);
            }
        }

        // ------------------------------------------------------------------ 工具
        Vector3 Centroid() => (_feet[0] + _feet[1] + _feet[2]) / 3f;

        // 落點的地面高度（往下打射線；自己、其他會動的東西不算）
        float GroundAt(Vector3 p, float fallback)
        {
            float top = 1.5f * Mathf.Max(0.5f, _scale);
            int n = Physics.RaycastNonAlloc(new Vector3(p.x, fallback + top, p.z), Vector3.down, _hits, top * 2f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue, y = fallback;
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.collider.attachedRigidbody || (_owner && h.transform.IsChildOf(_owner))) continue;
                if (h.collider.GetComponentInParent<DSTAnimal>()) continue;
                if (h.distance < best) { best = h.distance; y = h.point.y; }
            }
            return y;
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);
        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        static float Angle(Vector3 v) => Mathf.Atan2(v.z, v.x) * Mathf.Rad2Deg;
        static Vector3 Dir(float deg) => new Vector3(Mathf.Cos(deg * Mathf.Deg2Rad), 0f, Mathf.Sin(deg * Mathf.Deg2Rad));
        static Vector3 Perp(Vector3 v, Vector3 axis)
        {
            var n = axis.normalized;
            var r = v - n * Vector3.Dot(v, n);
            return r.sqrMagnitude > 1e-8f ? r.normalized : Vector3.Cross(n, Vector3.right).normalized;
        }

        // ------------------------------------------------------------------ 材質
        // 有烘好的材質（M_DST_Tripod：顏色、法線、遮罩，含骨甲裂紋）就用它，每個物種複製一份、帶一點基因的毛色；
        // 五個欄位共用同一張貼圖。沒有才退回純色。
        static readonly Dictionary<int, Material[]> _mats = new Dictionary<int, Material[]>();
        static readonly string[] SlotNames = { "Dermis", "Shell", "Chalk", "Horn", "Glass" };

        static Material[] MaterialsFor(DSTGenome g, Material baked, Material template, int slots)
        {
            if (_mats.TryGetValue(g.key, out var arr) && arr != null && arr.Length == slots && arr[0]) return arr;
            arr = new Material[slots];
            if (baked)
            {
                // 毛色只淡淡染一層（亮度拉回 1 附近，不然整隻變暗）
                var c = g.coat;
                float lum = Mathf.Max(0.05f, c.r * 0.3f + c.g * 0.59f + c.b * 0.11f);
                var hue = new Color(Mathf.Min(1f, c.r / lum * 0.9f), Mathf.Min(1f, c.g / lum * 0.9f), Mathf.Min(1f, c.b / lum * 0.9f));
                var tint = Color.Lerp(Color.white, hue, 0.15f);
                var m = new Material(baked) { name = "M_Tri_" + g.key };
                m.SetColor("_BaseColor", tint);
                for (int i = 0; i < slots; i++) arr[i] = m;
                _mats[g.key] = arr;
                return arr;
            }
            var shell = Color.Lerp(new Color(0.62f, 0.42f, 0.26f), g.coat, 0.45f);
            var dermis = Color.Lerp(new Color(0.21f, 0.27f, 0.25f), g.mark, 0.2f);
            var chalk = Color.Lerp(new Color(0.86f, 0.80f, 0.68f), g.belly, 0.3f);
            Color[] cols = { dermis, shell, chalk, new Color(0.12f, 0.10f, 0.08f), new Color(0.05f, 0.24f, 0.23f) };
            float[] smooth = { 0.35f, 0.25f, 0.2f, 0.45f, 0.9f };
            for (int i = 0; i < slots; i++)
            {
                int j = Mathf.Min(i, cols.Length - 1);
                var mm = new Material(template) { name = $"M_Tri_{SlotNames[j]}_{g.key}" };
                mm.SetTexture("_BaseColorMap", null);
                mm.SetColor("_BaseColor", cols[j]);
                mm.SetFloat("_Smoothness", smooth[j]);
                arr[i] = mm;
            }
            _mats[g.key] = arr;
            return arr;
        }
    }
}
