using DUST.Player;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace DUST.World
{
    // 可被推動 / 撞壞的地圖物件（箱子、木桶）。
    // 物理由「最靠近的駕駛」那台電腦跑：載具靠近時 DSTPhysicsClaimer 會先把附近物件的 Ownership 要過去，
    // 這樣駕駛撞到的是真正的剛體而不是不會動的同步複本；物件停下來後交還主機。
    // 會不會碎由撞擊瞬間物件的速度變化（Δv = 衝量 / 質量）決定：慢慢頂是推，撞得夠猛才會爆開。
    [RequireComponent(typeof(Rigidbody))]
    public class DSTPhysicsProp : NetworkBehaviour
    {
        [Header("Break")]
        public bool breakable = true;
        public float breakDeltaV = 5f;            // m/s；超過就碎（約等於 15 km/h 撞上去）
        public GameObject debrisPrefab;
        public float debrisScale = 1f;            // 小箱子共用大箱子的碎片，縮放即可
        public float respawnSeconds = 120f;
        public float respawnClearRadius = 12f;    // 有人在附近就先不要重生（免得憑空冒出來）

        [Header("Ownership")]
        public float releaseAfterRestSeconds = 2f;

        public bool IsBroken => _broken.Value;
        public float LastImpactDeltaV { get; private set; }   // 調整碎裂門檻時參考

        readonly NetworkVariable<bool> _broken = new NetworkVariable<bool>(false);

        Rigidbody _rb;
        Collider[] _colliders;
        Renderer[] _renderers;
        Vector3 _homePos;
        Quaternion _homeRot;
        float _restTimer;
        float _brokenAt;
        float _lastClaimRequest;
        bool _breakSent;

        bool HasPhysicsAuthority => !IsSpawned || IsOwner;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _colliders = GetComponentsInChildren<Collider>();
            _renderers = GetComponentsInChildren<Renderer>();
            _homePos = transform.position;
            _homeRot = transform.rotation;
        }

        public override void OnNetworkSpawn()
        {
            _broken.OnValueChanged += OnBrokenChanged;
            ApplyBroken(_broken.Value);
        }

        public override void OnNetworkDespawn() => _broken.OnValueChanged -= OnBrokenChanged;

        // ------------------------------------------------------------------ Ownership
        // 載具（駕駛那台）在撞上之前呼叫：把物理交給自己算
        public void RequestOwnership()
        {
            if (!IsSpawned || IsOwner || IsBroken) return;
            if (Time.time - _lastClaimRequest < 0.5f) return;
            _lastClaimRequest = Time.time;
            ClaimRpc();
        }

        [Rpc(SendTo.Server)]
        void ClaimRpc(RpcParams rpc = default)
        {
            if (IsBroken) return;
            ulong id = rpc.Receive.SenderClientId;
            if (OwnerClientId == id) return;
            // 正在被別的玩家推（還沒停下來）就不要搶，避免兩邊來回切換
            if (OwnerClientId != NetworkManager.ServerClientId && _rb.linearVelocity.sqrMagnitude > 0.25f) return;
            NetworkObject.ChangeOwnership(id);
        }

        [Rpc(SendTo.Server)]
        void ReleaseRpc(RpcParams rpc = default)
        {
            if (OwnerClientId == rpc.Receive.SenderClientId) NetworkObject.RemoveOwnership();
        }

        void FixedUpdate()
        {
            // 碎掉時碰撞體關掉了：Ownership 轉移時 NetworkRigidbody 可能把它改回動態，會一路掉出地圖
            if (IsBroken && !_rb.isKinematic) _rb.isKinematic = true;
            if (!IsSpawned || !IsOwner || IsServer || IsBroken) return;
            // 客戶端拿著物理：停穩一陣子就交還主機
            bool resting = _rb.IsSleeping() || (_rb.linearVelocity.sqrMagnitude < 0.0025f && _rb.angularVelocity.sqrMagnitude < 0.01f);
            _restTimer = resting ? _restTimer + Time.fixedDeltaTime : 0f;
            if (_restTimer > releaseAfterRestSeconds)
            {
                _restTimer = 0f;
                ReleaseRpc();
            }
        }

        // ------------------------------------------------------------------ 撞擊
        void OnCollisionEnter(Collision c)
        {
            if (IsBroken || _breakSent || !HasPhysicsAuthority) return;
            float deltaV = c.impulse.magnitude / Mathf.Max(0.01f, _rb.mass);
            LastImpactDeltaV = deltaV;
            if (!breakable || deltaV < breakDeltaV) return;

            // 撞擊方向：撞過來的東西的移動方向；撞到不會動的東西就用接觸法線
            var otherRb = c.rigidbody;
            Vector3 dir = otherRb && otherRb.linearVelocity.sqrMagnitude > 0.5f
                ? otherRb.linearVelocity.normalized
                : (transform.position - c.GetContact(0).point).normalized;
            Vector3 point = c.GetContact(0).point;
            // Δv 用來判斷碎不碎；碎片飛多快要看實際的相對速度（重車撞輕箱子時衝量 / 質量會大得不合理）
            float impactSpeed = Mathf.Min(deltaV, c.relativeVelocity.magnitude * 1.2f);
            _breakSent = true;
            if (IsSpawned) BreakRpc(dir, impactSpeed, point, _rb.linearVelocity);
            else { Shatter(dir, impactSpeed, point, _rb.linearVelocity, Random.Range(0, int.MaxValue)); ApplyBroken(true); }
        }

        [Rpc(SendTo.Server)]
        void BreakRpc(Vector3 dir, float deltaV, Vector3 point, Vector3 velocity)
        {
            if (IsBroken) return;
            _broken.Value = true;
            _brokenAt = Time.time;
            if (OwnerClientId != NetworkManager.ServerClientId) NetworkObject.RemoveOwnership();
            ShatterRpc(dir, deltaV, point, velocity, Random.Range(0, int.MaxValue));
        }

        [Rpc(SendTo.Everyone)]
        void ShatterRpc(Vector3 dir, float deltaV, Vector3 point, Vector3 velocity, int seed) => Shatter(dir, deltaV, point, velocity, seed);

        // 碎片純視覺、每台電腦自己算（同一個亂數種子，大致長得一樣）
        void Shatter(Vector3 dir, float deltaV, Vector3 point, Vector3 velocity, int seed)
        {
            ApplyBroken(true);
            if (!debrisPrefab) return;
            var go = Instantiate(debrisPrefab, transform.position, transform.rotation);
            go.transform.localScale = Vector3.one * debrisScale;
            var debris = go.GetComponent<DSTDebris>();
            if (debris) debris.Burst(velocity, dir, deltaV, point, seed);
        }

        void OnBrokenChanged(bool _, bool broken) => ApplyBroken(broken);

        void ApplyBroken(bool broken)
        {
            foreach (var r in _renderers) r.enabled = !broken;
            foreach (var col in _colliders) col.enabled = !broken;
            if (broken)
            {
                if (!_rb.isKinematic) { _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; }
                _rb.isKinematic = true;
            }
            else
            {
                _breakSent = false;
                // 交還給 NetworkRigidbody 決定（擁有者是動態、其他人是 Kinematic）
                if (!IsSpawned || IsOwner) _rb.isKinematic = false;
            }
        }

        // ------------------------------------------------------------------ 重生（主機）
        void Update()
        {
            if (!IsSpawned || !IsServer || !IsBroken || respawnSeconds <= 0f) return;
            if (Time.time - _brokenAt < respawnSeconds) return;
            foreach (var p in DSTNetPlayer.All)
                if (p && Vector3.Distance(p.transform.position, _homePos) < respawnClearRadius) return;
            var nt = GetComponent<NetworkTransform>();
            if (nt) nt.Teleport(_homePos, _homeRot, transform.localScale);
            else transform.SetPositionAndRotation(_homePos, _homeRot);
            _broken.Value = false;
            ApplyBroken(false);
        }
    }
}
