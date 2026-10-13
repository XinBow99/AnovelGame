using System.Collections.Generic;
using DUST.Player;
using Unity.Netcode;
using UnityEngine;

namespace DUST.World
{
    // 隨機世界擺設的「改動清單」：沒被碰過的擺設由種子算出來，不用存；被打碎或推走的才記在這裡。
    // 主機是權威：誰碎了、停在哪；新加入的玩家一連上就收到整份清單。
    // 過了一段時間、附近又沒人的時候，主機把記錄刪掉，東西就回到原本的樣子（沙漠把它們埋回去、又冒出來）。
    // 放在 DSTWorld 同一個物件上（場景裡的連線物件）。
    public class DSTPropSync : NetworkBehaviour
    {
        public const byte Broken = 1, Moved = 2;

        public struct Rec
        {
            public byte state;
            public Vector3 pos;
            public Quaternion rot;
            public Vector3 home;
            public double t;            // 累加遊戲時數
        }

        public static readonly Dictionary<ulong, Rec> State = new Dictionary<ulong, Rec>();
        public static DSTPropSync Instance { get; private set; }

        [Tooltip("碎掉的東西過多久（遊戲小時）會長回來")] public float regrowHours = 18f;
        [Tooltip("被推走的東西過多久（遊戲小時）會回到原位")] public float resetMovedHours = 72f;
        [Tooltip("附近有人就先不要恢復（免得憑空冒出來）")] public float clearRadius = 50f;

        float _nextPrune;

        static bool Online => Instance && Instance.IsSpawned;

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (IsServer) NetworkManager.OnClientConnectedCallback += SendAll;
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && NetworkManager) NetworkManager.OnClientConnectedCallback -= SendAll;
            if (Instance == this) Instance = null;
            State.Clear();
        }

        // ------------------------------------------------------------------ 碎掉
        public static void SendBreak(DSTWorldProp p, Vector3 dir, float speed, Vector3 point, Vector3 vel)
        {
            if (!Online)
            {
                State[p.Id] = new Rec { state = Broken, home = p.Home, t = DSTClockwork.Now };
                p.ApplyBreak(dir, speed, point, vel, Random.Range(0, int.MaxValue));
                return;
            }
            Instance.BreakServerRpc(p.Id, p.Home, dir, speed, point, vel);
        }

        [Rpc(SendTo.Server)]
        void BreakServerRpc(ulong id, Vector3 home, Vector3 dir, float speed, Vector3 point, Vector3 vel)
        {
            if (State.TryGetValue(id, out var r) && r.state == Broken) return;
            State[id] = new Rec { state = Broken, home = home, t = DSTClockwork.Now };
            BreakRpc(id, home, dir, speed, point, vel, Random.Range(0, int.MaxValue));
        }

        [Rpc(SendTo.Everyone)]
        void BreakRpc(ulong id, Vector3 home, Vector3 dir, float speed, Vector3 point, Vector3 vel, int seed)
        {
            State[id] = new Rec { state = Broken, home = home, t = DSTClockwork.Now };
            if (DSTWorldProp.Live.TryGetValue(id, out var p) && p) p.ApplyBreak(dir, speed, point, vel, seed);
        }

        // ------------------------------------------------------------------ 被推動：移動中（不可靠，丟了沒關係）／停下來（可靠，記下來）
        public static void SendPose(DSTWorldProp p, Vector3 pos, Quaternion rot, bool final)
        {
            if (!Online)
            {
                if (final) Record(p.Id, pos, rot, p.Home);
                return;
            }
            if (final) Instance.RestServerRpc(p.Id, pos, rot, p.Home);
            else Instance.MoveServerRpc(p.Id, pos, rot);
        }

        static void Record(ulong id, Vector3 pos, Quaternion rot, Vector3 home)
        {
            if (State.TryGetValue(id, out var r) && r.state == Broken) return;
            if ((pos - home).sqrMagnitude < 1e-4f) { State.Remove(id); return; }
            State[id] = new Rec { state = Moved, pos = pos, rot = rot, home = home, t = DSTClockwork.Now };
        }

        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
        void MoveServerRpc(ulong id, Vector3 pos, Quaternion rot) => MoveRpc(id, pos, rot);

        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Unreliable)]
        void MoveRpc(ulong id, Vector3 pos, Quaternion rot)
        {
            if (DSTWorldProp.Live.TryGetValue(id, out var p) && p) p.NetPose(pos, rot, false);
        }

        [Rpc(SendTo.Server)]
        void RestServerRpc(ulong id, Vector3 pos, Quaternion rot, Vector3 home) => RestRpc(id, pos, rot, home);

        [Rpc(SendTo.Everyone)]
        void RestRpc(ulong id, Vector3 pos, Quaternion rot, Vector3 home)
        {
            Record(id, pos, rot, home);
            if (DSTWorldProp.Live.TryGetValue(id, out var p) && p) p.NetPose(pos, rot, true);
        }

        // ------------------------------------------------------------------ 新加入的玩家：整份清單
        void SendAll(ulong client)
        {
            if (client == NetworkManager.ServerClientId || State.Count == 0) return;
            const int batch = 200;
            var ids = new List<ulong>(); var st = new List<byte>(); var pos = new List<Vector3>(); var rot = new List<Quaternion>(); var home = new List<Vector3>();
            void Flush()
            {
                if (ids.Count == 0) return;
                SyncRpc(ids.ToArray(), st.ToArray(), pos.ToArray(), rot.ToArray(), home.ToArray(), RpcTarget.Single(client, RpcTargetUse.Temp));
                ids.Clear(); st.Clear(); pos.Clear(); rot.Clear(); home.Clear();
            }
            foreach (var kv in State)
            {
                ids.Add(kv.Key); st.Add(kv.Value.state); pos.Add(kv.Value.pos); rot.Add(kv.Value.rot); home.Add(kv.Value.home);
                if (ids.Count >= batch) Flush();
            }
            Flush();
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void SyncRpc(ulong[] ids, byte[] st, Vector3[] pos, Quaternion[] rot, Vector3[] home, RpcParams rpc)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                State[ids[i]] = new Rec { state = st[i], pos = pos[i], rot = rot[i], home = home[i], t = DSTClockwork.Now };
                if (!DSTWorldProp.Live.TryGetValue(ids[i], out var p) || !p) continue;
                if (st[i] == Broken) p.HideBroken();
                else p.NetPose(pos[i], rot[i], true);
            }
        }

        // ------------------------------------------------------------------ 恢復（主機）
        [Rpc(SendTo.Everyone)]
        void RestoreRpc(ulong id)
        {
            State.Remove(id);
            if (DSTWorldProp.Live.TryGetValue(id, out var p) && p) p.Restore();
        }

        readonly List<ulong> _expired = new List<ulong>();

        void Update()
        {
            if (!IsSpawned || !IsServer || Time.time < _nextPrune) return;
            _nextPrune = Time.time + 5f;
            double now = DSTClockwork.Now;
            _expired.Clear();
            foreach (var kv in State)
            {
                double age = now - kv.Value.t;
                if (age < (kv.Value.state == Broken ? regrowHours : resetMovedHours)) continue;
                if (PlayerNear(kv.Value.home) || (kv.Value.state == Moved && PlayerNear(kv.Value.pos))) continue;
                _expired.Add(kv.Key);
            }
            foreach (var id in _expired) RestoreRpc(id);
        }

        bool PlayerNear(Vector3 p)
        {
            foreach (var pl in DSTNetPlayer.All)
                if (pl && (pl.transform.position - p).sqrMagnitude < clearRadius * clearRadius) return true;
            return false;
        }
    }
}
