using System.Collections.Generic;
using DUST.Core;
using DUST.Player;
using DUST.Vehicles;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.AI;

namespace DUST.Animals
{
    // 動物 AI：Idle → Wander / Sniff，發現威脅 → Alert → Flee；被打 → 逃跑；血量歸零 → Dead
    // Wander 和 Flee 每一步往哪走由步伐選擇函數決定（DSTStepSelection：候選步 × exp(β·x) × 加權抽樣）
    // 能量（DSTMetabolism）：活著就消耗，草食在 Sniff 時吃草，肉食打獵：Stalk（接近）→ Chase（衝刺）→ 咬 → Eat（吃屍體）；
    // 能量用完會慢慢餓死。打獵的階段參考 MacNulty et al. 2007 的狼捕食行為（搜尋 → 接近 → 攻擊 → 捕獲）。
    // 連線：AI 只在主機上跑，位置 / 動畫用 NetworkTransform、NetworkAnimator 同步給大家
    [RequireComponent(typeof(NavMeshAgent))]
    public class DSTAnimal : NetworkBehaviour, IDSTDamageable
    {
        // 新狀態加在後面（DSTProcAnimal 依狀態決定姿勢）
        public enum State { Idle, Wander, Sniff, Alert, Flee, Dead, Stalk, Chase, Eat }

        public DSTSpeciesDefinition species;
        public float corpseLifetime = 10f;

        public State CurrentState { get; private set; }
        public float Health { get; private set; }
        public float Energy { get; private set; } = 1f;   // 0..1
        public float Meat { get; private set; }           // 屍體上還剩多少肉（公斤）
        public float Hunger => 1f - Energy;

        // 遷徙：有綁的話，閒逛的中心改成「群現在該在的位置」（DSTMigration 算的），落後太多就小跑追上
        public System.Func<Vector3> leash;
        public float leashRadius = 5f;
        public bool IsAlive => CurrentState != State.Dead && !_dead.Value;
        public event System.Action<DSTAnimal> Removed;
        /// <summary>死掉的那一刻（主機）：killer = 打死牠的東西（玩家、車、捕食者）；null = 餓死。遠處族群和事件紀錄用</summary>
        public static event System.Action<DSTAnimal, Transform> Died;
        /// <summary>屬於哪一群（發條遷徙放出來的才有）</summary>
        public DSTBand Band { get; set; }

        // 群居用：找同物種的同伴（只在主機上用到）
        static readonly List<DSTAnimal> _all = new List<DSTAnimal>();
        public static IReadOnlyList<DSTAnimal> All => _all;
        void OnEnable() => _all.Add(this);
        void OnDisable() => _all.Remove(this);

        NavMeshAgent _agent;
        Animator _anim;
        NetworkAnimator _netAnim;
        readonly NetworkVariable<bool> _dead = new NetworkVariable<bool>(false);
        float _stateUntil;
        float _nextRepath;
        Transform _threat;
        bool _panicked;
        Vector3 _home;                 // 沒綁遷徙群的個體：出生點當作活動範圍的中心
        DSTProcAnimal _proc;

        // 打獵：獵物可以是動物（活的或屍體），也可以是玩家（鬃狼，約 23 kg，跟動物用同一套「吃得下嗎」的規則）
        DSTAnimal _target;             // 正在追的獵物，或要去吃的屍體
        DSTNetPlayer _targetPlayer;
        float _nextHunt, _nextLookForFood, _nextBite, _chaseUntil, _chaseStartDist;
        float _keepCorpseUntil;

        // 最近一次的步伐選擇（/ssf 除錯畫面用）
        public readonly DSTStepSelection.Decision LastStep = new DSTStepSelection.Decision();

        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int SniffId = Animator.StringToHash("Sniff");
        static readonly int DeadId = Animator.StringToHash("Dead");
        static readonly int AlertId = Animator.StringToHash("Alert");
        bool _hasAlertParam;

        // 程式生成的物種才有基因；手做的（遺跡二的 Prefab）當作 10 kg 的草食動物
        public DSTGenome Genome => _proc ? _proc.Genome : null;
        public float Mass => Genome != null ? Genome.Mass * _proc.Scale * _proc.Scale * _proc.Scale : 10f;

        /// <summary>這一隻的特徵相對族群平均的偏差（對數）：x 大小、y 跑速、z 警覺距離。主機；死掉時拿來算天擇</summary>
        public Vector3 TraitDev { get; private set; }
        bool _ownSpecies;

        /// <summary>主機，Spawn 之後、Start 之前：族群平均（演化的偏移）+ 個體差異，改這一隻的跑速和警覺距離</summary>
        public void SetTraits(Vector3 populationMean, Vector3 dev)
        {
            TraitDev = dev;
            float speed = Mathf.Exp(populationMean.y + dev.y), notice = Mathf.Exp(populationMean.z + dev.z);
            if (!species) return;
            species = Instantiate(species);
            species.hideFlags = HideFlags.DontSave;
            _ownSpecies = true;
            species.walkSpeed *= speed;
            species.runSpeed *= speed;
            species.acceleration *= speed;
            species.noticeRadius *= notice;
            species.fleeRadius *= notice;
            species.calmRadius *= notice;
        }

        // 同一個物種：程式生成的比基因（每隻的 species 是自己的複本），手做的比 species 資產
        bool SameSpecies(DSTAnimal o) => Genome != null ? o.Genome == Genome : o.species == species;
        public bool IsCarnivore => Genome != null && Genome.Carnivore;
        public DSTAnimal Target => _target;
        public string TargetName => _target ? (_target.Genome != null ? _target.Genome.name : _target.name) + (_target.IsAlive ? "" : $"（屍體 {_target.Meat:0.0} kg）")
                                  : _targetPlayer ? _targetPlayer.DisplayName : null;
        float Big => Mathf.InverseLerp(5f, 134f, Mass);

        Transform TargetT => _target ? _target.transform : _targetPlayer ? _targetPlayer.transform : null;
        bool TargetAlive => _target ? _target.IsAlive : _targetPlayer && Huntable(_targetPlayer);
        bool TargetFleeing => _target ? _target.CurrentState == State.Flee : _targetPlayer && _targetPlayer.IsSprinting;   // 玩家跑起來就會引發追擊（Mattson et al. 2011）
        float TargetMeat => _target ? _target.Meat : 0f;
        Vector3 TargetVelocity => _target ? _target.Velocity : Vector3.zero;
        float TargetRadius => _target ? (_target._agent ? _target._agent.radius : 0.2f) : 0.35f;
        void ClearTarget() { _target = null; _targetPlayer = null; }

        // 玩家能不能當獵物：活著、不在車上、不是創造模式，而且旁邊沒有別的玩家
        // （Mattson et al. 2011：有人在旁邊介入，美洲獅攻擊致死的機率低很多）
        static bool Huntable(DSTNetPlayer p)
        {
            if (!p || !p.IsSpawned || !p.IsAlive || p.InVehicle || p.IsCreative) return false;
            foreach (var o in DSTNetPlayer.All)
                if (o && o != p && o.IsAlive && (o.transform.position - p.transform.position).sqrMagnitude < 36f) return false;
            return true;
        }

        bool CanEatPlayer => IsCarnivore && DSTGenome.CanEat(Genome, Mass, -1, DSTNetPlayer.BodyMass);

        void Awake()
        {
            // 外觀貼地（導航網格比真正的地面高低差幾公分）
            if (!GetComponent<DSTGroundSnap>()) gameObject.AddComponent<DSTGroundSnap>();
            _agent = GetComponent<NavMeshAgent>();
            _anim = GetComponentInChildren<Animator>();
            _netAnim = GetComponentInChildren<NetworkAnimator>();
            _proc = GetComponent<DSTProcAnimal>();
            if (_anim)
                foreach (var p in _anim.parameters)
                    if (p.nameHash == AlertId) _hasAlertParam = true;
        }

        // 沒連線時（測試場景）照樣自己跑
        bool Authority => !IsSpawned || IsServer;

        public override void OnNetworkSpawn()
        {
            if (!IsServer) _agent.enabled = false;   // 客戶端只顯示，位置聽主機的
        }

        void Start()
        {
            if (!Authority) return;
            Health = species.maxHealth;
            if (!_energySet) Energy = Random.Range(0.45f, 1f);        // 有的剛吃飽、有的已經有點餓（生成時有指定就照指定）
            _agent.angularSpeed = species.turnSpeedDeg;
            _agent.acceleration = species.acceleration;
            _home = transform.position;
            Enter(State.Idle);
        }

        public override void OnDestroy()
        {
            _all.Remove(this);
            Removed?.Invoke(this);
            if (_ownSpecies && species) Destroy(species);
            base.OnDestroy();
        }

        void Update()
        {
            if (!Authority || CurrentState == State.Dead) return;
            Metabolize();
            if (CurrentState == State.Dead) return;
            Sense();
            Think();
            if (_anim)
            {
                _anim.SetFloat(SpeedId, _agent.velocity.magnitude, 0.12f, Time.deltaTime);
                _anim.SetBool(SniffId, CurrentState == State.Sniff || CurrentState == State.Eat);
                if (_hasAlertParam) _anim.SetBool(AlertId, CurrentState == State.Alert);
            }
        }

        // ------------------------------------------------------------------ 能量
        void Metabolize()
        {
            float h = DSTMetabolism.Hours(Time.deltaTime);
            float speed = _agent.enabled ? _agent.velocity.magnitude : 0f;
            // 三足站著不動時省 2 成：三點站立不用出力（Thomson 2019；倍數是建議）
            float rest = Genome != null && Genome.Triradial && speed < 0.1f ? 0.8f : 1f;
            Energy -= h / DSTMetabolism.EnduranceHours(Mass) * DSTMetabolism.ActivityMultiplier(speed, species.walkSpeed, species.runSpeed) * rest;

            // 草食：低頭（Sniff）就是在吃草。一隻 23 kg 的動物從空腹吃到飽，會吃掉一整格滿草的 1/4【建議】
            if (CurrentState == State.Sniff && !IsCarnivore && Energy < 1f)
            {
                float perEnergy = Mass / 23f * 0.25f;
                float want = h / DSTMetabolism.GrazeHoursToFull * DSTMetabolism.Forage(transform.position);
                Energy += DSTMetabolism.Eat(transform.position, want * perEnergy) / perEnergy;
            }
            // 肉食：吃屍體
            if (CurrentState == State.Eat && _target && _target.Meat > 0f)
            {
                float meal = Mass * DSTMetabolism.MealFraction;
                float bite = Mathf.Min(_target.Meat, meal / DSTMetabolism.MealHours * h);
                _target.Meat -= bite;
                _target.KeepCorpse(_target.Meat > 0f ? 5f : 1.5f);
                Energy += bite / meal;
            }
            Energy = Mathf.Min(Energy, 1f);

            // 能量用完：開始掉血，餓死
            if (Energy <= 0f)
            {
                Energy = 0f;
                Health -= species.maxHealth * h / DSTMetabolism.StarveHours;
                if (Health <= 0f) Die(null);
            }
        }

        // ------------------------------------------------------------------ 感知
        void Sense()
        {
            // 威脅有兩種：玩家 / 車，和會吃自己的肉食動物。挑「最該注意」的那個（距離 ÷ 發現距離最小）
            Transform t = null; float dist = 0f, mul = 1f, best = float.MaxValue;
            if (species.temperament != Temperament.Passive || _panicked)
            {
                var pt = DSTThreats.Nearest(transform.position, out float pd, out var kind);
                // 餓了、又吃得下玩家的肉食動物：走路的玩家不是威脅，是獵物（車子還是怕）
                if (pt && Energy < DSTMetabolism.HungryBelow && CanEatPlayer && !_panicked &&
                    kind != ThreatKind.MovingVehicle && kind != ThreatKind.IdleVehicle) pt = null;
                if (pt)
                {
                    float m = kind switch
                    {
                        ThreatKind.Crouching => species.crouchNoticeMultiplier,
                        ThreatKind.Sprinting => species.sprintNoticeMultiplier,
                        ThreatKind.MovingVehicle => species.vehicleNoticeMultiplier,
                        _ => 1f,
                    };
                    best = pd / m; t = pt; dist = pd; mul = m;
                }
            }
            var pred = NearestPredator(out float dd);
            if (pred)
            {
                // 偷偷接近的捕食者跟蹲著的玩家一樣難發現；衝過來的跟跑步的一樣明顯
                float m = pred.CurrentState == State.Stalk ? species.crouchNoticeMultiplier
                        : pred.CurrentState == State.Chase ? species.sprintNoticeMultiplier : 1f;
                if (dd / m < best) { best = dd / m; t = pred.transform; dist = dd; mul = m; }
            }

            if (t != null)
            {
                float notice = species.noticeRadius * mul;
                float flee = species.fleeRadius * Mathf.Max(1f, mul * 0.8f);
                bool seen = dist < notice && HasLineOfSight(t);
                bool heard = dist < flee * 0.6f;   // 貼太近，就算隔著箱子也知道

                switch (CurrentState)
                {
                    case State.Idle:
                    case State.Wander:
                    case State.Sniff:
                        if ((seen || heard) && dist < flee) { StartFlee(t); return; }
                        if (seen) { _threat = t; Enter(State.Alert); return; }
                        break;
                    case State.Alert:
                        if (dist < flee) { StartFlee(t); return; }
                        break;
                    case State.Flee:
                        _threat = t;
                        return;
                    case State.Stalk:
                    case State.Eat:
                        // 打獵 / 吃到一半，被更大的東西靠近：放棄獵物先跑
                        if ((seen || heard) && dist < flee && t != TargetT) { ClearTarget(); StartFlee(t); return; }
                        break;
                }
            }

            // 肉食動物餓了：找屍體（用聞的）或看得到的獵物
            if (IsCarnivore && Energy < DSTMetabolism.HungryBelow && Time.time > _nextHunt && Time.time > _nextLookForFood &&
                (CurrentState == State.Idle || CurrentState == State.Wander || CurrentState == State.Sniff))
            {
                _nextLookForFood = Time.time + 0.5f;
                if (FindFood(species.noticeRadius * 2.5f, true, out _target, out _targetPlayer, out _)) Enter(State.Stalk);
            }
        }

        bool HasLineOfSight(Transform t)
        {
            Vector3 eye = transform.position + Vector3.up * 0.4f;
            Vector3 target = t.position + Vector3.up * 0.5f;
            if (!Physics.Linecast(eye, target, out var hit, ~0, QueryTriggerInteraction.Ignore)) return true;
            return hit.transform == t || hit.transform.IsChildOf(t) || hit.transform.IsChildOf(transform);
        }

        // 會吃我的、最近的肉食動物
        DSTAnimal NearestPredator(out float dist)
        {
            dist = float.MaxValue;
            DSTAnimal best = null;
            int key = Genome != null ? Genome.key : 0;
            float range = species.noticeRadius * 2f;
            foreach (var o in _all)
            {
                if (o == this || !o.IsAlive || !o.IsCarnivore) continue;
                if (!DSTGenome.CanEat(o.Genome, o.Mass, key, Mass)) continue;
                float d = Vector3.Distance(transform.position, o.transform.position);
                if (d < range && d < dist) { dist = d; best = o; }
            }
            return best;
        }

        // 找吃的：有肉的屍體（不用看到）優先，再來是最近的活獵物（動物或玩家）
        bool FindFood(float range, bool needSight, out DSTAnimal animal, out DSTNetPlayer player, out Vector3 at)
        {
            DSTAnimal carcass = null, prey = null;
            player = null;
            float bc = range * 1.5f, bp = range;
            foreach (var o in _all)
            {
                if (o == this) continue;
                float d = Vector3.Distance(transform.position, o.transform.position);
                if (!o.IsAlive)
                {
                    if (o.Meat > 0f && d < bc) { bc = d; carcass = o; }
                    continue;
                }
                if (d >= bp || !DSTGenome.CanEat(Genome, Mass, o.Genome != null ? o.Genome.key : 0, o.Mass)) continue;
                if (needSight && !HasLineOfSight(o.transform)) continue;
                bp = d; prey = o;
            }
            if (!carcass && CanEatPlayer)
                foreach (var p in DSTNetPlayer.All)
                {
                    if (!Huntable(p)) continue;
                    float d = Vector3.Distance(transform.position, p.transform.position);
                    if (d >= bp || (needSight && !HasLineOfSight(p.transform))) continue;
                    bp = d; prey = null; player = p;
                }
            animal = carcass ? carcass : prey;
            at = animal ? animal.transform.position : player ? player.transform.position : default;
            return animal || player;
        }

        // ------------------------------------------------------------------ 決策
        void Think()
        {
            switch (CurrentState)
            {
                case State.Idle:
                    // 草食動物越餓越常低頭吃草
                    float graze = species.sniffChance + (IsCarnivore ? 0f : Hunger * 0.5f);
                    if (Time.time > _stateUntil || Strayed()) Enter(Random.value < graze && !Strayed() ? State.Sniff : State.Wander);
                    break;
                case State.Sniff:
                    if (Time.time > _stateUntil || Strayed()) Enter(State.Idle);
                    break;
                case State.Wander:
                    if (Time.time > _stateUntil) Enter(State.Idle);
                    else if (Arrived() && !TakeStep()) Enter(State.Idle);   // 走完一步接下一步
                    break;
                case State.Alert:
                    if (_threat) FaceTowards(_threat.position);
                    if (Time.time > _stateUntil)
                    {
                        float d = _threat ? Vector3.Distance(transform.position, _threat.position) : 999f;
                        if (d < species.noticeRadius * 0.75f) StartFlee(_threat);
                        else Enter(State.Idle);
                    }
                    break;
                case State.Flee:
                    float dist = _threat ? Vector3.Distance(transform.position, _threat.position) : 999f;
                    if (dist > species.calmRadius && Time.time > _stateUntil) { _panicked = false; Enter(State.Idle); }
                    else if (Time.time > _nextRepath || Arrived()) PickFleeDestination();
                    break;
                case State.Stalk: ThinkStalk(); break;
                case State.Chase: ThinkChase(); break;
                case State.Eat:
                    if (_target) FaceTowards(_target.transform.position);
                    if (!_target || _target.Meat <= 0f || Energy >= 0.98f) { ClearTarget(); _nextHunt = Time.time + 10f; Enter(State.Idle); }
                    break;
            }
        }

        // ------------------------------------------------------------------ 打獵
        float Reach => _agent.radius + TargetRadius + 0.6f;
        float AttackDistance => Mathf.Lerp(4f, 10f, Big);     // 離這麼近才開始衝【建議】
        float ChaseSeconds => Mathf.Lerp(6f, 10f, Big);       // 衝多久追不到就放棄【建議】

        // 接近：走過去（慢一點、不出聲）。屍體就直接走去吃
        void ThinkStalk()
        {
            var t = TargetT;
            if (!t || (!TargetAlive && TargetMeat <= 0f)) { GiveUp(); return; }
            float d = Vector3.Distance(transform.position, t.position);
            if (!TargetAlive)
            {
                if (d < Reach) { Enter(State.Eat); return; }
                _agent.speed = species.walkSpeed * 1.6f;
            }
            else
            {
                if (d < AttackDistance || TargetFleeing) { BeginChase(true); return; }
                if (d > species.noticeRadius * 4f || Time.time > _stateUntil) { GiveUp(); return; }
                _agent.speed = species.walkSpeed * 1.3f;
            }
            if (Time.time > _nextRepath) { _nextRepath = Time.time + 0.5f; Go(t.position); }
        }

        void BeginChase(bool callPack)
        {
            Enter(State.Chase);
            // 成群的一起追（MacNulty et al. 2011：狼群 4 隻以內，越多隻成功率越高）
            if (!callPack || species.herdCohesion <= 0f) return;
            foreach (var o in _all)
                if (o != this && SameSpecies(o) && o.IsAlive && o.CurrentState != State.Chase && o.CurrentState != State.Flee &&
                    (o.transform.position - transform.position).sqrMagnitude < 400f)
                {
                    o._target = _target;
                    o._targetPlayer = _targetPlayer;
                    o.BeginChase(false);
                }
        }

        // 衝刺：全速追，追到就咬
        void ThinkChase()
        {
            var t = TargetT;
            if (!t) { GiveUp(); return; }
            float d = Vector3.Distance(transform.position, t.position);
            if (!TargetAlive)
            {
                if (TargetMeat <= 0f) { GiveUp(); return; }
                Enter(State.Stalk);       // 倒了：走過去吃
                return;
            }
            if (Time.time > _chaseUntil || d > _chaseStartDist * 2f + 6f) { GiveUp(); return; }
            if (d < Reach && Time.time > _nextBite)
            {
                _nextBite = Time.time + 0.8f;
                // 咬一口的傷害跟自己的體型走：大型的幾口就能放倒獵物【建議】
                IDSTDamageable victim = _target ? (IDSTDamageable)_target : _targetPlayer;
                victim.TakeDamage(species.maxHealth * 0.3f, transform);
            }
            if (Time.time > _nextRepath)
            {
                _nextRepath = Time.time + 0.2f;
                // 往獵物前面一點的地方跑（預判），停在咬得到的距離（不要疊到獵物身上）
                var aim = t.position + TargetVelocity * 0.4f;
                var back = transform.position - aim; back.y = 0f;
                float keep = Reach * 0.7f;
                Go(back.sqrMagnitude > keep * keep ? aim + back.normalized * keep : transform.position);
            }
        }

        void GiveUp()
        {
            ClearTarget();
            _nextHunt = Time.time + 15f;
            Enter(State.Idle);
        }

        Vector3 Velocity => _agent && _agent.enabled ? _agent.velocity : Vector3.zero;

        public void KeepCorpse(float seconds) => _keepCorpseUntil = Mathf.Max(_keepCorpseUntil, Time.time + seconds);

        /// <summary>/hunger 測試用：直接設定能量（只在主機有效）</summary>
        public void SetEnergy(float e) { if (Authority && IsAlive) { Energy = Mathf.Clamp01(e); _energySet = true; } }
        bool _energySet;

        // ------------------------------------------------------------------ 狀態切換
        void Enter(State s)
        {
            CurrentState = s;
            switch (s)
            {
                case State.Idle:
                    Stop();
                    _stateUntil = Time.time + Random.Range(species.idleTime.x, species.idleTime.y);
                    break;
                case State.Sniff:
                    Stop();
                    // 餓的草食動物一次吃比較久
                    _stateUntil = Time.time + Random.Range(species.sniffTime.x, species.sniffTime.y) * (IsCarnivore ? 1f : 1f + 2f * Hunger);
                    break;
                case State.Wander:
                    // 一段閒逛 = 連續走幾步（每步幾秒），然後停下來
                    _stateUntil = Time.time + Random.Range(5f, 14f);
                    if (!TakeStep()) { Enter(State.Idle); return; }
                    break;
                case State.Alert:
                    Stop();
                    _stateUntil = Time.time + Random.Range(species.alertTime.x, species.alertTime.y);
                    break;
                case State.Flee:
                    _agent.speed = species.runSpeed * (_panicked ? 1.1f : 1f);
                    _stateUntil = Time.time + 2f;
                    PickFleeDestination();
                    break;
                case State.Stalk:
                    _stateUntil = Time.time + 30f;
                    _nextRepath = 0f;
                    break;
                case State.Chase:
                    _agent.speed = species.runSpeed;
                    _chaseUntil = Time.time + ChaseSeconds;
                    _chaseStartDist = TargetT ? Vector3.Distance(transform.position, TargetT.position) : 0f;
                    _nextRepath = 0f;
                    break;
                case State.Eat:
                    Stop();
                    break;
            }
        }

        void StartFlee(Transform from, bool alarmHerd = true)
        {
            _threat = from;
            Enter(State.Flee);
            if (alarmHerd && species.herdCohesion > 0f) AlarmHerd(from);
        }

        // 一隻跑，附近的同伴跟著跑（同伴不會再往外傳，避免連鎖到整張地圖）
        void AlarmHerd(Transform threat)
        {
            float r = 6f + species.herdCohesion * 8f;
            foreach (var o in _all)
                if (o != this && SameSpecies(o) && o.IsAlive && o.CurrentState != State.Flee && o.CurrentState != State.Chase &&
                    (o.transform.position - transform.position).sqrMagnitude < r * r)
                    o.StartFlee(threat, false);
        }

        bool HerdCenter(out Vector3 center)
        {
            center = Vector3.zero;
            int n = 0;
            foreach (var o in _all)
            {
                if (o == this || !SameSpecies(o) || !o.IsAlive) continue;
                if ((o.transform.position - transform.position).sqrMagnitude > 400f) continue;   // 20 m 內
                center += o.transform.position;
                n++;
            }
            if (n == 0) return false;
            center /= n;
            return true;
        }

        // ------------------------------------------------------------------ 步伐選擇
        // 閒逛的一步：慢慢走、大致往前；被群拉住或往同伴靠；餓的往草多的地方、肉食往聞到的獵物
        bool TakeStep()
        {
            if (!_agent.isOnNavMesh) return false;
            _agent.speed = species.walkSpeed;
            float mean = Mathf.Max(1.5f, species.walkSpeed * 4f);   // 一步約 4 秒
            var c = BaseContext(0f, null);
            if (leash != null && Vector3.Distance(transform.position, c.home) > leashRadius * 3f)
            {
                // 落後群太多：小跑、步子大一點
                _agent.speed = Mathf.Min(species.runSpeed * 0.6f, species.walkSpeed * 2.2f);
                mean = _agent.speed * 4f;
            }
            var k = new DSTStepSelection.Kernel { candidates = 20, meanStep = mean, shape = 2f, kappa = 1f, heading = transform.forward };
            if (!DSTStepSelection.Choose(transform.position, k, c, LastStep, out var end)) return false;
            Go(end);
            return true;
        }

        // 逃跑的一步：跑、步子大、候選散開（往哪跑主要由「遠離」「躲藏」決定）
        void PickFleeDestination()
        {
            _nextRepath = Time.time + 1.0f;
            if (!_agent.isOnNavMesh) return;
            Vector3 away = _threat ? transform.position - _threat.position : transform.forward;
            away.y = 0;
            if (away.sqrMagnitude < 0.01f) away = transform.forward;
            var c = BaseContext(_panicked ? 1.4f : 1f, _threat);
            var k = new DSTStepSelection.Kernel { candidates = 28, meanStep = species.fleeDistance * 0.6f, shape = 3f, kappa = 0.3f, heading = away };
            if (DSTStepSelection.Choose(transform.position, k, c, LastStep, out var end)) Go(end);
        }

        DSTStepSelection.Context BaseContext(float fear, Transform threat)
        {
            var c = new DSTStepSelection.Context { self = transform, fear = fear, threat = threat, hunger = Hunger, grazer = !IsCarnivore };
            if (leash != null) { c.hasHome = true; c.home = leash(); c.homeRadius = leashRadius; }
            else
            {
                c.hasHome = true; c.home = _home; c.homeRadius = species.wanderRadius * 3f;
                if (species.herdCohesion > 0f && HerdCenter(out var hc))
                {
                    c.hasHerd = true; c.herd = hc; c.cohesion = species.herdCohesion;
                    c.herdRadius = 2f + species.wanderRadius * (1f - species.herdCohesion) * 0.5f;
                }
            }
            var g = Genome;
            if (g != null) c.habitat = g.terrain == Terrain.Rocky ? 1f : g.terrain == Terrain.Open ? -1f : 0f;
            // 餓的肉食動物：往 60 m 內聞得到的獵物 / 屍體的方向走（不用看到）
            if (IsCarnivore && Energy < DSTMetabolism.HungryBelow && fear <= 0f)
            {
                if (FindFood(60f, false, out _, out _, out var scent)) { c.hasPrey = true; c.prey = scent; }
            }
            return c;
        }

        // ------------------------------------------------------------------ 受傷 / 死亡
        public void TakeDamage(float amount, Transform source)
        {
            if (!Authority || !IsAlive) return;
            Health -= amount;
            if (Health <= 0f) { Die(source); return; }
            _panicked = true;
            ClearTarget();
            StartFlee(source);
        }

        void Die(Transform killer)
        {
            CurrentState = State.Dead;
            Died?.Invoke(this, killer);
            if (IsSpawned) _dead.Value = true;
            if (_agent.isOnNavMesh) _agent.isStopped = true;
            _agent.enabled = false;
            // 屍體可以吃的部分：體重的 6 成【建議】；餓死的比較瘦，只剩一半
            Meat = Mass * (Energy > 0f ? 0.6f : 0.3f);
            // 留久一點，讓肉食動物有時間聞過來（有東西在吃就會一直延長）
            KeepCorpse(Mathf.Max(corpseLifetime, 40f));
            if (_anim)
            {
                _anim.SetFloat(SpeedId, 0f);
                // Trigger 要經過 NetworkAnimator 才會同步到客戶端
                if (_netAnim && IsSpawned) _netAnim.SetTrigger(DeadId);
                else _anim.SetTrigger(DeadId);
            }
            StartCoroutine(RemoveWhenDone());
        }

        System.Collections.IEnumerator RemoveWhenDone()
        {
            while (Time.time < _keepCorpseUntil) yield return new WaitForSeconds(0.5f);
            Remove();
        }

        // 連線中要用 Despawn，客戶端才會一起消失
        public void Remove()
        {
            if (IsSpawned) NetworkObject.Despawn(true);
            else Destroy(gameObject);
        }

        // ------------------------------------------------------------------ 工具
        void Go(Vector3 p)
        {
            _agent.isStopped = false;
            _agent.SetDestination(p);
        }

        void Stop()
        {
            if (_agent.isOnNavMesh) { _agent.isStopped = true; _agent.ResetPath(); }
        }

        bool Strayed() => leash != null && (leash() - transform.position).sqrMagnitude > leashRadius * leashRadius * 2.25f;

        bool Arrived() => !_agent.pathPending && _agent.remainingDistance <= _agent.stoppingDistance + 0.2f;

        void FaceTowards(Vector3 p)
        {
            Vector3 d = p - transform.position; d.y = 0;
            if (d.sqrMagnitude < 0.01f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(d), species.turnSpeedDeg * 0.5f * Time.deltaTime);
        }

        public static bool RandomPoint(Vector3 center, float radius, out Vector3 result)
        {
            for (int i = 0; i < 8; i++)
            {
                Vector2 r = Random.insideUnitCircle * radius;
                if (NavMesh.SamplePosition(center + new Vector3(r.x, 0, r.y), out var hit, 2f, NavMesh.AllAreas))
                {
                    result = hit.position;
                    return true;
                }
            }
            result = center;
            return false;
        }
    }

    public enum ThreatKind { Walking, Crouching, Sprinting, IdleVehicle, MovingVehicle }

    // 威脅來源：走路的玩家、有人駕駛的車（每個 frame 只掃描一次場景）
    public static class DSTThreats
    {
        static int _frame = -1;
        static readonly List<(Transform t, ThreatKind kind)> _cache = new List<(Transform, ThreatKind)>();

        // Unity 6 預設進 Play Mode 不重載 domain，靜態快取會留著上一局已銷毀的物件
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _frame = -1;
            _cache.Clear();
        }

        static void Refresh()
        {
            if (_frame == Time.frameCount && _cache.TrueForAll(c => c.t != null)) return;
            _frame = Time.frameCount;
            _cache.Clear();
            // 所有連線玩家（主機上看得到每個人的蹲 / 跑狀態）
            foreach (var p in DSTNetPlayer.All)
            {
                if (p == null || !p.IsSpawned || p.InVehicle || !p.IsAlive) continue;   // 死掉的玩家不是威脅
                _cache.Add((p.transform, p.IsCrouching ? ThreatKind.Crouching : p.IsSprinting ? ThreatKind.Sprinting : ThreatKind.Walking));
            }
            foreach (var v in Object.FindObjectsByType<DSTVehicleEntry>())
                if (v.HasDriver)
                    _cache.Add((v.transform, Mathf.Abs(v.Drivable.SpeedKmh) > 3f ? ThreatKind.MovingVehicle : ThreatKind.IdleVehicle));
        }

        public static Transform Nearest(Vector3 from, out float distance, out ThreatKind kind)
        {
            Refresh();
            Transform best = null; distance = float.MaxValue; kind = ThreatKind.Walking;
            foreach (var (t, k) in _cache)
            {
                if (t == null) continue;
                float d = Vector3.Distance(from, t.position);
                if (d < distance) { distance = d; best = t; kind = k; }
            }
            return best;
        }

        // 所有威脅的位置和種類，給玩家活動指數累計用
        public static void All(List<(Vector3 pos, ThreatKind kind)> into)
        {
            Refresh();
            into.Clear();
            foreach (var (t, k) in _cache)
                if (t != null) into.Add((t.position, k));
        }

        // 所有玩家（含開車中）的位置，給生成器用
        public static void Positions(List<Vector3> into)
        {
            Refresh();
            into.Clear();
            foreach (var (t, _) in _cache)
                if (t != null) into.Add(t.position);
        }
    }
}
