using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DUST.World
{
    // 隨機世界裡每一個擺設（石頭、牆、陶罐、木桶……）都可以被撞倒或打碎。
    // 擺設本身是種子算出來的，不是連線物件；每一個都有「由種子和生成順序決定」的編號，
    // 被動過的才記下來（DSTPropSync：哪個編號碎了、被推到哪），區塊重新載入時照記錄還原。
    // 物理只在「碰到它的那台電腦」上跑，跑的時候把位置送給其他人，停下來後再變回不會動的物件。
    public class DSTWorldProp : MonoBehaviour
    {
        public ulong Id { get; private set; }
        public DSTPropCatalog.Entry Entry { get; private set; }
        public Vector3 Home { get; private set; }
        public bool IsBroken { get; private set; }
        public bool Simulating => _local;

        public static readonly Dictionary<ulong, DSTWorldProp> Live = new Dictionary<ulong, DSTWorldProp>();

        Rigidbody _rb;
        Renderer[] _renderers;
        Collider[] _colliders;
        readonly List<Collider> _dynColliders = new List<Collider>();
        float _volume, _mass;
        bool _local, _breakSent, _following;
        float _wokeAt, _rest, _nextSend;
        Vector3 _netPos;
        Quaternion _netRot;
        Quaternion _homeRot;

        const float RestSeconds = 1.2f;
        const float SendInterval = 0.1f;

        // ------------------------------------------------------------------ 建立：生成完一批擺設後呼叫，依階層順序編號（每台電腦一樣）
        public static void Attach(GameObject root, int seed, int a, int b, int ns)
        {
            var w = DSTWorld.Instance;
            var cat = w && w.palette ? w.palette.props : null;
            if (!cat || !root) return;
            uint hi = DSTNoise.Hash(seed, a, b, 9000 + ns);
            uint ordinal = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.EndsWith("(Clone)", System.StringComparison.Ordinal)) continue;
                ordinal++;
                var e = cat.Find(t.name);
                if (e == null || t.GetComponent<DSTWorldProp>()) continue;
                ulong id = ((ulong)hi << 32) | ordinal;
                if (e.mode == DSTPropCatalog.Mode.Sturdy && Duplicate(t.gameObject, id)) continue;
                t.gameObject.AddComponent<DSTWorldProp>().Init(id, e);
            }
        }

        // 相鄰兩間房子會在共用的那條邊上各蓋一面牆（完全重疊）：只留一面，不然撞碎一面還有一面擋著。
        // 留編號小的那面（每台電腦載入順序不同，結果還是一樣）
        static readonly Dictionary<Vector3Int, DSTWorldProp> _solid = new Dictionary<Vector3Int, DSTWorldProp>();

        static bool Duplicate(GameObject go, ulong id)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return false;
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            var key = Vector3Int.RoundToInt(b.center * 10f);
            if (!_solid.TryGetValue(key, out var other) || !other) return false;
            var ob = other.WorldBounds();
            if ((ob.size - b.size).sqrMagnitude > 0.01f || (ob.center - b.center).sqrMagnitude > 0.0025f) return false;
            if (id < other.Id)
            {
                // 新來的編號比較小：留新的，舊的拿掉
                Destroy(other.gameObject);
                _solid.Remove(key);
                return false;
            }
            Destroy(go);
            return true;
        }

        Vector3Int _solidKey;
        bool _inSolid;

        void Init(ulong id, DSTPropCatalog.Entry e)
        {
            Id = id;
            Entry = e;
            Home = transform.position;
            _homeRot = transform.rotation;
            _renderers = GetComponentsInChildren<Renderer>();
            _colliders = GetComponentsInChildren<Collider>();
            var bb = WorldBounds();
            _volume = Mathf.Max(0.001f, bb.size.x * bb.size.y * bb.size.z);
            _mass = Mathf.Max(1f, e.mass * _volume / Mathf.Max(0.001f, e.refVolume));
            if (Live.TryGetValue(id, out var old) && old && old != this) old.Id = 0;
            Live[id] = this;
            if (e.mode == DSTPropCatalog.Mode.Sturdy)
            {
                _solidKey = Vector3Int.RoundToInt(bb.center * 10f);
                _solid[_solidKey] = this;
                _inSolid = true;
            }

            // 之前被動過：照記錄還原
            if (DSTPropSync.State.TryGetValue(id, out var rec))
            {
                if (rec.state == DSTPropSync.Broken) Hide();
                else transform.SetPositionAndRotation(rec.pos, rec.rot);
            }
        }

        void OnDestroy()
        {
            if (Id != 0 && Live.TryGetValue(Id, out var p) && p == this) Live.Remove(Id);
            if (_inSolid && _solid.TryGetValue(_solidKey, out var s) && s == this) _solid.Remove(_solidKey);
        }

        public Bounds WorldBounds()
        {
            var b = new Bounds(transform.position, Vector3.zero);
            bool first = true;
            if (_renderers != null)
                foreach (var r in _renderers)
                {
                    if (!r) continue;
                    if (first) { b = r.bounds; first = false; }
                    else b.Encapsulate(r.bounds);
                }
            return b;
        }

        /// <summary>越大的東西越難撞壞</summary>
        public float SizeFactor => Mathf.Clamp(Mathf.Pow(_volume / Mathf.Max(0.001f, Entry.refVolume), 1f / 3f), 0.6f, 2.2f);
        public float Mass => _mass;

        // ------------------------------------------------------------------ 變成會動的（只有輕的和會倒的）
        public bool CanMove => Entry != null && Entry.mode != DSTPropCatalog.Mode.Sturdy;

        /// <summary>這台電腦要開始算它的物理（自己的車快撞到、自己的角色碰到）</summary>
        public void Wake()
        {
            if (!CanMove || IsBroken) return;
            if (!_local && !GroundBelow()) return;      // 地面碰撞只在玩家附近才有：沒有地面就先不要動（不然直接掉出世界）
            EnsureBody();
            _following = false;
            if (!_local)
            {
                _local = true;
                _wokeAt = Time.time;
                _rest = 0f;
                _rb.isKinematic = false;
                _rb.WakeUp();
            }
            else _rest = 0f;
        }

        bool GroundBelow()
        {
            var b = WorldBounds();
            var hits = Physics.RaycastAll(b.center + Vector3.up * 0.5f, Vector3.down, b.extents.y + 3f, ~0, QueryTriggerInteraction.Ignore);
            foreach (var h in hits) if (h.collider.GetComponent<DSTWorldChunk>()) return true;
            return false;
        }

        void EnsureBody()
        {
            if (_rb) return;
            // 原本的碰撞體（網格，不能當剛體）換成箱子 / 膠囊
            foreach (var c in _colliders) if (c) c.enabled = false;
            foreach (var r in _renderers)
            {
                if (!r || r is ParticleSystemRenderer) continue;
                var lb = r.localBounds;
                if (Entry.capsule || Entry.mode == DSTPropCatalog.Mode.Topple)
                {
                    var cc = r.gameObject.AddComponent<CapsuleCollider>();
                    cc.center = lb.center;
                    int axis = lb.size.y >= lb.size.x && lb.size.y >= lb.size.z ? 1 : lb.size.x >= lb.size.z ? 0 : 2;
                    cc.direction = axis;
                    cc.height = lb.size[axis];
                    float r0 = Mathf.Max(lb.size[(axis + 1) % 3], lb.size[(axis + 2) % 3]) * 0.5f;
                    cc.radius = Entry.mode == DSTPropCatalog.Mode.Topple ? r0 * 0.12f : r0;   // 棕櫚樹：只有樹幹
                    if (Entry.mode == DSTPropCatalog.Mode.Topple) { cc.center = new Vector3(0f, lb.center.y, 0f); }
                    _dynColliders.Add(cc);
                }
                else
                {
                    var bc = r.gameObject.AddComponent<BoxCollider>();
                    bc.center = lb.center;
                    bc.size = lb.size;
                    _dynColliders.Add(bc);
                }
            }
            _rb = gameObject.AddComponent<Rigidbody>();
            _rb.mass = _mass;
            _rb.linearDamping = 0.05f;
            _rb.angularDamping = 0.25f;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _rb.maxDepenetrationVelocity = 0.5f;   // 擺設本來就微微陷在沙裡：慢慢推出來，不要彈起來
            _rb.isKinematic = true;
        }

        /// <summary>角色走路撞到（CharacterController 不會推剛體，自己推）</summary>
        public void Bump(Vector3 velocity, float hitterMass, Vector3 point)
        {
            if (IsBroken || Entry == null) return;
            float speed = velocity.magnitude;
            if (CanMove)
            {
                Wake();
                if (_rb && !_rb.isKinematic)
                {
                    // 動量交換（簡化）：越輕被推得越快
                    float share = hitterMass / (hitterMass + _mass);
                    var dv = velocity * share * 1.6f - Vector3.Project(_rb.linearVelocity, velocity.normalized);
                    if (Vector3.Dot(dv, velocity) > 0f) _rb.AddForceAtPosition(dv * _mass, point + Vector3.up * 0.05f, ForceMode.Impulse);
                }
            }
            if (Entry.debris && hitterMass >= Entry.minHitterMass && speed >= Entry.breakSpeed * (Entry.mode == DSTPropCatalog.Mode.Sturdy ? SizeFactor : 1f))
                RequestBreak(velocity.normalized, speed, point);
        }

        /// <summary>重的東西被撞：撞上來的相對速度夠快、撞的東西夠重就碎</summary>
        public bool Impact(Vector3 hitterVelocity, float relativeSpeed, float hitterMass, Vector3 point)
        {
            if (IsBroken || Entry == null || !Entry.debris || Entry.mode == DSTPropCatalog.Mode.Loose) return false;
            if (hitterMass < Entry.minHitterMass || relativeSpeed < Entry.breakSpeed * SizeFactor) return false;
            if (Entry.mode == DSTPropCatalog.Mode.Topple) { Wake(); return false; }
            RequestBreak(hitterVelocity.sqrMagnitude > 0.25f ? hitterVelocity.normalized : (transform.position - point).normalized, relativeSpeed, point);
            return true;
        }

        void OnCollisionEnter(Collision c)
        {
            // 自己在算的時候：被撞 / 摔得太猛就碎
            if (!_local || !_rb || _breakSent || IsBroken || !Entry.debris) return;
            var other = c.rigidbody;
            // 剛醒來時和地面 / 牆的接觸是「本來就貼著」，不是撞擊
            if (!other && Time.time - _wokeAt < 0.4f) return;
            // 用撞擊方向上的相對速度判斷（衝量 / 質量 會把穿插的推出量也算進去，一碰就碎）
            var contact = c.GetContact(0);
            float speed = Mathf.Abs(Vector3.Dot(c.relativeVelocity, contact.normal));
            if (speed < Entry.breakSpeed) return;
            var dir = other && other.linearVelocity.sqrMagnitude > 0.5f ? other.linearVelocity.normalized : (transform.position - contact.point).normalized;
            RequestBreak(dir, speed, contact.point);
        }

        public void RequestBreak(Vector3 dir, float speed, Vector3 point)
        {
            if (_breakSent || IsBroken || !Entry.debris) return;
            _breakSent = true;
            var vel = _rb && !_rb.isKinematic ? _rb.linearVelocity : Vector3.zero;
            var bounds = WorldBounds();
            // 先把碰撞關掉（不用等主機回覆），車子才不會在同一個地方再撞一次
            foreach (var c in _colliders) if (c) c.enabled = false;
            foreach (var c in _dynColliders) if (c) c.enabled = false;
            DSTPropSync.SendBreak(this, dir, speed, point, vel);
            // 上面撐著的東西（屋簷、屋頂）也跟著塌
            StartCoroutine(Collapse(bounds));
        }

        IEnumerator Collapse(Bounds b)
        {
            yield return new WaitForSeconds(0.18f);
            var hits = Physics.OverlapBox(b.center + Vector3.up * (b.extents.y + 0.2f), new Vector3(b.extents.x * 0.9f, b.extents.y * 0.6f + 0.3f, b.extents.z * 0.9f));
            var done = new HashSet<DSTWorldProp>();
            foreach (var h in hits)
            {
                var p = h.GetComponentInParent<DSTWorldProp>();
                if (!p || p == this || p.IsBroken || !done.Add(p)) continue;
                if (p.WorldBounds().min.y < b.center.y) continue;      // 旁邊的，不是壓在上面的
                if (p.CanMove) p.Wake();
                else p.RequestBreak(Vector3.down, 3f, p.WorldBounds().center);
            }
        }

        // ------------------------------------------------------------------ 每台電腦都會收到：碎掉
        public void ApplyBreak(Vector3 dir, float speed, Vector3 point, Vector3 vel, int seed)
        {
            if (IsBroken) return;
            var b = WorldBounds();
            Hide();
            if (!Entry.debris) return;
            // 碎片 Prefab 已經和原模型對齊（DSTWorldPropSetup）；擺設縮放過（石頭 0.8~2.4 倍），碎片跟著縮
            var go = Instantiate(Entry.debris, transform.position, transform.rotation);
            var s = transform.lossyScale; var s0 = Entry.sourceScale;
            go.transform.localScale = new Vector3(s.x / Nz(s0.x), s.y / Nz(s0.y), s.z / Nz(s0.z));
            var d = go.GetComponent<DSTDebris>();
            if (d)
            {
                d.IgnoreStaticAround(b);
                d.Burst(vel, dir, speed, point, seed);
            }
        }

        static float Nz(float v) => Mathf.Abs(v) < 1e-5f ? 1f : v;

        /// <summary>已經碎掉的（新加入時收到的記錄）：直接不見，不噴碎片</summary>
        public void HideBroken() { if (!IsBroken) Hide(); }

        void Hide()
        {
            IsBroken = true;
            _local = false;
            _following = false;
            foreach (var r in _renderers) if (r) r.enabled = false;
            foreach (var c in _colliders) if (c) c.enabled = false;
            foreach (var c in _dynColliders) if (c) c.enabled = false;
            if (_rb) { if (!_rb.isKinematic) { _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; } _rb.isKinematic = true; }
        }

        /// <summary>沙漠把東西「修好」了：回到原本的樣子（DSTPropSync 過了一段時間、附近沒人時）</summary>
        public void Restore()
        {
            IsBroken = false;
            _breakSent = false;
            _local = false;
            _following = false;
            if (_rb) { if (!_rb.isKinematic) { _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; } _rb.isKinematic = true; }
            transform.SetPositionAndRotation(Home, _homeRot);
            foreach (var r in _renderers) if (r) r.enabled = true;
            bool dyn = _dynColliders.Count > 0;
            foreach (var c in _colliders) if (c) c.enabled = !dyn;
            foreach (var c in _dynColliders) if (c) c.enabled = true;
        }

        // ------------------------------------------------------------------ 別人在推：跟著收到的位置走
        public void NetPose(Vector3 pos, Quaternion rot, bool final)
        {
            if (IsBroken || _local) return;
            EnsureBody();
            _rb.isKinematic = true;
            _netPos = pos; _netRot = rot;
            _following = true;
            if (final && (transform.position - pos).sqrMagnitude > 4f) transform.SetPositionAndRotation(pos, rot);
        }

        void Update()
        {
            if (_following)
            {
                float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
                transform.SetPositionAndRotation(Vector3.Lerp(transform.position, _netPos, k), Quaternion.Slerp(transform.rotation, _netRot, k));
                if ((transform.position - _netPos).sqrMagnitude < 1e-6f) _following = false;
            }
        }

        void FixedUpdate()
        {
            if (!_local || !_rb || _rb.isKinematic) return;
            // 掉出世界：放回原位
            if (transform.position.y < Home.y - 60f) { Restore(); DSTPropSync.SendPose(this, Home, _homeRot, true); return; }
            bool resting = _rb.IsSleeping() || (_rb.linearVelocity.sqrMagnitude < 0.01f && _rb.angularVelocity.sqrMagnitude < 0.02f);
            _rest = resting ? _rest + Time.fixedDeltaTime : 0f;
            bool moved = (transform.position - Home).sqrMagnitude > 0.0004f || Quaternion.Angle(transform.rotation, _homeRot) > 0.5f;
            if (_rest > RestSeconds)
            {
                // 停下來：送出最後的位置，變回不會動的
                _local = false;
                _rb.isKinematic = true;
                if (moved) DSTPropSync.SendPose(this, transform.position, transform.rotation, true);
                return;
            }
            if (moved && Time.time >= _nextSend && !resting)
            {
                _nextSend = Time.time + SendInterval;
                DSTPropSync.SendPose(this, transform.position, transform.rotation, false);
            }
        }
    }
}
