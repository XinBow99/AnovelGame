using DUST.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace DUST.Animals
{
    // 程式生成物種的個體。連線只同步兩個東西：物種 key（外型、習性全部由它算出來）和目前姿勢（吃草 / 警戒 / 逃跑 / 死掉）。
    // 每台電腦自己用 key 從零件庫拼出身體、自己依移動速度播動畫。AI 照舊用 DSTAnimal（只在主機跑）。
    [RequireComponent(typeof(DSTAnimal))]
    public class DSTProcAnimal : NetworkBehaviour
    {
        public GameObject kit;                          // SK_DST_CreatureKit（Blender 零件庫）
        public RuntimeAnimatorController controller;    // AC_CreatureKit（Speed / Graze / Alert）
        public Material template;                       // HDRP/Lit 範本材質（舊的毛色貼圖模式）
        public Material skin;                           // M_DST_AlienSkin（程序外皮 shader）；有指定就用它，沒有才退回毛色貼圖
        public GameObject tripodModel;                  // SK_DST_Tripod（沙陶翻盤獸，三輻射三足）
        public Material tripodMaterial;                 // M_DST_Tripod（Blender 烘好的骨甲貼圖：顏色、法線、遮罩）
        DSTTripodGait _tripod;

        readonly NetworkVariable<int> _key = new NetworkVariable<int>();
        readonly NetworkVariable<byte> _pose = new NetworkVariable<byte>();
        // 演化：新物種的來源（root + 累積倍率），和這一隻的體型倍率（族群平均 × 個體差異）
        readonly NetworkVariable<int> _root = new NetworkVariable<int>();
        readonly NetworkVariable<Vector3> _lineage = new NetworkVariable<Vector3>(Vector3.one);
        readonly NetworkVariable<float> _scale = new NetworkVariable<float>(0f);
        int _pendingRoot;
        Vector3 _pendingLineage = Vector3.one;
        float _pendingScale;

        public DSTGenome Genome { get; private set; }
        public int Key => _key.Value;
        public CreaturePose Pose { get; private set; }

        DSTAnimal _animal;
        CreatureRig _rig;
        int _pendingKey;
        bool _built;
        float _nextLook;
        Vector3 _lastPos;
        float _speed, _deadT, _speedNorm, _walked;
        bool _leftFoot;
        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int GrazeId = Animator.StringToHash("Graze");
        static readonly int AlertId = Animator.StringToHash("Alert");

        // 說話（主機決定時機，種子傳給大家）
        AudioSource _voice;
        DSTAnimal.State _lastState;
        float _lastHealth = -1f;
        float _nextSpeak, _nextIdleMurmur;
        bool _saidDeath;
        const float HearRange = 14f;     // 有玩家在這麼近才說話

        void Awake() => _animal = GetComponent<DSTAnimal>();

        /// <summary>主機：Spawn 之前指定物種</summary>
        /// <summary>主機：Spawn 之前指定物種；scale = 這一隻的體型倍率（0 = 舊的固定小差異）</summary>
        public void SetKey(int key, float scale = 0f)
        {
            _pendingKey = key;
            var g = DSTGenome.Get(key);
            _pendingRoot = g.IsDerived ? g.root : 0;
            _pendingLineage = g.lineage;
            _pendingScale = scale;
            // 不連線（測試）時直接組
            if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsListening) Build(key, _pendingRoot, _pendingLineage, scale);
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                _key.Value = _pendingKey;
                _root.Value = _pendingRoot;
                _lineage.Value = _pendingLineage;
                _scale.Value = _pendingScale;
            }
            Build(_key.Value, _root.Value, _lineage.Value, _scale.Value);
        }

        /// <summary>這一隻的體型倍率（相對物種）</summary>
        public float Scale { get; private set; } = 1f;

        void Build(int key, int root, Vector3 lineage, float scale)
        {
            if (_built) return;
            _built = true;
            // 演化出來的新物種：客戶端沒有主機的族群資料，用 root + 倍率自己算出同一個基因
            Genome = root != 0 && root != key ? DSTGenome.Derive(key, root, lineage) : DSTGenome.Get(key);
            var snap = GetComponent<DSTGroundSnap>();
            if (!snap) snap = gameObject.AddComponent<DSTGroundSnap>();
            // 三輻射三足：另一個模型，骨頭全部由程式步態擺（DSTTripodGait）
            bool tri = Genome.Triradial && tripodModel;
            _rig = tri ? DSTTripodGait.Build(Genome, snap.EnsurePivot(), tripodModel, tripodMaterial, template)
                       : DSTCreatureAssembler.Build(Genome, snap.EnsurePivot(), kit, controller, template, skin);
            // 個體大小：主機給的（族群平均 × 個體差異）；沒給就用連線 ID 的小差異（每台電腦一樣）
            float s = scale > 0f ? scale : 1f + ((IsSpawned ? (int)(NetworkObjectId % 17) : 8) - 8) * 0.012f;
            Scale = s;
            _rig.root.localScale *= s;
            _rig.root.localPosition *= s;
            _rig.restPosition = _rig.root.localPosition;
            if (tri)
            {
                _tripod = _rig.root.GetComponent<DSTTripodGait>();
                _tripod.Init(transform);
            }
            _speedNorm = Mathf.Max(0.2f, Genome.size * Genome.legLen * s);
            _lastPos = transform.position;

            // 碰撞（揮拳打得到）與導航大小跟著體型
            var col = GetComponent<CapsuleCollider>();
            if (col)
            {
                col.direction = 2;
                col.center = new Vector3(0f, Genome.hipHeight * s, Genome.bodyLength * 0.15f * s);
                col.radius = Genome.bodyRadius * 1.1f * s;
                col.height = (Genome.bodyLength * 1.3f + Genome.neckLength) * s;
            }
            var agent = GetComponent<NavMeshAgent>();
            if (agent)
            {
                agent.radius = Mathf.Max(0.08f, Genome.bodyRadius * 1.2f * s);
                agent.height = (Genome.hipHeight + Genome.bodyRadius) * s;
            }
            _animal.species = Genome.Species;

            _voice = gameObject.AddComponent<AudioSource>();
            _voice.playOnAwake = false;
            _voice.spatialBlend = 1f;
            _voice.rolloffMode = AudioRolloffMode.Linear;
            _voice.minDistance = 1.5f;
            _voice.maxDistance = HearRange + 4f;
            _voice.volume = 0.8f;
            _nextIdleMurmur = Time.time + Random.Range(3f, 10f);
        }

        // ------------------------------------------------------------------ 說話
        void ThinkSpeech(float nearest)
        {
            var state = _animal.CurrentState;
            bool alive = _animal.IsAlive;
            if (!alive)
            {
                if (!_saidDeath && nearest < HearRange) Speak(SpeechMood.Death, force: true);
                _saidDeath = true;
                return;
            }
            // 被打
            if (_lastHealth >= 0f && _animal.Health < _lastHealth - 0.01f) Speak(SpeechMood.Hurt, force: true);
            _lastHealth = _animal.Health;

            if (state != _lastState)
            {
                if (nearest < HearRange)
                {
                    if (state == DSTAnimal.State.Alert && Random.value < 0.8f) Speak(SpeechMood.Notice);
                    else if (state == DSTAnimal.State.Flee && Random.value < 0.45f) Speak(SpeechMood.Flee);
                    else if (state == DSTAnimal.State.Sniff && Random.value < 0.3f) Speak(SpeechMood.Graze);
                }
                _lastState = state;
            }
            // 有人在附近：偶爾自言自語
            if (nearest < HearRange * 0.75f && Time.time > _nextIdleMurmur && (state == DSTAnimal.State.Idle || state == DSTAnimal.State.Wander))
            {
                _nextIdleMurmur = Time.time + Random.Range(8f, 18f);
                Speak(SpeechMood.Idle);
            }
        }

        void Speak(SpeechMood mood, bool force = false)
        {
            if (!force && Time.time < _nextSpeak) return;
            _nextSpeak = Time.time + 2.5f;
            int seed = Random.Range(int.MinValue, int.MaxValue);
            if (IsSpawned) SpeakRpc((byte)mood, seed);
            else Say((SpeechMood)mood, seed);
        }

        [Rpc(SendTo.Everyone)]
        void SpeakRpc(byte mood, int seed) => Say((SpeechMood)mood, seed);

        void Say(SpeechMood mood, int seed)
        {
            if (Genome == null) return;
            var voice = DSTVoice.For(Genome);
            var line = voice.Make(mood, seed, DSTEcology.Hour);
            float height = (Genome.hipHeight + Genome.bodyRadius + Genome.neckLength * 0.6f + Genome.headRadius) * 1.1f + 0.15f;
            DUST.UI.DSTSpeechBubbles.Show(transform, height, Genome.name, line.text, Genome.belly, mood == SpeechMood.Death ? 2.5f : 3.5f);
            // 聽不到的距離就不用合成聲音
            var listener = FindAnyObjectByType<AudioListener>();
            if (_voice && (!listener || Vector3.Distance(listener.transform.position, transform.position) < _voice.maxDistance))
            {
                if (_voice.clip) Destroy(_voice.clip);
                _voice.clip = voice.Synth(line, seed);
                _voice.pitch = 1f;
                _voice.Play();
            }
        }

        public override void OnDestroy()
        {
            if (_voice && _voice.clip) Destroy(_voice.clip);
            base.OnDestroy();
        }

        void Update()
        {
            if (!_built || _rig == null) return;
            if (IsServer || !IsSpawned)
            {
                var p = _animal.IsAlive ? _animal.CurrentState switch
                {
                    DSTAnimal.State.Sniff or DSTAnimal.State.Eat => CreaturePose.Graze,
                    DSTAnimal.State.Alert => CreaturePose.Alert,
                    DSTAnimal.State.Flee or DSTAnimal.State.Chase => CreaturePose.Flee,
                    _ => CreaturePose.Normal,
                } : CreaturePose.Dead;
                if (IsSpawned && _pose.Value != (byte)p) _pose.Value = (byte)p;
                Pose = p;
            }
            else Pose = (CreaturePose)_pose.Value;
            Animate();

            if (Time.time > _nextLook)
            {
                _nextLook = Time.time + 0.5f;
                float bd = float.MaxValue;
                foreach (var pl in DSTNetPlayer.All)
                    if (pl) bd = Mathf.Min(bd, (pl.transform.position - transform.position).sqrMagnitude);
                _nearest = bd < float.MaxValue ? Mathf.Sqrt(bd) : float.MaxValue;
            }
            if (IsServer || !IsSpawned) ThinkSpeech(_nearest);
        }

        // 速度從位移估（主機、客戶端都一樣）；動畫的速度參數換算成「零件庫原始大小」的速度，大隻的步伐自然變大
        void Animate()
        {
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            var d = transform.position - _lastPos; d.y = 0f;
            _lastPos = transform.position;
            float inst = d.magnitude / dt;
            if (inst > 20f) inst = 0f;
            // 足跡：每走一步留一個（左右交替）
            if (inst > 0f && Pose != CreaturePose.Dead)
            {
                _walked += d.magnitude;
                float stride = DSTTracks.StrideOf(Genome);
                if (_walked > stride)
                {
                    _walked -= stride;
                    _leftFoot = !_leftFoot;
                    DSTTracks.Step(transform.position, d.normalized, Genome, _leftFoot);
                }
            }
            _speed = Mathf.Lerp(_speed, inst, 1f - Mathf.Exp(-8f * dt));
            if (_tripod) { _tripod.Pose = Pose; return; }      // 三足：步態、警戒、死亡都在 DSTTripodGait
            var anim = _rig.animator;
            if (Pose == CreaturePose.Dead)
            {
                // 側倒（沒有死亡動畫）：動畫停在最後一格，整隻轉 90°
                if (anim.enabled) anim.speed = 0f;
                _deadT = Mathf.Min(1f, _deadT + dt / 0.6f);
                float e = Mathf.SmoothStep(0f, 1f, _deadT);
                _rig.root.localRotation = Quaternion.Euler(0f, 180f, 85f * e);
                _rig.root.localPosition = _rig.restPosition + new Vector3(0f, -Genome.hipHeight * 0.55f * e, 0f);
                return;
            }
            anim.speed = 1f;
            anim.SetFloat(SpeedId, _speed / _speedNorm, 0.1f, dt);
            anim.SetBool(GrazeId, Pose == CreaturePose.Graze);
            anim.SetBool(AlertId, Pose == CreaturePose.Alert);
        }
        float _nearest = float.MaxValue;
    }
}
