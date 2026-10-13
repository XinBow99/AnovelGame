using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace DUST.Animals
{
    // Minecraft 式生成：在玩家周圍的環狀範圍、玩家看不到的地方生成；離太遠又不在畫面裡就移除
    // 連線：只有主機生成 / 移除，距離以「最近的玩家」為準；可見性只檢查主機自己的鏡頭
    public class DSTAnimalSpawner : MonoBehaviour
    {
        public List<DSTSpeciesDefinition> species = new List<DSTSpeciesDefinition>();
        public float tickInterval = 2f;
        public int attemptsPerTick = 6;
        public bool populateOnStart = true;
        public bool showDebugCounts = false;

        readonly Dictionary<DSTSpeciesDefinition, List<DSTAnimal>> _alive = new Dictionary<DSTSpeciesDefinition, List<DSTAnimal>>();
        readonly List<Vector3> _players = new List<Vector3>();
        float _nextTick;
        bool _populated;

        public int CountAlive(DSTSpeciesDefinition s) => _alive.TryGetValue(s, out var l) ? l.Count : 0;

        static bool IsServer => NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

        void Update()
        {
            if (!IsServer) { _populated = false; return; }
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + tickInterval;

            DSTThreats.Positions(_players);
            if (_players.Count == 0) { _populated = false; return; }
            // 第一個玩家進場時先放一部分族群，不然整張地圖一開始是空的
            if (!_populated && populateOnStart)
                for (int i = 0; i < 4; i++) Tick(ignoreVisibility: true);
            _populated = true;
            Tick(false);
        }

        public void Tick(bool ignoreVisibility)
        {
            DSTThreats.Positions(_players);
            if (_players.Count == 0) return;
            var cam = Camera.main;

            foreach (var s in species)
            {
                if (s == null || !s.spawns || s.prefab == null) continue;
                var list = List(s);

                // 移除：離每個玩家都太遠而且看不到
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var a = list[i];
                    if (a == null) { list.RemoveAt(i); continue; }
                    if (a.IsAlive && NearestPlayer(a.transform.position) > s.despawnDistance && !Visible(cam, a.transform.position))
                        a.Remove();
                }

                // 生成：輪流在某個玩家周圍找點
                int attempts = attemptsPerTick;
                while (list.Count < s.maxAlive && attempts-- > 0)
                {
                    var focus = _players[Random.Range(0, _players.Count)];
                    if (!TryFindSpawnPoint(s, focus, cam, ignoreVisibility, out var p)) continue;
                    int group = Random.Range(s.groupSize.x, s.groupSize.y + 1);
                    for (int g = 0; g < group && list.Count < s.maxAlive; g++)
                    {
                        Vector3 gp = p;
                        if (g > 0 && !DSTAnimal.RandomPoint(p, 2.5f, out gp)) gp = p;
                        Spawn(s, gp, Random.Range(0f, 360f));
                    }
                }
            }
        }

        float NearestPlayer(Vector3 p)
        {
            float best = float.MaxValue;
            foreach (var q in _players) best = Mathf.Min(best, Vector3.Distance(p, q));
            return best;
        }

        bool TryFindSpawnPoint(DSTSpeciesDefinition s, Vector3 focus, Camera cam, bool ignoreVisibility, out Vector3 point)
        {
            point = default;
            float r = Random.Range(s.minSpawnDistance, s.maxSpawnDistance);
            Vector2 dir = Random.insideUnitCircle.normalized;
            Vector3 candidate = focus + new Vector3(dir.x, 0f, dir.y) * r;
            if (!NavMesh.SamplePosition(candidate, out var hit, 3f, NavMesh.AllAreas)) return false;
            // 不能生在任何一個玩家旁邊
            if (NearestPlayer(hit.position) < s.minSpawnDistance) return false;
            if (!ignoreVisibility && Visible(cam, hit.position)) return false;
            // 要能從玩家那邊走到（不要生成在封死的屋頂或角落）
            var path = new NavMeshPath();
            if (NavMesh.SamplePosition(focus, out var fh, 3f, NavMesh.AllAreas) &&
                (!NavMesh.CalculatePath(fh.position, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete))
                return false;
            point = hit.position;
            return true;
        }

        internal static bool Visible(Camera cam, Vector3 p)
        {
            if (cam == null) return false;
            Vector3 vp = cam.WorldToViewportPoint(p + Vector3.up * 0.3f);
            if (vp.z <= 0f || vp.x < -0.05f || vp.x > 1.05f || vp.y < -0.05f || vp.y > 1.05f) return false;
            return !Physics.Linecast(cam.transform.position, p + Vector3.up * 0.3f, ~0, QueryTriggerInteraction.Ignore);
        }

        public DSTAnimal Spawn(DSTSpeciesDefinition s, Vector3 position, float yaw)
        {
            var go = Instantiate(s.prefab, position, Quaternion.Euler(0f, yaw, 0f));
            var a = go.GetComponent<DSTAnimal>();
            a.species = s;
            var list = List(s);
            list.Add(a);
            a.Removed += dead => list.Remove(dead);
            var no = go.GetComponent<NetworkObject>();
            if (no && NetworkManager.Singleton && NetworkManager.Singleton.IsServer) no.Spawn(true);
            return a;
        }

        List<DSTAnimal> List(DSTSpeciesDefinition s)
        {
            if (!_alive.TryGetValue(s, out var l)) _alive[s] = l = new List<DSTAnimal>();
            return l;
        }

        void OnGUI()
        {
            if (!showDebugCounts) return;
            string t = "";
            foreach (var s in species) if (s) t += $"{s.displayName}: {CountAlive(s)}/{s.maxAlive}\n";
            GUI.Label(new Rect(10, 10, 300, 100), t);
        }
    }
}
