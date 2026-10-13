using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace DUST.Animals
{
    // 程式生成物種的生成器（只在主機跑）：在玩家周圍看不到的地方，依那個地點的地區名冊挑物種，
    // 只挑「現在是活動時間」「地形合適」「數量還沒滿」的。離玩家太遠、或過了活動時間又沒人看著，就讓牠離開。
    public class DSTProcSpawner : MonoBehaviour
    {
        public GameObject prefab;
        public float tickInterval = 2f;
        public int attemptsPerTick = 6;
        public int maxTotal = 24;
        public float minSpawnDistance = 16f;
        public float maxSpawnDistance = 45f;
        public float despawnDistance = 75f;
        public float sleepDistance = 25f;      // 過了活動時間：離最近的玩家這麼遠就離開

        public static DSTProcSpawner Instance { get; private set; }

        readonly Dictionary<int, List<DSTAnimal>> _alive = new Dictionary<int, List<DSTAnimal>>();
        readonly List<Vector3> _players = new List<Vector3>();
        float _nextTick;
        bool _populated;

        void OnEnable() => Instance = this;
        void OnDisable() { if (Instance == this) Instance = null; }

        static bool IsServer => NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

        public int Alive(int key) => _alive.TryGetValue(key, out var l) ? l.Count : 0;

        int Total()
        {
            int n = 0;
            foreach (var l in _alive.Values) n += l.Count;
            return n;
        }

        void Update()
        {
            if (!IsServer || !prefab) { _populated = false; return; }
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + tickInterval;
            DSTThreats.Positions(_players);
            if (_players.Count == 0) { _populated = false; return; }
            if (!_populated) for (int i = 0; i < 4; i++) Tick(true);
            _populated = true;
            Tick(false);
        }

        void Tick(bool ignoreVisibility)
        {
            var cam = Camera.main;
            float hour = DSTEcology.Hour;

            // 離開：太遠，或過了活動時間（都要沒人看著）
            foreach (var kv in _alive)
            {
                var g = DSTGenome.Get(kv.Key);
                var list = kv.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var a = list[i];
                    if (!a) { list.RemoveAt(i); continue; }
                    if (!a.IsAlive) continue;
                    float d = NearestPlayer(a.transform.position);
                    bool asleep = !DSTEcology.ActiveAt(g.activity, hour) && d > sleepDistance;
                    if ((d > despawnDistance || asleep) && !DSTAnimalSpawner.Visible(cam, a.transform.position)) a.Remove();
                }
            }

            for (int n = 0; n < attemptsPerTick && Total() < maxTotal; n++)
            {
                var focus = _players[Random.Range(0, _players.Count)];
                float r = Random.Range(minSpawnDistance, maxSpawnDistance);
                var dir = Random.insideUnitCircle.normalized;
                if (!NavMesh.SamplePosition(focus + new Vector3(dir.x, 0f, dir.y) * r, out var hit, 3f, NavMesh.AllAreas)) continue;
                var p = hit.position;
                if (NearestPlayer(p) < minSpawnDistance) continue;
                if (!ignoreVisibility && DSTAnimalSpawner.Visible(cam, p)) continue;

                // 那個地點的名冊裡，挑一個現在醒著、地形合適、還沒滿的
                var roster = DSTEcology.At(p);
                int start = Random.Range(0, roster.keys.Length);
                for (int j = 0; j < roster.keys.Length; j++)
                {
                    int key = roster.keys[(start + j) % roster.keys.Length];
                    var g = DSTGenome.Get(key);
                    if (!DSTEcology.ActiveAt(g.activity, hour) || Alive(key) >= g.maxAlive || !DSTEcology.TerrainOk(g.terrain, p)) continue;
                    SpawnGroup(key, p, Random.Range(g.group.x, g.group.y + 1));
                    break;
                }
            }
        }

        public void SpawnGroup(int key, Vector3 p, int count)
        {
            var g = DSTGenome.Get(key);
            for (int i = 0; i < count && Alive(key) < g.maxAlive + 2; i++)
            {
                var gp = p;
                if (i > 0 && !DSTAnimal.RandomPoint(p, 1.5f + g.size * 2f, out gp)) gp = p;
                Spawn(key, gp, Random.Range(0f, 360f));
            }
        }

        public DSTAnimal Spawn(int key, Vector3 position, float yaw)
        {
            var go = Instantiate(prefab, position, Quaternion.Euler(0f, yaw, 0f));
            go.GetComponent<DSTProcAnimal>().SetKey(key);
            var a = go.GetComponent<DSTAnimal>();
            if (!_alive.TryGetValue(key, out var list)) _alive[key] = list = new List<DSTAnimal>();
            list.Add(a);
            a.Removed += dead => list.Remove(dead);
            var no = go.GetComponent<NetworkObject>();
            if (no && NetworkManager.Singleton && NetworkManager.Singleton.IsServer) no.Spawn(true);
            return a;
        }

        float NearestPlayer(Vector3 p)
        {
            float best = float.MaxValue;
            foreach (var q in _players) best = Mathf.Min(best, Vector3.Distance(p, q));
            return best;
        }
    }
}
