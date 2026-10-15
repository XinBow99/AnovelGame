using System.Collections.Generic;
using System.Threading.Tasks;
using DUST.Player;
using DUST.Vehicles;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.World
{
    // 地圖模式：遺跡二（場景裡做好的地圖）或隨機世界（種子碼生成、無限大、分區塊載入）。
    // 連線：主機開房時決定模式與種子，用 NetworkVariable 傳給所有人；地形是種子算出來的，每台電腦自己生成，不用傳地形資料。
    // 區塊載入：
    //   - 畫面：以「自己」為中心 viewRings 圈，越遠越粗。
    //   - 碰撞：以所有玩家、所有載具、箱子為中心各 1 圈（3×3）。主機要跑沒人開的車、箱子的物理，
    //     駕駛那台電腦要跑自己的車，所以每台電腦都幫所有人載入碰撞，誰拿到物理都站得住。
    public class DSTWorld : NetworkBehaviour
    {
        public DSTWorldPalette palette;
        public GameObject dr2Map;                 // 遺跡二的地圖（隨機世界時藏起來）
        public Volume fogVolume;                  // 隨機世界把霧拉近，遮住還沒載入的遠方
        public float randomFogMeanFreePath = 300f;
        public float horizonHeight = -9f;           // 比地形最低處再低一點
        public GameObject meBikeStation;           // 營地旁的 MeBike 站（DUST/Vehicles/Setup MeBike 會填）

        [Header("Streaming")]
        public int viewRings = 6;                 // 6 圈 × 64 m ≈ 400 m
        public int maxJobs = 4;
        public int appliesPerFrame = 3;
        public int scattersPerFrame = 2;

        // 開房前由選單設定（只有主機用得到）
        public static bool RequestRandom;
        public static int RequestSeed;

        public static DSTWorld Instance { get; private set; }
        // 玩家要等世界準備好（地面生出來）才放到出生點
        public static bool IsReady => Instance == null || Instance._ready;

        public bool IsRandom => _active;
        public int Seed => _seed.Value;
        public DSTWorldGen Gen { get; private set; }
        public Transform Root => _root;
        public int ChunkCount => _chunks.Count;
        public int ColliderVersion { get; private set; }   // 有新的碰撞進來 → 導航網格要重建

        readonly NetworkVariable<bool> _random = new NetworkVariable<bool>(false);
        readonly NetworkVariable<int> _seed = new NetworkVariable<int>(0);

        bool _ready;
        bool _active;
        bool _relocatePending;
        Transform _horizon;
        Transform _root;
        readonly Dictionary<Vector2Int, DSTWorldChunk> _chunks = new Dictionary<Vector2Int, DSTWorldChunk>();
        readonly Dictionary<Vector2Int, (int lod, Task<DSTWorldChunk.Data> task)> _jobs = new Dictionary<Vector2Int, (int, Task<DSTWorldChunk.Data>)>();
        readonly Dictionary<Vector2Int, Task> _bakes = new Dictionary<Vector2Int, Task>();
        readonly Dictionary<Vector2Int, int> _want = new Dictionary<Vector2Int, int>();
        readonly HashSet<Vector2Int> _wantCollider = new HashSet<Vector2Int>();
        readonly List<Vector2Int> _tmp = new List<Vector2Int>();
        readonly List<(Vector2Int c, int lod, int prio)> _queue = new List<(Vector2Int, int, int)>();

        // 離開房間時把場景還原
        readonly List<GameObject> _hidden = new List<GameObject>();
        readonly List<(Transform t, Vector3 p, Quaternion r)> _scenePoses = new List<(Transform, Vector3, Quaternion)>();
        readonly List<(DSTDroneController d, Bounds b)> _droneAreas = new List<(DSTDroneController, Bounds)>();
        Behaviour _dr2NavSurface;
        Fog _fog;
        float _fogMfp;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            RequestRandom = false;
            RequestSeed = 0;
            Instance = null;
        }

        void Awake()
        {
            Instance = this;
            foreach (var v in FindObjectsByType<DSTVehicleEntry>(FindObjectsInactive.Include))
                _scenePoses.Add((v.transform, v.transform.position, v.transform.rotation));
            foreach (var p in FindObjectsByType<DSTPhysicsProp>(FindObjectsInactive.Include))
                _scenePoses.Add((p.transform, p.transform.position, p.transform.rotation));
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                _random.Value = RequestRandom;
                _seed.Value = RequestSeed;
            }
            if (_random.Value) Activate(_seed.Value);
            _ready = true;
        }

        public override void OnNetworkDespawn()
        {
            // 房主關房：存檔（地形卸載前，動物的群還算得出來）
            if (DSTSave.Active)
            {
                DSTSave.SaveAll();
                DSTSave.Close();
            }
            Deactivate();
            RestoreScenePoses();
            _ready = false;
        }

        // ------------------------------------------------------------------ 開啟 / 關閉隨機世界
        void Activate(int seed)
        {
            Gen = new DSTWorldGen(seed);
            _active = true;
            Debug.Log($"[DUST] Random world, seed {seed}");

            // 藏起遺跡二：連線物件（箱子）不能關，之後搬到出生點；其他全部關掉
            _hidden.Clear();
            if (dr2Map)
            {
                foreach (Transform group in dr2Map.transform)
                {
                    if (!group.GetComponentInChildren<NetworkObject>(true)) Hide(group.gameObject);
                    else
                        foreach (Transform t in group)
                            if (!t.GetComponentInChildren<NetworkObject>(true)) Hide(t.gameObject);
                }
                // 遺跡二的導航網格會疊在新地形上，關掉（動物改用 DSTWorldNavMesh 即時建的）
                foreach (var b in dr2Map.GetComponents<Behaviour>())
                    if (b.GetType().Name == "NavMeshSurface" && b.enabled) { _dr2NavSurface = b; b.enabled = false; }
            }

            if (fogVolume && fogVolume.profile.TryGet(out _fog))
            {
                _fogMfp = _fog.meanFreePath.value;
                _fog.meanFreePath.value = randomFogMeanFreePath;
            }

            _droneAreas.Clear();
            foreach (var d in FindObjectsByType<DSTDroneController>())
            {
                _droneAreas.Add((d, d.flightArea));
                d.flightArea = default;   // 無限大的世界不限制飛行範圍
            }

            _root = new GameObject("RandomWorld").transform;
            _horizon = CreateHorizon(_root);

            // 營地旁的 MeBike 站（主機會在每根車柱生成一台）
            if (meBikeStation)
            {
                var st = Instantiate(meBikeStation, _root);
                float sx = 8f, sz = -1f;
                st.transform.SetPositionAndRotation(new Vector3(sx, Gen.Height(sx, sz), sz), Quaternion.Euler(0f, 90f, 0f));
                foreach (Transform part in st.transform)
                {
                    var p = part.position;
                    part.position = new Vector3(p.x, Gen.Height(p.x, p.z), p.z);
                }
            }

            // 出生點附近先同步生成（含碰撞），玩家和車子一放下去就有地面
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
            {
                var c = new Vector2Int(x, z);
                var ch = GetOrCreate(c);
                ch.Apply(DSTWorldChunk.Build(Gen, c, 0));
                Physics.BakeMesh(ch.MeshId, false);
                ch.AttachCollider();
                _settlementJobs.Clear();
                ch.SetScatter(DSTWorldScatter.Build(Gen, palette, c, ch.transform, _settlementJobs));
                foreach (var job in _settlementJobs) ch.StartCoroutine(job);   // 出生點旁的聚落中心也可能落在這 9 塊裡
            }
            ColliderVersion++;
            _relocatePending = IsServer;
        }

        // 地平線：載入範圍外是一大片低一點的沙地，不然會看到天空貼圖下半部的山（在霧裡看起來像遠方的沙漠）
        Transform CreateHorizon(Transform parent)
        {
            const float half = 4000f;
            var mesh = new Mesh { name = "Horizon" };
            mesh.vertices = new[] { new Vector3(-half, 0, -half), new Vector3(-half, 0, half), new Vector3(half, 0, half), new Vector3(half, 0, -half) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(half * 2f, 1f, half * 2f));
            var go = new GameObject("Horizon");
            go.transform.SetParent(parent, false);
            go.AddComponent<DSTOwnedMesh>().mesh = mesh;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = palette.sand;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            DSTOwnedMesh.OptOutGpuDriven(mr);
            return go.transform;
        }

        void Hide(GameObject go)
        {
            if (!go.activeSelf) return;
            go.SetActive(false);
            _hidden.Add(go);
        }

        void Deactivate()
        {
            if (!_active) return;
            _active = false;
            _region = null; _candidate = null; _notice = null;
            // 背景還在烘焙的碰撞要等它做完才能刪網格
            try { Task.WaitAll(new List<Task>(_bakes.Values).ToArray()); } catch { }
            foreach (var ch in _chunks.Values) if (ch) Destroy(ch.gameObject);
            _chunks.Clear();
            _jobs.Clear();     // 背景工作算完就丟掉
            _bakes.Clear();
            if (_root) Destroy(_root.gameObject);
            _root = null;
            foreach (var go in _hidden) if (go) go.SetActive(true);
            _hidden.Clear();
            if (_dr2NavSurface) _dr2NavSurface.enabled = true;
            _dr2NavSurface = null;
            if (_fog) _fog.meanFreePath.value = _fogMfp;
            _fog = null;
            foreach (var (d, b) in _droneAreas) if (d) d.flightArea = b;
            _droneAreas.Clear();
            Gen = null;
        }

        void RestoreScenePoses()
        {
            foreach (var (t, p, r) in _scenePoses)
            {
                if (!t) continue;
                Place(t, p, r);
                if (t.TryGetComponent<DSTPhysicsProp>(out var prop)) prop.SetHome(p, r);
            }
        }

        // ------------------------------------------------------------------ 出生點
        public Vector3 SpawnPoint => new Vector3(0f, Gen != null ? Gen.Height(0f, 0f) : 0f, 0f);

        public float GroundHeight(float x, float z) => Gen != null ? Gen.Height(x, z) : 0f;

        // 主機：把車和箱子搬到出生點附近（場景裡原本的位置在遺跡二）
        void Relocate()
        {
            int i = 0;
            foreach (var v in FindObjectsByType<DSTVehicleEntry>())
            {
                if (v.Drivable is DSTBikeController) continue;   // MeBike 由站點自己管
                if (v.Drivable is DSTJetController)
                {
                    // 米格-25：停在跑道頭，機頭朝跑道方向
                    var p = Gen.AirstripStart + Gen.AirstripDir * 12f;
                    float yaw = Mathf.Atan2(Gen.AirstripDir.x, Gen.AirstripDir.y) * Mathf.Rad2Deg;
                    Place(v.transform, new Vector3(p.x, Gen.Height(p.x, p.y) + 0.1f, p.y), Quaternion.Euler(0f, yaw, 0f));
                    continue;
                }
                float x = -3f + 4.5f * i++, z = 9f;
                Place(v.transform, new Vector3(x, Gen.Height(x, z) + 0.15f, z), Quaternion.Euler(0f, 0f, 0f));
            }
            // 箱子、木桶排成一區，給車子撞
            i = 0;
            foreach (var p in FindObjectsByType<DSTPhysicsProp>())
            {
                if (p.IsBroken) continue;
                int col = i % 5, row = i / 5;
                i++;
                float x = -16f + col * 1.9f + (row % 2) * 0.9f, z = 4f + row * 1.9f;
                var r = p.GetComponent<Renderer>();
                float lift = r ? p.transform.position.y - r.bounds.min.y : 0f;
                var pos = new Vector3(x, Gen.Height(x, z) + lift + 0.03f, z);
                var rot = Quaternion.Euler(0f, (i * 37) % 90, 0f);
                Place(p.transform, pos, rot);
                p.SetHome(pos, rot);
            }
        }

        static void Place(Transform t, Vector3 pos, Quaternion rot)
        {
            if (t.TryGetComponent<Rigidbody>(out var rb))
            {
                if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                rb.position = pos;
                rb.rotation = rot;
            }
            t.SetPositionAndRotation(pos, rot);
            if (t.TryGetComponent<NetworkTransform>(out var nt) && nt.IsSpawned && nt.CanCommitToTransform)
                nt.Teleport(pos, rot, t.localScale);
            Physics.SyncTransforms();
        }

        // ------------------------------------------------------------------ 區塊串流
        public static Vector2Int ChunkOf(Vector3 p) => new Vector2Int(Mathf.FloorToInt(p.x / DSTWorldChunk.Size), Mathf.FloorToInt(p.z / DSTWorldChunk.Size));

        Vector3 Viewer()
        {
            foreach (var v in DSTVehicleEntry.All)
                if (v && v.IsLocalDriver) return v.transform.position;
            var me = DSTNetPlayer.Local;
            return me ? me.transform.position : Vector3.zero;
        }

        void Update()
        {
            if (!_active) return;
            if (_relocatePending)
            {
                _relocatePending = false;
                Relocate();
            }

            RescueFallen();
            UpdateRegionNotice();

            // 想要的區塊與細節
            _want.Clear();
            _wantCollider.Clear();
            var vc = ChunkOf(Viewer());
            if (_horizon) _horizon.position = new Vector3(vc.x * DSTWorldChunk.Size, horizonHeight, vc.y * DSTWorldChunk.Size);
            for (int dx = -viewRings; dx <= viewRings; dx++)
            for (int dz = -viewRings; dz <= viewRings; dz++)
            {
                int ring = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz));
                _want[new Vector2Int(vc.x + dx, vc.y + dz)] = ring <= 1 ? 0 : ring <= 3 ? 1 : 2;
            }
            foreach (var p in DSTNetPlayer.All) if (p) AddColliderAnchor(p.transform.position);
            foreach (var v in DSTVehicleEntry.All)
            {
                if (!v) continue;
                AddColliderAnchor(v.transform.position);
                // 快的載具（米格-25 滑跑 60 m/s ≈ 每秒一個區塊）：前方 1.5 / 3 秒的區塊也先烘碰撞，
                // 不然機輪會掉進還沒有碰撞的區塊、撞上下一塊的邊（實測：滑跑到 400 m 速度從 61 掉到 32）
                if (v.TryGetComponent<Rigidbody>(out var vrb) && !vrb.isKinematic && vrb.linearVelocity.sqrMagnitude > 15f * 15f)
                {
                    AddColliderAnchor(v.transform.position + vrb.linearVelocity * 1.5f);
                    AddColliderAnchor(v.transform.position + vrb.linearVelocity * 3f);
                }
            }
            foreach (var p in DSTPhysicsProp.All) if (p && !p.IsBroken) AddColliderAnchor(p.transform.position);

            // 卸載：離太遠、也沒人需要碰撞
            _tmp.Clear();
            foreach (var kv in _chunks)
            {
                var c = kv.Key;
                if (_want.ContainsKey(c)) continue;
                int ring = Mathf.Max(Mathf.Abs(c.x - vc.x), Mathf.Abs(c.y - vc.y));
                if (ring > viewRings + 1 && !_bakes.ContainsKey(c)) _tmp.Add(c);   // 烘焙中的網格不能刪
            }
            foreach (var c in _tmp)
            {
                if (_chunks[c]) Destroy(_chunks[c].gameObject);
                _chunks.Remove(c);
                _jobs.Remove(c);
                _bakes.Remove(c);
            }

            // 算好的網格放上去
            int applied = 0;
            _tmp.Clear();
            foreach (var kv in _jobs) if (kv.Value.task.IsCompleted) _tmp.Add(kv.Key);
            foreach (var c in _tmp)
            {
                var job = _jobs[c];
                _jobs.Remove(c);
                if (job.task.IsFaulted) { Debug.LogException(job.task.Exception); continue; }
                if (!_chunks.TryGetValue(c, out var ch) || !ch) continue;
                if (applied++ >= appliesPerFrame) { _jobs[c] = job; continue; }   // 下一幀再放
                ch.Apply(job.task.Result);
            }

            // 碰撞：網格在背景烘焙好再掛上
            _tmp.Clear();
            foreach (var kv in _bakes) if (kv.Value.IsCompleted) _tmp.Add(kv.Key);
            foreach (var c in _tmp)
            {
                _bakes.Remove(c);
                if (_chunks.TryGetValue(c, out var ch) && ch && ch.CanCollide && _wantCollider.Contains(c))
                {
                    ch.AttachCollider();
                    ColliderVersion++;
                }
            }
            foreach (var c in _wantCollider)
            {
                if (!_chunks.TryGetValue(c, out var ch) || !ch || !ch.CanCollide || ch.HasCollider || _bakes.ContainsKey(c) || _jobs.ContainsKey(c)) continue;
                var id = ch.MeshId;
                _bakes[c] = Task.Run(() => Physics.BakeMesh(id, false));
            }

            // 擺設：近的區塊才有；聚落是地標，遠一點就蓋
            int scattered = 0;
            foreach (var kv in _want)
            {
                if (scattered >= scattersPerFrame) break;
                if (!WantsScatter(kv.Key, kv.Value, vc) || !_chunks.TryGetValue(kv.Key, out var ch) || !ch || ch.Lod < 0 || ch.ScatterBuilt) continue;
                _settlementJobs.Clear();
                ch.SetScatter(DSTWorldScatter.Build(Gen, palette, kv.Key, ch.transform, _settlementJobs));
                foreach (var job in _settlementJobs) ch.StartCoroutine(job);   // 區塊卸載時一起停掉
                scattered++;
            }
            foreach (var kv in _chunks)
                if (kv.Value && kv.Value.ScatterBuilt && (!_want.TryGetValue(kv.Key, out int l) || !WantsScatter(kv.Key, l, vc)) && !_wantCollider.Contains(kv.Key))
                {
                    kv.Value.StopAllCoroutines();
                    kv.Value.ClearScatter();
                }

            // 排程：要碰撞的先、近的先
            _queue.Clear();
            foreach (var kv in _want)
            {
                var c = kv.Key;
                int lod = kv.Value;
                if (_jobs.TryGetValue(c, out var running) && running.lod == lod) continue;
                if (_bakes.ContainsKey(c)) continue;   // 烘焙中的網格不能改
                if (_chunks.TryGetValue(c, out var ch) && ch && ch.Lod == lod) continue;
                int ring = Mathf.Max(Mathf.Abs(c.x - vc.x), Mathf.Abs(c.y - vc.y));
                _queue.Add((c, lod, (_wantCollider.Contains(c) ? 0 : 100) + ring));
            }
            if (_queue.Count == 0) return;
            _queue.Sort((a, b) => a.prio.CompareTo(b.prio));
            foreach (var (c, lod, _) in _queue)
            {
                if (_jobs.Count >= maxJobs) break;
                if (_jobs.ContainsKey(c)) continue;
                GetOrCreate(c);
                var gen = Gen;
                _jobs[c] = (lod, Task.Run(() => DSTWorldChunk.Build(gen, c, lod)));
            }
        }

        // 保險：碰撞還沒載入就掉到地底下（傳送、斷線重連、極快的速度）→ 拉回地面。
        // 只處理這台電腦負責物理的東西（自己、自己在開的車、自己拿著物理的箱子）
        void RescueFallen()
        {
            if (Time.frameCount % 10 != 0) return;
            var me = DSTNetPlayer.Local;
            if (me && !me.InVehicle && Below(me.transform.position, 4f, out var ground))
                me.Controller.Teleport(ground + Vector3.up * 0.3f, me.transform.eulerAngles.y);
            foreach (var v in DSTVehicleEntry.All)
                if (v && v.IsOwner && Below(v.transform.position, 4f, out ground))
                    Place(v.transform, ground + Vector3.up * 0.6f, Quaternion.Euler(0f, v.transform.eulerAngles.y, 0f));
            foreach (var p in DSTPhysicsProp.All)
                if (p && p.IsOwner && !p.IsBroken && Below(p.transform.position, 4f, out ground))
                    Place(p.transform, ground + Vector3.up * 0.5f, p.transform.rotation);
        }

        // ------------------------------------------------------------------ 地區提示（左下角「你來到了…」）
        // 走進不同的地區時跳一次：聚落、遺跡馬上跳；大地形要在新地區待 1.5 秒才算（在邊界附近不要一直跳）
        string _region;
        string _candidate;
        float _candidateSince;
        float _nextRegionCheck;
        string _notice, _noticeSub;

        void UpdateRegionNotice()
        {
            if (Time.time < _nextRegionCheck) return;
            _nextRegionCheck = Time.time + 0.25f;
            var p = Viewer();
            string name = Gen.RegionAt(p.x, p.z, out var kind);
            if (name == _region) { _candidate = null; return; }
            if (name != _candidate) { _candidate = name; _candidateSince = Time.time; }
            bool instant = kind == DSTWorldGen.RegionKind.Settlement || kind == DSTWorldGen.RegionKind.Ruins || _region == null;
            if (!instant && Time.time - _candidateSince < 1.5f) return;
            _region = name;
            _candidate = null;
            _notice = name;
            switch (kind)
            {
                case DSTWorldGen.RegionKind.Settlement: _noticeSub = "你來到了聚落"; break;
                case DSTWorldGen.RegionKind.Ruins: _noticeSub = "你發現了"; break;
                default: _noticeSub = "你來到了"; break;
            }
            DSTNotice.Show(_noticeSub, _notice);
        }

        bool Below(Vector3 p, float margin, out Vector3 ground)
        {
            float h = Gen.Height(p.x, p.z);
            ground = new Vector3(p.x, h, p.z);
            return p.y < h - margin;
        }

        readonly List<System.Collections.IEnumerator> _settlementJobs = new List<System.Collections.IEnumerator>();

        bool WantsScatter(Vector2Int c, int lod, Vector2Int viewer)
        {
            if (lod <= 1) return true;
            int ring = Mathf.Max(Mathf.Abs(c.x - viewer.x), Mathf.Abs(c.y - viewer.y));
            return ring <= 5 && Gen.SettlementCenteredIn(c) != null;
        }

        void AddColliderAnchor(Vector3 p)
        {
            // 在聚落裡：房子是聚落中心那個區塊負責蓋的，那塊也要載入（不然這台電腦上沒有房子的碰撞）
            var s = Gen.SettlementContaining(p.x, p.z, 8f);
            if (s != null)
            {
                var sc = ChunkOf(new Vector3(s.center.x, 0f, s.center.y));
                _want[sc] = 0;
                _wantCollider.Add(sc);
            }
            var c = ChunkOf(p);
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                var k = new Vector2Int(c.x + dx, c.y + dz);
                _wantCollider.Add(k);
                _want[k] = 0;   // 有碰撞的區塊一定用最細的網格
            }
        }

        DSTWorldChunk GetOrCreate(Vector2Int c)
        {
            if (_chunks.TryGetValue(c, out var ch) && ch) return ch;
            ch = DSTWorldChunk.Create(_root, c, palette);
            _chunks[c] = ch;
            return ch;
        }
    }
}
