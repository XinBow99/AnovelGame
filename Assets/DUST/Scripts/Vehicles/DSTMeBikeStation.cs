using System.Collections.Generic;
using DUST.Player;
using DUST.World;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace DUST.Vehicles
{
    // MeBike 站：一排車柱（一柱停一台）。主機進場時在每根車柱生成一台 MeBike（連線物件）。
    // 調度：沒人騎、離站很遠、附近也沒玩家的車，放一陣子後主機會把它「載回」空的車柱（跟真的公共自行車調度車一樣）。
    public class DSTMeBikeStation : MonoBehaviour
    {
        public GameObject bikePrefab;
        public Transform[] docks;
        public string stationName = "MeBike 站";
        public float rebalanceAfter = 90f;        // 被丟在外面多久就載回來
        public float rebalanceClearRadius = 60f;  // 附近有玩家就先不要載

        public static readonly List<DSTMeBikeStation> All = new List<DSTMeBikeStation>();

        // 停進車柱時，車子要在車柱前面多遠（前輪卡進卡榫）
        public const float DockOffset = 0.34f;

        readonly List<NetworkObject> _bikes = new List<NetworkObject>();
        readonly Dictionary<NetworkObject, float> _strayedAt = new Dictionary<NetworkObject, float>();
        bool _spawned;
        float _nextCheck;

        void OnEnable()
        {
            All.Add(this);
            foreach (var v in DSTVehicleEntry.All)
                if (v && v.Drivable is DSTBikeController) IgnoreDocksFor(v.GetComponentsInChildren<Collider>());
        }

        // 車柱的卡榫本來就要讓前輪插進去：MeBike 不跟車柱碰撞（不然停著會一直被推出去、慢慢轉歪）
        public static void IgnoreDockCollisions(Collider[] bikeColliders)
        {
            foreach (var st in All) if (st) st.IgnoreDocksFor(bikeColliders);
        }

        void IgnoreDocksFor(Collider[] bikeColliders)
        {
            if (docks == null) return;
            foreach (var d in docks)
                if (d && d.TryGetComponent<Collider>(out var dc))
                    foreach (var c in bikeColliders) if (c) Physics.IgnoreCollision(c, dc);
        }

        void OnDisable()
        {
            All.Remove(this);
            _bikes.Clear();
            _strayedAt.Clear();
            _spawned = false;
        }

        public static void DockPose(Transform dock, out Vector3 pos, out Quaternion rot)
        {
            pos = dock.position + dock.forward * DockOffset;
            rot = Quaternion.LookRotation(-dock.forward, Vector3.up);   // 車頭朝車柱
        }

        // 還車：找附近空著的車柱（不算 self）
        public static bool TryFindFreeDock(Vector3 near, float radius, Component self, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = default;
            float best = radius;
            bool found = false;
            foreach (var st in All)
            {
                if (!st || st.docks == null) continue;
                foreach (var d in st.docks)
                {
                    if (!d) continue;
                    DockPose(d, out var p, out var r);
                    float dist = Vector3.Distance(Flat(near), Flat(p));
                    if (dist > best || Occupied(p, self)) continue;
                    best = dist; pos = p; rot = r; found = true;
                }
            }
            return found;
        }

        public static bool IsDocked(Vector3 p)
        {
            foreach (var st in All)
            {
                if (!st || st.docks == null) continue;
                foreach (var d in st.docks)
                {
                    if (!d) continue;
                    DockPose(d, out var dp, out _);
                    if (Vector3.Distance(Flat(dp), Flat(p)) < 0.35f) return true;
                }
            }
            return false;
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        static bool Occupied(Vector3 p, Component self)
        {
            foreach (var v in DSTVehicleEntry.All)
                if (v && v.Drivable is DSTBikeController && (!self || v.gameObject != self.gameObject) && Vector3.Distance(Flat(v.transform.position), Flat(p)) < 0.3f)
                    return true;
            return false;
        }

        void Update()
        {
            var nm = NetworkManager.Singleton;
            if (!nm || !nm.IsListening)
            {
                _spawned = false;
                _bikes.Clear();
                return;
            }
            if (!nm.IsServer || !DSTWorld.IsReady || !bikePrefab) return;

            if (!_spawned)
            {
                _spawned = true;
                foreach (var d in docks)
                {
                    if (!d) continue;
                    DockPose(d, out var p, out var r);
                    var go = Instantiate(bikePrefab, p, r);
                    var no = go.GetComponent<NetworkObject>();
                    no.Spawn(true);
                    _bikes.Add(no);
                }
            }

            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + 5f;
            Rebalance();
        }

        void Rebalance()
        {
            for (int i = _bikes.Count - 1; i >= 0; i--)
            {
                var no = _bikes[i];
                if (!no || !no.IsSpawned) { _bikes.RemoveAt(i); continue; }
                var entry = no.GetComponent<DSTVehicleEntry>();
                bool atStation = IsDocked(no.transform.position);   // 沒停進車柱的（被撞歪、亂停）也會被整理回去
                bool playerNear = false;
                foreach (var p in DSTNetPlayer.All)
                    if (p && Vector3.Distance(p.transform.position, no.transform.position) < rebalanceClearRadius) playerNear = true;

                if (atStation || entry.HasDriver || playerNear) { _strayedAt.Remove(no); continue; }
                if (!_strayedAt.TryGetValue(no, out float since)) { _strayedAt[no] = Time.time; continue; }
                if (Time.time - since < rebalanceAfter) continue;

                foreach (var d in docks)
                {
                    if (!d) continue;
                    DockPose(d, out var pos, out var rot);
                    if (Occupied(pos, no)) continue;
                    no.GetComponent<DSTBikeController>().Place(pos, rot);
                    _strayedAt.Remove(no);
                    break;
                }
            }
        }
    }
}
