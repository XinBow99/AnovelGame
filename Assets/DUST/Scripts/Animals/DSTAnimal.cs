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
    // 連線：AI 只在主機上跑，位置 / 動畫用 NetworkTransform、NetworkAnimator 同步給大家
    [RequireComponent(typeof(NavMeshAgent))]
    public class DSTAnimal : NetworkBehaviour, IDSTDamageable
    {
        public enum State { Idle, Wander, Sniff, Alert, Flee, Dead }

        public DSTSpeciesDefinition species;
        public float corpseLifetime = 10f;

        public State CurrentState { get; private set; }
        public float Health { get; private set; }
        public bool IsAlive => CurrentState != State.Dead && !_dead.Value;
        public event System.Action<DSTAnimal> Removed;

        NavMeshAgent _agent;
        Animator _anim;
        NetworkAnimator _netAnim;
        readonly NetworkVariable<bool> _dead = new NetworkVariable<bool>(false);
        float _stateUntil;
        float _nextRepath;
        Transform _threat;
        bool _panicked;

        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int SniffId = Animator.StringToHash("Sniff");
        static readonly int DeadId = Animator.StringToHash("Dead");
        static readonly int AlertId = Animator.StringToHash("Alert");
        bool _hasAlertParam;

        void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _anim = GetComponentInChildren<Animator>();
            _netAnim = GetComponentInChildren<NetworkAnimator>();
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
            _agent.angularSpeed = species.turnSpeedDeg;
            _agent.acceleration = species.acceleration;
            Enter(State.Idle);
        }

        public override void OnDestroy()
        {
            Removed?.Invoke(this);
            base.OnDestroy();
        }

        void Update()
        {
            if (!Authority || CurrentState == State.Dead) return;
            Sense();
            Think();
            if (_anim)
            {
                _anim.SetFloat(SpeedId, _agent.velocity.magnitude, 0.12f, Time.deltaTime);
                _anim.SetBool(SniffId, CurrentState == State.Sniff);
                if (_hasAlertParam) _anim.SetBool(AlertId, CurrentState == State.Alert);
            }
        }

        // ------------------------------------------------------------------ 感知
        void Sense()
        {
            if (species.temperament == Temperament.Passive && !_panicked) return;
            var t = DSTThreats.Nearest(transform.position, out float dist, out var kind);
            if (t == null) return;
            float noticeMul = kind switch
            {
                ThreatKind.Crouching => species.crouchNoticeMultiplier,
                ThreatKind.Sprinting => species.sprintNoticeMultiplier,
                ThreatKind.MovingVehicle => species.vehicleNoticeMultiplier,
                _ => 1f,
            };
            float notice = species.noticeRadius * noticeMul;
            float flee = species.fleeRadius * Mathf.Max(1f, noticeMul * 0.8f);
            bool seen = dist < notice && HasLineOfSight(t);
            bool heard = dist < flee * 0.6f;   // 貼太近，就算隔著箱子也知道

            switch (CurrentState)
            {
                case State.Idle:
                case State.Wander:
                case State.Sniff:
                    if ((seen || heard) && dist < flee) StartFlee(t);
                    else if (seen) { _threat = t; Enter(State.Alert); }
                    break;
                case State.Alert:
                    if (dist < flee) StartFlee(t);
                    break;
                case State.Flee:
                    _threat = t;
                    break;
            }
        }

        bool HasLineOfSight(Transform t)
        {
            Vector3 eye = transform.position + Vector3.up * 0.4f;
            Vector3 target = t.position + Vector3.up * 0.5f;
            if (!Physics.Linecast(eye, target, out var hit, ~0, QueryTriggerInteraction.Ignore)) return true;
            return hit.transform == t || hit.transform.IsChildOf(t) || hit.transform.IsChildOf(transform);
        }

        // ------------------------------------------------------------------ 決策
        void Think()
        {
            switch (CurrentState)
            {
                case State.Idle:
                    if (Time.time > _stateUntil) Enter(Random.value < species.sniffChance ? State.Sniff : State.Wander);
                    break;
                case State.Sniff:
                    if (Time.time > _stateUntil) Enter(State.Idle);
                    break;
                case State.Wander:
                    if (Arrived() || Time.time > _stateUntil) Enter(State.Idle);
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
            }
        }

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
                    _stateUntil = Time.time + Random.Range(species.sniffTime.x, species.sniffTime.y);
                    break;
                case State.Wander:
                    _agent.speed = species.walkSpeed;
                    if (RandomPoint(transform.position, species.wanderRadius, out var p)) Go(p);
                    else { Enter(State.Idle); return; }
                    _stateUntil = Time.time + 20f;
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
            }
        }

        void StartFlee(Transform from)
        {
            _threat = from;
            Enter(State.Flee);
        }

        void PickFleeDestination()
        {
            _nextRepath = Time.time + 1.0f;
            Vector3 away = _threat ? transform.position - _threat.position : transform.forward;
            away.y = 0;
            if (away.sqrMagnitude < 0.01f) away = transform.forward;
            away.Normalize();
            // 正後方走不通（死巷）就往兩側試
            foreach (float ang in new[] { 0f, 35f, -35f, 70f, -70f, 110f, -110f })
            {
                Vector3 dir = Quaternion.Euler(0, ang, 0) * away;
                Vector3 target = transform.position + dir * species.fleeDistance;
                if (NavMesh.SamplePosition(target, out var hit, 4f, NavMesh.AllAreas))
                {
                    var path = new NavMeshPath();
                    if (_agent.CalculatePath(hit.position, path) && path.status == NavMeshPathStatus.PathComplete)
                    {
                        _agent.isStopped = false;
                        _agent.SetPath(path);
                        return;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ 受傷 / 死亡
        public void TakeDamage(float amount, Transform source)
        {
            if (!Authority || !IsAlive) return;
            Health -= amount;
            if (Health <= 0f) { Die(); return; }
            _panicked = true;
            StartFlee(source);
        }

        void Die()
        {
            CurrentState = State.Dead;
            if (IsSpawned) _dead.Value = true;
            if (_agent.isOnNavMesh) _agent.isStopped = true;
            _agent.enabled = false;
            if (_anim)
            {
                _anim.SetFloat(SpeedId, 0f);
                // Trigger 要經過 NetworkAnimator 才會同步到客戶端
                if (_netAnim && IsSpawned) _netAnim.SetTrigger(DeadId);
                else _anim.SetTrigger(DeadId);
            }
            StartCoroutine(RemoveAfter(corpseLifetime));
        }

        System.Collections.IEnumerator RemoveAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
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
                if (p == null || !p.IsSpawned || p.InVehicle) continue;
                _cache.Add((p.transform, p.IsCrouching ? ThreatKind.Crouching : p.IsSprinting ? ThreatKind.Sprinting : ThreatKind.Walking));
            }
            foreach (var v in Object.FindObjectsByType<DSTVehicleEntry>())
                if (v.HasDriver)
                    _cache.Add((v.transform, Mathf.Abs(v.vehicle.SpeedKmh) > 3f ? ThreatKind.MovingVehicle : ThreatKind.IdleVehicle));
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
