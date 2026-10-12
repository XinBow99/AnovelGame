using System.Collections.Generic;
using DUST.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace DUST.World
{
    // 隨機世界的導航網格：只有主機需要（動物 AI 只在主機跑）。
    // 每個玩家周圍一塊（size × size），玩家走遠了或有新的地形碰撞載入時，在背景重建。
    [RequireComponent(typeof(DSTWorld))]
    public class DSTWorldNavMesh : MonoBehaviour
    {
        public Vector3 size = new Vector3(130f, 90f, 130f);
        public float moveThreshold = 24f;         // 玩家離上次的中心多遠就重建
        public float minRebuildInterval = 3f;

        class Area
        {
            public NavMeshData data;
            public NavMeshDataInstance instance;
            public Vector3 center;
            public AsyncOperation op;
            public int colliderVersion;
            public float lastBuild;
        }

        DSTWorld _world;
        readonly Dictionary<DSTNetPlayer, Area> _areas = new Dictionary<DSTNetPlayer, Area>();
        readonly List<NavMeshBuildSource> _sources = new List<NavMeshBuildSource>();
        readonly List<NavMeshBuildMarkup> _markups = new List<NavMeshBuildMarkup>();
        readonly List<DSTNetPlayer> _gone = new List<DSTNetPlayer>();

        void Awake() => _world = GetComponent<DSTWorld>();

        static bool IsServer => NetworkManager.Singleton && NetworkManager.Singleton.IsServer;

        void Update()
        {
            if (!_world.IsRandom || !IsServer || !_world.Root)
            {
                if (_areas.Count > 0) Clear();
                return;
            }

            foreach (var p in DSTNetPlayer.All)
            {
                if (!p) continue;
                if (!_areas.TryGetValue(p, out var a))
                {
                    a = new Area { data = new NavMeshData(0), lastBuild = -999f };
                    a.instance = NavMesh.AddNavMeshData(a.data);
                    a.center = new Vector3(float.MaxValue, 0f, 0f);
                    _areas[p] = a;
                }
                if (a.op != null && !a.op.isDone) continue;
                bool moved = Vector3.Distance(Flat(p.transform.position), Flat(a.center)) > moveThreshold;
                bool newGround = a.colliderVersion != _world.ColliderVersion;
                if (!moved && !newGround) continue;
                if (!moved && Time.time - a.lastBuild < minRebuildInterval) continue;
                Rebuild(a, p.transform.position);
            }

            _gone.Clear();
            foreach (var kv in _areas) if (!kv.Key) _gone.Add(kv.Key);
            foreach (var p in _gone)
            {
                _areas[p].instance.Remove();
                _areas.Remove(p);
            }
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        void Rebuild(Area a, Vector3 around)
        {
            a.center = new Vector3(Mathf.Round(around.x / 8f) * 8f, around.y, Mathf.Round(around.z / 8f) * 8f);
            a.colliderVersion = _world.ColliderVersion;
            a.lastBuild = Time.time;
            var bounds = new Bounds(a.center, size);
            _sources.Clear();
            // 只收地形與擺設（世界根物件底下）；車子、箱子、玩家會動，不烘進去。
            // 地形是執行期建的網格（可讀）；石頭、牆是匯入的模型（建置版不能讀網格）→ 用貼合的方盒代替
            foreach (var ch in _world.Root.GetComponentsInChildren<DSTWorldChunk>())
            {
                if (!ch.HasCollider) continue;
                var mf = ch.GetComponent<MeshFilter>();
                if (!mf.sharedMesh || !bounds.Intersects(ch.GetComponent<Renderer>().bounds)) continue;
                _sources.Add(new NavMeshBuildSource { shape = NavMeshBuildSourceShape.Mesh, sourceObject = mf.sharedMesh, transform = ch.transform.localToWorldMatrix, area = 0 });
                var scatter = ch.ScatterRoot;
                if (!scatter) continue;
                foreach (var f in scatter.GetComponentsInChildren<MeshFilter>())
                {
                    if (!f.sharedMesh || !f.GetComponent<Collider>()) continue;
                    var lb = f.sharedMesh.bounds;
                    var t = f.transform;
                    _sources.Add(new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.Box,
                        size = lb.size,
                        transform = Matrix4x4.TRS(t.TransformPoint(lb.center), t.rotation, t.lossyScale),
                        area = 0,
                    });
                }
            }
            var settings = NavMesh.GetSettingsByID(0);
            a.op = NavMeshBuilder.UpdateNavMeshDataAsync(a.data, settings, _sources, bounds);
        }

        void Clear()
        {
            foreach (var a in _areas.Values) a.instance.Remove();
            _areas.Clear();
        }

        void OnDisable() => Clear();
    }
}
