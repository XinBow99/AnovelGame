using UnityEngine;

namespace DUST.Animals
{
    public enum CreaturePose : byte { Normal, Graze, Alert, Flee, Dead }

    // 程式生成物種的動作（沒有動畫片段）：
    //   腳：依步態相位輪流抬腳，落腳點 = 站姿位置 + 往前預估半步，打射線貼地；兩節腿用 IK 對到落腳點。
    //   身體：跟著前後腳的高度前傾後仰、走路微微起伏。頭：吃草低頭、警戒抬頭看人。尾巴：左右擺。
    // 主機和客戶端都各自算（速度從位移估），所以不用同步動畫。
    public class DSTCreatureMotor : MonoBehaviour
    {
        public CreaturePose pose;
        public Transform lookTarget;

        DSTGenome _g;
        CreatureRig _rig;
        Vector3 _lastPos;
        Vector3 _vel;
        float _phase, _run, _deadT;
        bool _running;
        readonly LegState[] _legs = new LegState[4];
        readonly RaycastHit[] _hits = new RaycastHit[8];
        float _graze, _alert, _bobT;

        struct LegState
        {
            public Vector3 planted, from, to;
            public bool swinging;
            public float t, dur;
            public float lastLegPhase;
        }

        // 相位（0..1）：走 = 側對步順序、小跑 = 對角腳一起、跳 = 前後腳成對
        static readonly float[] WalkOff = { 0.25f, 0.75f, 0f, 0.5f };
        static readonly float[] TrotOff = { 0f, 0.5f, 0.5f, 0f };
        static readonly float[] BoundOff = { 0f, 0.06f, 0.5f, 0.56f };

        public void Init(DSTGenome g, CreatureRig rig)
        {
            _g = g;
            _rig = rig;
            _lastPos = transform.position;
            for (int i = 0; i < 4; i++)
            {
                var p = Ground(transform.TransformPoint(rig.legs[i].restFoot));
                _legs[i] = new LegState { planted = p };
            }
        }

        void LateUpdate()
        {
            if (_rig == null) return;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);

            // 速度：從位移估（主機、客戶端都適用）
            var d = (transform.position - _lastPos) / dt;
            _lastPos = transform.position;
            if (d.magnitude > 20f) d = Vector3.zero;            // 傳送
            _vel = Vector3.Lerp(_vel, new Vector3(d.x, 0f, d.z), 1f - Mathf.Exp(-8f * dt));
            float speed = _vel.magnitude;

            if (pose == CreaturePose.Dead) { Die(dt); return; }

            // 跑 / 走切換（有遲滯，不會在門檻上抖）
            if (!_running && speed > _g.walkSpeed * 1.8f) _running = true;
            else if (_running && speed < _g.walkSpeed * 1.3f) _running = false;
            _run = Mathf.MoveTowards(_run, _running ? 1f : 0f, dt * 3f);

            float stride = Mathf.Lerp(_g.strideWalk, _g.strideRun, _run);
            float duty = Mathf.Lerp(0.65f, _g.bounds ? 0.3f : 0.4f, _run);
            float freq = speed / Mathf.Max(0.05f, stride);
            bool moving = speed > 0.06f;
            if (moving) _phase = Mathf.Repeat(_phase + freq * dt, 1f);

            var offs = _running ? (_g.bounds ? BoundOff : TrotOff) : WalkOff;
            for (int i = 0; i < 4; i++) StepLeg(i, offs[i], duty, freq, moving, dt);

            Body(dt, speed);
            for (int i = 0; i < 4; i++) SolveLeg(i);
            HeadAndTail(dt, speed);
        }

        // ------------------------------------------------------------------ 腳
        Vector3 Neutral(int i) => transform.TransformPoint(_rig.legs[i].restFoot);

        void StepLeg(int i, float off, float duty, float freq, bool moving, float dt)
        {
            ref var s = ref _legs[i];
            if (s.swinging)
            {
                s.t += dt / Mathf.Max(0.05f, s.dur);
                if (s.t >= 1f) { s.swinging = false; s.planted = s.to; }
            }

            if (moving)
            {
                float lp = Mathf.Repeat(_phase + off, 1f);
                // 從「著地」進入「抬腳」的那一刻開始一步
                bool enteredSwing = lp < s.lastLegPhase;   // 相位繞回 0 = 新的一步
                s.lastLegPhase = lp;
                if (enteredSwing && !s.swinging)
                {
                    float swingTime = (1f - duty) / Mathf.Max(0.1f, freq);
                    float stanceTime = duty / Mathf.Max(0.1f, freq);
                    var target = Neutral(i) + _vel * (swingTime + stanceTime * 0.5f);
                    Begin(ref s, target, swingTime);
                }
            }
            else
            {
                // 停下來：離站姿太遠的腳一隻一隻收回來
                bool another = false;
                for (int j = 0; j < 4; j++) if (j != i && _legs[j].swinging) another = true;
                var n = Neutral(i);
                var flat = s.planted - n; flat.y = 0f;
                if (!s.swinging && !another && flat.magnitude > _g.strideWalk * 0.25f)
                    Begin(ref s, n, 0.22f);
            }
        }

        void Begin(ref LegState s, Vector3 target, float dur)
        {
            s.from = s.planted;
            s.to = Ground(target);
            s.t = 0f;
            s.dur = dur;
            s.swinging = true;
        }

        Vector3 FootPos(int i)
        {
            var s = _legs[i];
            if (!s.swinging) return s.planted;
            float t = Mathf.SmoothStep(0f, 1f, s.t);
            return Vector3.Lerp(s.from, s.to, t) + Vector3.up * Mathf.Sin(Mathf.PI * s.t) * _g.stepHeight * Mathf.Lerp(1f, 1.6f, _run);
        }

        Vector3 Ground(Vector3 p)
        {
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 1.5f, Vector3.down, _hits, 4f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; var g = new Vector3(p.x, transform.position.y, p.z);
            for (int k = 0; k < n; k++)
            {
                var h = _hits[k];
                if (h.transform == transform || h.transform.IsChildOf(transform)) continue;
                if (h.collider.attachedRigidbody) continue;
                if (h.distance < best) { best = h.distance; g = h.point; }
            }
            return g;
        }

        // 兩節腿 IK：髖 → 膝 → 腳。前腳的膝蓋往前彎、後腳往後（像跗關節）
        void SolveLeg(int i)
        {
            var leg = _rig.legs[i];
            var foot = FootPos(i) + Vector3.up * _g.legRadius * 0.4f;
            var H = leg.hip.position;
            float a = leg.upper, b = leg.lower;
            var toFoot = foot - H;
            float dist = Mathf.Clamp(toFoot.magnitude, Mathf.Abs(a - b) + 1e-3f, a + b - 1e-3f);
            var dir = toFoot.sqrMagnitude > 1e-6f ? toFoot.normalized : -_rig.body.up;
            float cosA = Mathf.Clamp((a * a + dist * dist - b * b) / (2f * a * dist), -1f, 1f);
            var pole = _rig.body.forward * (leg.front ? 1f : -1f);
            var perp = Vector3.ProjectOnPlane(pole, dir);
            if (perp.sqrMagnitude < 1e-6f) perp = _rig.body.forward;
            perp.Normalize();
            var K = H + (dir * cosA + perp * Mathf.Sqrt(1f - cosA * cosA)) * a;

            Aim(leg.hip, K - H);
            Aim(leg.knee, foot - K);
            leg.foot.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(_rig.body.forward, Vector3.up).normalized + Vector3.up * 1e-4f, Vector3.up);
        }

        void Aim(Transform t, Vector3 down)
        {
            if (down.sqrMagnitude < 1e-8f) return;
            var up = -down.normalized;
            var fwd = Vector3.ProjectOnPlane(_rig.body.forward, up);
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.ProjectOnPlane(_rig.body.up, up);
            t.rotation = Quaternion.LookRotation(fwd.normalized, up);
        }

        // ------------------------------------------------------------------ 身體
        void Body(float dt, float speed)
        {
            // 前後腳平均高度 → 身體高度與前傾
            float front = (transform.InverseTransformPoint(FootPos(0)).y + transform.InverseTransformPoint(FootPos(1)).y) * 0.5f;
            float hind = (transform.InverseTransformPoint(FootPos(2)).y + transform.InverseTransformPoint(FootPos(3)).y) * 0.5f;
            float span = Mathf.Abs(_rig.legs[0].restFoot.z - _rig.legs[2].restFoot.z);
            float pitch = -Mathf.Atan2(front - hind, Mathf.Max(0.05f, span)) * Mathf.Rad2Deg;
            float legLen = _g.upperLeg + _g.lowerLeg;
            _bobT += dt * (speed / Mathf.Max(0.05f, Mathf.Lerp(_g.strideWalk, _g.strideRun, _run))) * 2f;
            float bob = -Mathf.Abs(Mathf.Sin(_bobT * Mathf.PI)) * legLen * Mathf.Lerp(0.03f, 0.09f, _run) * Mathf.Clamp01(speed * 3f);
            float crouch = (_graze * 0.06f - _alert * 0.02f) * legLen;
            var want = _rig.bodyRest + new Vector3(0f, Mathf.Min(front, hind) * 0.5f + Mathf.Max(front, hind) * 0.5f + bob - crouch, 0f);
            _rig.body.localPosition = Vector3.Lerp(_rig.body.localPosition, want, 1f - Mathf.Exp(-15f * dt));
            // 跑的時候左右扭一點
            float roll = Mathf.Sin(_phase * Mathf.PI * 2f) * 4f * _run;
            _rig.body.localRotation = Quaternion.Slerp(_rig.body.localRotation, Quaternion.Euler(pitch, 0f, roll), 1f - Mathf.Exp(-10f * dt));
        }

        void HeadAndTail(float dt, float speed)
        {
            float k = 1f - Mathf.Exp(-5f * dt);
            _graze = Mathf.Lerp(_graze, pose == CreaturePose.Graze ? 1f : 0f, k);
            _alert = Mathf.Lerp(_alert, pose == CreaturePose.Alert ? 1f : 0f, k);

            // 低頭：脖子往下轉到嘴碰得到地面
            float down = _graze * (_g.neckPitch + 55f);
            float up = _alert * 18f;
            float nod = Mathf.Sin(_bobT * Mathf.PI) * 4f * Mathf.Clamp01(speed * 2f) * (1f - _run * 0.5f);
            float yaw = 0f;
            if (_alert > 0.05f && lookTarget)
            {
                var local = _rig.body.InverseTransformPoint(lookTarget.position);
                yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -70f, 70f) * _alert;
            }
            float runStretch = _run * 20f;   // 跑的時候脖子往前伸
            _rig.neck.localRotation = _rig.neckRest * Quaternion.Euler(down - up + nod + runStretch, yaw * 0.6f, 0f);
            _rig.head.localRotation = _rig.headRest * Quaternion.Euler(_graze * 25f - runStretch * 0.7f, yaw * 0.4f, 0f);

            // 尾巴：擺動，跑的時候翹起來平衡
            float t = Time.time;
            for (int i = 0; i < _rig.tail.Count; i++)
            {
                float sway = Mathf.Sin(t * Mathf.Lerp(1.5f, 6f, _run) + i * 0.7f + _g.key * 0.01f) * Mathf.Lerp(8f, 14f, _run);
                float lift = i == 0 ? _run * 18f + _alert * 12f : 0f;
                _rig.tail[i].localRotation = _rig.tailRest[i] * Quaternion.Euler(lift, sway, 0f);
            }
        }

        // ------------------------------------------------------------------ 死掉：側倒
        void Die(float dt)
        {
            _deadT = Mathf.Min(1f, _deadT + dt / 0.7f);
            float e = Mathf.SmoothStep(0f, 1f, _deadT);
            _rig.root.localRotation = Quaternion.Euler(0f, 0f, 88f * e);
            _rig.root.localPosition = new Vector3(-_g.hipHeight * 0.55f * e, (_g.bodyRadius - _g.hipHeight * 0.45f) * e, 0f);
            foreach (var leg in _rig.legs)
            {
                leg.hip.localRotation = Quaternion.Slerp(leg.hip.localRotation, Quaternion.Euler(leg.front ? -25f : 25f, 0f, 0f), e);
                leg.knee.localRotation = Quaternion.Slerp(leg.knee.localRotation, Quaternion.Euler(leg.front ? 20f : -20f, 0f, 0f), e);
            }
            _rig.neck.localRotation = Quaternion.Slerp(_rig.neck.localRotation, _rig.neckRest * Quaternion.Euler(_g.neckPitch * 0.8f, 0f, 0f), e);
        }
    }
}
