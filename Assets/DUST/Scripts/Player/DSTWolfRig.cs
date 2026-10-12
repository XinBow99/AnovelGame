using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;

namespace DUST.Player
{
    // 鬃狼模型的播放器：平常播走路（Animator Controller），上下車 / 騎乘時改播動作片段。
    // 動作片段是在「載具座標」裡做的：播的時候把模型掛到載具底下（anchor），開頭用 warp 從玩家實際站的地方平滑接到片段起點。
    public class DSTWolfRig : MonoBehaviour
    {
        public Transform model;                 // FBX 模型（有 Animator）
        public RuntimeAnimatorController locomotion;

        // FBX（Blender 匯出、bakeAxisConversion）面向 -Z；掛在玩家 / 載具底下都要轉 180°
        public static readonly Quaternion ModelFlip = Quaternion.Euler(0f, 180f, 0f);

        public bool InAction => _anchor != null;
        public Transform Hips { get; private set; }
        public Transform Head { get; private set; }
        public float GroundSpeed => _speed;

        Animator _animator;
        PlayableGraph _graph;
        AnimationMixerPlayable _mixer;          // 0 = 走路、1 / 2 = 動作（交替使用，才能交叉淡入）
        AnimatorControllerPlayable _loco;
        int _slot = 1;
        readonly float[] _fadeFrom = new float[3];
        float _fade = 1f, _fadeTime;
        int _fadeTo;
        Renderer[] _renderers;
        ShadowCastingMode _shadowMode = ShadowCastingMode.On;

        // 騎乘：循環 + 左右轉姿勢 + 插播的小動作
        AnimationMixerPlayable _ride;
        bool _rideActive;
        AnimationClipPlayable _fidget;
        int _fidgetIndex = -1;
        float _fidgetFade;

        // 動作定位
        Transform _anchor;
        Vector3 _warpPos, _startPos;
        float _warpYaw, _warpTime, _warpDur;
        Vector3 _lastPos;
        float _speed;

        void Awake()
        {
            if (!model) model = transform.Find("Model");
            _animator = model.GetComponent<Animator>();
            _renderers = model.GetComponentsInChildren<Renderer>(true);
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Hips") Hips = t;
                else if (t.name == "Head") Head = t;
            }
            BuildGraph();
            _lastPos = transform.position;
        }

        void OnDestroy()
        {
            if (_graph.IsValid()) _graph.Destroy();
            // 模型掛在車上時角色被刪掉（斷線）：模型也要一起清掉
            if (model && model.parent != transform) Destroy(model.gameObject);
        }

        void BuildGraph()
        {
            _graph = PlayableGraph.Create("DSTWolfRig");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            var output = AnimationPlayableOutput.Create(_graph, "Anim", _animator);
            _mixer = AnimationMixerPlayable.Create(_graph, 3);
            output.SetSourcePlayable(_mixer);
            if (locomotion)
            {
                _loco = AnimatorControllerPlayable.Create(_graph, locomotion);
                _graph.Connect(_loco, 0, _mixer, 0);
            }
            _mixer.SetInputWeight(0, 1f);
            _graph.Play();
        }

        void Update()
        {
            // 速度從位移估：自己和別人的角色都適用（別人的角色位置來自 NetworkTransform）
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            var d = transform.position - _lastPos; d.y = 0f;
            _lastPos = transform.position;
            float inst = d.magnitude / dt;
            if (inst > 20f) inst = 0f;                     // 傳送（Teleport）不算
            _speed = Mathf.Lerp(_speed, inst, 1f - Mathf.Exp(-10f * dt));
            if (_loco.IsValid()) _loco.SetFloat("Speed", InAction ? 0f : _speed);

            if (_fade < 1f)
            {
                _fade = Mathf.MoveTowards(_fade, 1f, dt / _fadeTime);
                float e = Mathf.SmoothStep(0f, 1f, _fade);
                for (int i = 0; i < 3; i++)
                    _mixer.SetInputWeight(i, Mathf.Lerp(_fadeFrom[i], i == _fadeTo ? 1f : 0f, e));
                if (_fade >= 1f)
                    for (int i = 1; i < 3; i++)
                        if (i != _fadeTo) Disconnect(i);    // 淡出完的片段釋放掉
            }
            UpdateFidget();
        }

        void LateUpdate()
        {
            if (!_anchor) return;
            // warp：一開始模型在玩家原本的位置，慢慢收斂到片段本身的位置
            _warpTime += Time.deltaTime;
            float w = _warpDur <= 0f ? 1f : Mathf.SmoothStep(0f, 1f, _warpTime / _warpDur);
            var rot = Quaternion.Euler(0f, _warpYaw * (1f - w), 0f);
            // 繞片段起點旋轉、再平移到玩家原本的位置
            model.localPosition = _startPos + _warpPos * (1f - w) - rot * _startPos;
            model.localRotation = rot * ModelFlip;
        }

        void FadeTo(int input, float time)
        {
            for (int i = 0; i < 3; i++) _fadeFrom[i] = _mixer.GetInputWeight(i);
            _fadeTo = input;
            _fade = 0f;
            _fadeTime = Mathf.Max(0.01f, time);
        }

        void Disconnect(int input)
        {
            var p = _mixer.GetInput(input);
            if (!p.IsValid()) return;
            _graph.Disconnect(_mixer, input);
            _mixer.SetInputWeight(input, 0f);
            p.Destroy();
            if (input == _rideSlot) { _rideActive = false; _fidgetIndex = -1; _rideSlot = -1; }
        }
        int _rideSlot = -1;

        int NextSlot()
        {
            _slot = _slot == 1 ? 2 : 1;
            Disconnect(_slot);
            return _slot;
        }

        // ------------------------------------------------------------------ 動作
        /// <summary>開始一個在 anchor 座標裡播的片段。fromPos / fromYaw 是玩家目前在 anchor 座標的位置與朝向，用來做 warp。</summary>
        public AnimationClipPlayable PlayAction(Transform anchor, DSTActionClip a, float speed, float fade, Vector3 fromPos, float fromYaw, float warp)
        {
            AttachTo(anchor);
            _startPos = a.startPos; _startPos.y = 0f;
            _warpPos = fromPos - a.startPos; _warpPos.y = 0f;
            _warpYaw = Mathf.DeltaAngle(a.startYaw, fromYaw);
            _warpTime = 0f;
            _warpDur = warp;
            int s = NextSlot();
            var p = AnimationClipPlayable.Create(_graph, a.clip);
            p.SetApplyFootIK(false);
            p.SetSpeed(speed);
            p.SetTime(0);
            p.SetDuration(a.clip.length);
            _graph.Connect(p, 0, _mixer, s);
            FadeTo(s, fade);
            return p;
        }

        /// <summary>原地播一個片段（聞一聞、豎耳朵），模型留在玩家身上</summary>
        public void PlayInPlace(AnimationClip clip, float fade, float speed = 1f)
        {
            if (_anchor) ReturnToLocomotion(0.01f);
            int s = NextSlot();
            var p = AnimationClipPlayable.Create(_graph, clip);
            p.SetApplyFootIK(false);
            p.SetSpeed(speed);
            _graph.Connect(p, 0, _mixer, s);
            FadeTo(s, fade);
        }

        /// <summary>騎乘循環（加上左右轉姿勢）。要跟曲柄同步的話之後呼叫 SetRidePhase。</summary>
        public void PlayRide(Transform anchor, DSTActionClip loop, DSTActionClip steerL, DSTActionClip steerR, float fade)
        {
            AttachTo(anchor);
            _warpDur = 0f; _warpPos = Vector3.zero; _warpYaw = 0f; _startPos = Vector3.zero;
            int s = NextSlot();
            _ride = AnimationMixerPlayable.Create(_graph, 3);
            var l = AnimationClipPlayable.Create(_graph, loop.clip);
            l.SetApplyFootIK(false);
            _graph.Connect(l, 0, _ride, 0);
            _ride.SetInputWeight(0, 1f);
            if (steerL != null) { var p = AnimationClipPlayable.Create(_graph, steerL.clip); p.SetSpeed(0); _graph.Connect(p, 0, _ride, 1); }
            if (steerR != null) { var p = AnimationClipPlayable.Create(_graph, steerR.clip); p.SetSpeed(0); _graph.Connect(p, 0, _ride, 2); }
            _graph.Connect(_ride, 0, _mixer, s);
            _rideActive = true;
            _rideSlot = s;
            _fidgetIndex = -1;
            FadeTo(s, fade);
        }

        /// <summary>steer：+1 = 左轉姿勢、-1 = 右轉姿勢</summary>
        public void SetRideSteer(float steer)
        {
            if (!_rideActive) return;
            float l = _ride.GetInput(1).IsValid() ? Mathf.Clamp01(steer) : 0f;
            float r = _ride.GetInput(2).IsValid() ? Mathf.Clamp01(-steer) : 0f;
            _ride.SetInputWeight(1, l);
            _ride.SetInputWeight(2, r);
            float f = _fidgetIndex >= 0 ? _ride.GetInputWeight(_fidgetIndex) : 0f;
            _ride.SetInputWeight(0, Mathf.Max(0f, 1f - l - r - f));
        }

        /// <summary>騎乘循環的時間（例如跟曲柄同步），0~1</summary>
        public void SetRidePhase(float normalized)
        {
            if (!_rideActive) return;
            var l = (AnimationClipPlayable)_ride.GetInput(0);
            l.SetSpeed(0);
            l.SetTime(Mathf.Repeat(normalized, 1f) * l.GetAnimationClip().length);
        }

        /// <summary>騎乘中插播一個小動作（甩頭、回頭看）；播完自己淡回循環</summary>
        public bool PlayFidget(DSTActionClip f, float fade = 0.25f)
        {
            if (!_rideActive || _fidgetIndex >= 0 || f == null || !f.clip) return false;
            _fidget = AnimationClipPlayable.Create(_graph, f.clip);
            _fidget.SetApplyFootIK(false);
            _fidget.SetDuration(f.clip.length);
            _fidgetIndex = _ride.AddInput(_fidget, 0, 0f);
            _fidgetFade = fade;
            return true;
        }

        public bool FidgetPlaying => _fidgetIndex >= 0;

        void UpdateFidget()
        {
            if (_fidgetIndex < 0 || !_rideActive) return;
            float t = (float)_fidget.GetTime(), len = _fidget.GetAnimationClip().length;
            float w = Mathf.Clamp01(Mathf.Min(t / _fidgetFade, (len - t) / _fidgetFade));
            _ride.SetInputWeight(_fidgetIndex, w);
            float steer = _ride.GetInputWeight(1) + _ride.GetInputWeight(2);
            _ride.SetInputWeight(0, Mathf.Max(0f, 1f - steer - w));
            if (t >= len)
            {
                _ride.SetInputWeight(_fidgetIndex, 0f);
                _graph.Disconnect(_ride, _fidgetIndex);
                _fidget.Destroy();
                _fidgetIndex = -1;
            }
        }

        // ------------------------------------------------------------------ 掛載
        void AttachTo(Transform anchor)
        {
            if (_anchor == anchor) return;
            _anchor = anchor;
            model.SetParent(anchor, true);
        }

        /// <summary>回到走路：模型掛回玩家身上</summary>
        public void ReturnToLocomotion(float fade)
        {
            _anchor = null;
            model.SetParent(transform, false);
            model.localPosition = Vector3.zero;
            model.localRotation = ModelFlip;
            _lastPos = transform.position;
            _speed = 0f;
            FadeTo(0, fade);
        }

        // ------------------------------------------------------------------ 外觀
        /// <summary>第一人稱時自己的身體只投影子（不然鏡頭在頭裡面）</summary>
        public void SetShadowOnly(bool shadowOnly)
        {
            var mode = shadowOnly ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
            if (mode == _shadowMode) return;
            _shadowMode = mode;
            foreach (var r in _renderers) if (r) r.shadowCastingMode = mode;
        }

        public void SetVisible(bool visible)
        {
            foreach (var r in _renderers) if (r) r.enabled = visible;
        }
    }
}
