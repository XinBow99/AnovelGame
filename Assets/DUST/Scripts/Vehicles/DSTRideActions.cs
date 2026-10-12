using System.Collections.Generic;
using DUST.Player;
using Unity.Netcode;
using UnityEngine;

namespace DUST.Vehicles
{
    // 鬃狼的上下車「動作系統」：
    //   主機依情況（車門開關、站在哪一側、旁邊有沒有空間、上次用過哪個）加權隨機挑一個上 / 下車動作，再給一個隨機種子；
    //   種子決定：走過去的速度、要不要先遲疑（聞一聞 / 豎耳）、播放快慢、接合時間、騎乘中小動作出現的時機。
    //   這些都放在一個連線變數裡，每台電腦用同一個種子算出同樣的結果，中途加入的人也看得到正確的狀態。
    [RequireComponent(typeof(DSTVehicleEntry))]
    public class DSTRideActions : NetworkBehaviour
    {
        public DSTActionLibrary library;
        public DSTVehicleKind kind;
        public Transform anchor;                // 動作片段的座標原點（皮卡 / 無人機 = 車身、MeBike = 會傾斜的 Visual）
        public Transform door;                  // 左前門（只有皮卡）
        public Vector3 doorAxis = Vector3.up;
        public float doorSign = 1f;

        public enum Phase : byte { None, Approach, Enter, Exit }

        public struct State : INetworkSerializable, System.IEquatable<State>
        {
            public Phase phase;
            public short clip;
            public int seed;
            public double start;                // 主機時間
            public ulong driver;

            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref phase); s.SerializeValue(ref clip); s.SerializeValue(ref seed);
                s.SerializeValue(ref start); s.SerializeValue(ref driver);
            }
            public bool Equals(State o) => phase == o.phase && clip == o.clip && seed == o.seed && start == o.start && driver == o.driver;
        }

        readonly NetworkVariable<State> _state = new NetworkVariable<State>();
        readonly NetworkVariable<float> _doorAngle = new NetworkVariable<float>();

        public Phase CurrentPhase => _state.Value.phase;
        /// <summary>正在走過去 / 上下車（還不能開）</summary>
        public bool Busy => _state.Value.phase != Phase.None && !_riding;
        public float DoorAngle => _doorAngle.Value;

        DSTVehicleEntry _entry;
        DSTVehicleController _car;
        DSTBikeController _bike;
        Quaternion _doorRest;
        Collider[] _ownColliders;

        // 本地播放狀態
        State _applied;
        DSTNetPlayer _driverPlayer;
        DSTWolfRig _rig;
        Plan _plan;
        bool _clipStarted, _riding, _exitDone;
        float _exitWait;
        double _nextFidget;
        System.Random _fidgetRng;
        // 走過去（只有駕駛自己那台電腦）
        readonly List<Vector3> _path = new List<Vector3>();
        float _approachUntil;

        // 主機：每個玩家上次用過的片段，避免連續一樣
        readonly Dictionary<ulong, int> _lastClip = new Dictionary<ulong, int>();

        // 由種子算出來、每台電腦都一樣的參數
        struct Plan
        {
            public DSTActionClip clip;
            public float speed;          // 播放速度
            public float hesitate;       // 開始前遲疑幾秒
            public bool sniff;           // 遲疑時聞一聞（否則豎耳張望）
            public float warp;           // 從玩家位置接到片段起點的時間
            public float approachSpeed;  // 走過去的速度（m/s）
            public float Duration => clip.Length / speed;
        }

        static Plan MakePlan(DSTActionClip c, int seed, bool enter)
        {
            var r = new System.Random(seed);
            var p = new Plan { clip = c };
            p.speed = Mathf.Lerp(0.9f, 1.12f, (float)r.NextDouble());
            if (enter && r.NextDouble() < 0.55)
            {
                p.hesitate = Mathf.Lerp(0.5f, 1.3f, (float)r.NextDouble());
                p.sniff = r.NextDouble() < 0.45;
            }
            p.warp = Mathf.Lerp(0.35f, 0.7f, (float)r.NextDouble());
            p.approachSpeed = Mathf.Lerp(1.1f, 1.7f, (float)r.NextDouble());
            return p;
        }

        void Awake()
        {
            _entry = GetComponent<DSTVehicleEntry>();
            _car = GetComponent<DSTVehicleController>();
            _bike = GetComponent<DSTBikeController>();
            if (!anchor) anchor = transform;
            if (door) _doorRest = door.localRotation;
            _ownColliders = GetComponentsInChildren<Collider>(true);
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer) _state.Value = default;
            _applied = default;
        }

        double Now => NetworkManager ? NetworkManager.ServerTime.Time : Time.timeAsDouble;

        // ================================================================== 上車
        /// <summary>玩家按 F：請主機挑動作</summary>
        public void RequestEnter(DSTPlayerController player)
        {
            if (_state.Value.phase != Phase.None) return;
            var t = player.transform;
            RequestEnterRpc(t.position, t.eulerAngles.y);
        }

        [Rpc(SendTo.Server)]
        void RequestEnterRpc(Vector3 pos, float yaw, RpcParams rpc = default)
        {
            ulong id = rpc.Receive.SenderClientId;
            if (_state.Value.phase != Phase.None || _entry.HasDriver) return;
            var clip = ChooseEnter(pos, id);
            if (clip == null) return;
            if (!_entry.ServerAssignDriver(id)) return;
            int seed = Random.Range(int.MinValue, int.MaxValue);
            _lastClip[id] = library.IndexOf(clip);
            _state.Value = new State { phase = Phase.Approach, clip = (short)library.IndexOf(clip), seed = seed, start = Now, driver = id };
        }

        /// <summary>駕駛走到起點了（或本來就在旁邊）：開始播</summary>
        [Rpc(SendTo.Server)]
        void StartEnterRpc(RpcParams rpc = default)
        {
            var s = _state.Value;
            if (s.phase != Phase.Approach || s.driver != rpc.Receive.SenderClientId) return;
            s.phase = Phase.Enter;
            s.start = Now;
            _state.Value = s;
            var plan = MakePlan(library.At(s.clip), s.seed, true);
            if (plan.clip.HasDoor) _doorAngle.Value = plan.clip.doorAngle.Evaluate(plan.clip.Length);
        }

        DSTActionClip ChooseEnter(Vector3 playerPos, ulong id)
        {
            if (!library) return null;
            var local = anchor.InverseTransformPoint(playerPos);
            var side = SideOf(local);
            bool doorOpen = _doorAngle.Value > 30f;
            var list = new List<(DSTActionClip c, float w)>();
            foreach (var c in library.Of(kind, DSTActionKind.Enter))
            {
                if (c.door == DSTDoorNeed.Closed && doorOpen) continue;
                if (c.door == DSTDoorNeed.Open && !doorOpen) continue;
                float w = c.weight;
                if (c.side == side) w *= 2.5f;                       // 站在哪邊就比較常從那邊上
                if (!Clear(anchor.TransformPoint(c.startPos), c.clearance * 0.5f)) w *= 0.15f;
                if (_lastClip.TryGetValue(id, out int last) && last == library.IndexOf(c)) w *= 0.3f;   // 不要每次都一樣
                list.Add((c, w));
            }
            return Pick(list);
        }

        // ================================================================== 下車
        public void RequestExit()
        {
            if (_state.Value.phase != Phase.Enter || !_riding) return;
            RequestExitRpc();
        }

        [Rpc(SendTo.Server)]
        void RequestExitRpc(RpcParams rpc = default)
        {
            ulong id = rpc.Receive.SenderClientId;
            var s = _state.Value;
            if (s.driver != id || s.phase != Phase.Enter) return;
            bool doorOpen = _doorAngle.Value > 30f;
            var list = new List<(DSTActionClip c, float w)>();
            var fallback = new List<(DSTActionClip c, float w)>();
            foreach (var c in library.Of(kind, DSTActionKind.Exit))
            {
                float w = c.weight;
                if (c.door == DSTDoorNeed.Closed && doorOpen) w *= 0.4f;   // 門開著也能用（只是推門那段比較少見）
                if (_lastClip.TryGetValue(id, out int last) && last == library.IndexOf(c)) w *= 0.3f;
                fallback.Add((c, w));
                if (Clear(anchor.TransformPoint(c.endPos), c.clearance * 0.5f)) list.Add((c, w));
            }
            var clip = Pick(list.Count > 0 ? list : fallback);
            if (clip == null) return;
            _lastClip[id] = library.IndexOf(clip);
            int seed = Random.Range(int.MinValue, int.MaxValue);
            _state.Value = new State { phase = Phase.Exit, clip = (short)library.IndexOf(clip), seed = seed, start = Now, driver = id };
            if (clip.HasDoor) _doorAngle.Value = clip.doorAngle.Evaluate(clip.Length);
        }

        [Rpc(SendTo.Server)]
        void ExitDoneRpc(RpcParams rpc = default)
        {
            var s = _state.Value;
            if (s.driver != rpc.Receive.SenderClientId) return;
            _state.Value = default;
            _entry.ServerRelease();
        }

        /// <summary>主機：駕駛斷線 / 被踢掉時清掉狀態</summary>
        public void ServerClear()
        {
            if (!IsServer) return;
            _state.Value = default;
        }

        // ================================================================== 選擇工具
        DSTActionSide SideOf(Vector3 local)
        {
            var b = LocalBounds();
            if (local.z < b.min.z - 0.05f && Mathf.Abs(local.x) < b.extents.x + 0.2f) return DSTActionSide.Rear;
            return local.x < 0f ? DSTActionSide.Left : DSTActionSide.Right;
        }

        DSTActionClip Pick(List<(DSTActionClip c, float w)> list)
        {
            float total = 0f;
            foreach (var e in list) total += Mathf.Max(0f, e.w);
            if (total <= 0f) return list.Count > 0 ? list[0].c : null;
            float r = Random.value * total;
            foreach (var e in list)
            {
                r -= Mathf.Max(0f, e.w);
                if (r <= 0f) return e.c;
            }
            return list[list.Count - 1].c;
        }

        bool Clear(Vector3 groundPoint, float radius)
        {
            var a = groundPoint + Vector3.up * (radius + 0.06f);
            var b = groundPoint + Vector3.up * 0.45f;
            foreach (var c in Physics.OverlapCapsule(a, b, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                if (System.Array.IndexOf(_ownColliders, c) >= 0) continue;
                if (c is CharacterController) continue;      // 其他玩家會讓開
                if (c.attachedRigidbody && c.attachedRigidbody.GetComponent<DSTNetPlayer>()) continue;
                return false;
            }
            return true;
        }

        Bounds LocalBounds()
        {
            var b = new Bounds(Vector3.zero, Vector3.zero);
            bool first = true;
            foreach (var c in _ownColliders)
            {
                if (!c || c.isTrigger || !c.enabled) continue;
                foreach (var p in Corners(c.bounds))
                {
                    var l = anchor.InverseTransformPoint(p);
                    if (first) { b = new Bounds(l, Vector3.zero); first = false; } else b.Encapsulate(l);
                }
            }
            return b;
        }

        static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
                yield return new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
        }

        // ================================================================== 每一幀：照狀態播放
        void Update()
        {
            if (!IsSpawned || !library) return;
            var s = _state.Value;
            if (!s.Equals(_applied)) OnStateChanged(_applied, s);

            switch (s.phase)
            {
                case Phase.Approach: TickApproach(s); break;
                case Phase.Enter: TickEnter(s); break;
                case Phase.Exit: TickExit(s); break;
            }
            UpdateDoor(s);

            // 主機：駕駛已經不在了（斷線）就清掉
            if (IsServer && s.phase != Phase.None && !_entry.HasDriver) _state.Value = default;
        }

        void OnStateChanged(State from, State to)
        {
            _applied = to;
            _clipStarted = false;
            _exitDone = false;
            _exitWait = 0f;
            if (to.phase == Phase.None)
            {
                // 下車做完（或中斷）：模型還掛在這台車上的話交回去
                if (_rig && _rig.InAction && _rig.model.parent == anchor) _rig.ReturnToLocomotion(0.15f);
                _riding = false;
                _driverPlayer = null; _rig = null;
                return;
            }
            _driverPlayer = FindPlayer(to.driver);
            _rig = _driverPlayer ? _driverPlayer.GetComponent<DSTWolfRig>() : null;
            _plan = MakePlan(library.At(to.clip), to.seed, to.phase != Phase.Exit);
            if (to.phase == Phase.Approach) BeginApproach(to);
            if (to.phase == Phase.Exit)
            {
                _riding = false;
                if (IsLocalDriver(to)) _entry.StopDriving(this);
                ForceView(true);
            }
        }

        static DSTNetPlayer FindPlayer(ulong id)
        {
            foreach (var p in DSTNetPlayer.All)
                if (p && p.OwnerClientId == id) return p;
            return null;
        }

        bool IsLocalDriver(State s) => NetworkManager && s.driver == NetworkManager.LocalClientId && _driverPlayer && _driverPlayer.IsOwner;

        void ForceView(bool on)
        {
            if (!_driverPlayer || !_driverPlayer.IsOwner || !_rig) return;
            var view = _driverPlayer.GetComponent<DSTPlayerView>();
            if (!view) return;
            if (on) { _driverPlayer.SetViewCamera(true); view.ForceThirdPerson(_rig.Hips, anchor); }
            else view.ReleaseForce();
        }

        // ------------------------------------------------------------------ 走過去
        void BeginApproach(State s)
        {
            if (!IsLocalDriver(s)) return;
            _driverPlayer.BeginScripted();
            ForceView(true);
            if (_bike) _bike.PullOutIfDocked();       // MeBike：先從車柱牽出來，鬃狼再走過去
            _path.Clear();
            var start = StartWorld(_plan.clip, out _);
            var from = _driverPlayer.transform.position;
            // 起點在車子另一側：從車頭或車尾繞過去（不要穿過車子）
            var b = LocalBounds();
            b.Expand(new Vector3(0.5f, 0f, 0.5f));
            var a = anchor.InverseTransformPoint(from); var t = anchor.InverseTransformPoint(start);
            if (SegmentHitsBox(new Vector2(a.x, a.z), new Vector2(t.x, t.z), b))
            {
                var front = new[] { new Vector3(b.min.x, 0, b.max.z), new Vector3(b.max.x, 0, b.max.z) };
                var back = new[] { new Vector3(b.min.x, 0, b.min.z), new Vector3(b.max.x, 0, b.min.z) };
                float viaFront = RouteLength(a, t, front), viaBack = RouteLength(a, t, back);
                // 一樣遠就看種子
                bool useFront = Mathf.Abs(viaFront - viaBack) < 0.3f ? (s.seed & 1) == 0 : viaFront < viaBack;
                foreach (var c in Ordered(a, useFront ? front : back)) _path.Add(anchor.TransformPoint(c));
            }
            _path.Add(start);
            _approachUntil = Time.time + 9f;
        }

        static IEnumerable<Vector3> Ordered(Vector3 from, Vector3[] corners)
        {
            if ((corners[0] - from).sqrMagnitude <= (corners[1] - from).sqrMagnitude) { yield return corners[0]; yield return corners[1]; }
            else { yield return corners[1]; yield return corners[0]; }
        }

        static float RouteLength(Vector3 a, Vector3 b, Vector3[] corners)
        {
            float d0 = Vector3.Distance(a, corners[0]) + Vector3.Distance(corners[0], corners[1]) + Vector3.Distance(corners[1], b);
            float d1 = Vector3.Distance(a, corners[1]) + Vector3.Distance(corners[1], corners[0]) + Vector3.Distance(corners[0], b);
            return Mathf.Min(d0, d1);
        }

        static bool SegmentHitsBox(Vector2 p, Vector2 q, Bounds b)
        {
            // 2D 線段 vs 長方形（x / z）：取樣檢查就夠了
            for (int i = 1; i < 20; i++)
            {
                var m = Vector2.Lerp(p, q, i / 20f);
                if (m.x > b.min.x && m.x < b.max.x && m.y > b.min.z && m.y < b.max.z) return true;
            }
            return false;
        }

        Vector3 StartWorld(DSTActionClip c, out float yaw)
        {
            var p = anchor.TransformPoint(new Vector3(c.startPos.x, 0f, c.startPos.z));
            if (Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore) && System.Array.IndexOf(_ownColliders, hit.collider) < 0)
                p = hit.point;
            yaw = anchor.eulerAngles.y + c.startYaw;
            return p;
        }

        void TickApproach(State s)
        {
            if (!IsLocalDriver(s)) return;
            var pc = _driverPlayer.Controller;
            // 車子還在牽出來：先站著等，牽好之後重新規劃路線
            if (_bike && _bike.PullOutPending) { pc.Simulate(Vector2.zero, false, false, false, Time.deltaTime); _path.Clear(); return; }
            if (_path.Count == 0) { BeginApproach(s); return; }
            var start = StartWorld(_plan.clip, out float wantYaw);
            _path[_path.Count - 1] = start;           // 車子被推動的話起點跟著
            var me = pc.transform.position;
            var target = _path[0];
            var flat = target - me; flat.y = 0f;
            bool last = _path.Count == 1;
            float dt = Time.deltaTime;

            if (flat.magnitude > (last ? 0.07f : 0.2f))
            {
                float yaw = Quaternion.LookRotation(flat).eulerAngles.y;
                float turn = Mathf.DeltaAngle(pc.transform.eulerAngles.y, yaw);
                pc.transform.Rotate(0f, Mathf.Clamp(turn, -300f * dt, 300f * dt), 0f);
                // 先轉向再走，角度差很大時放慢
                float speed = _plan.approachSpeed * Mathf.Clamp01(1f - Mathf.Abs(turn) / 120f);
                if (last) speed = Mathf.Min(speed, flat.magnitude * 3f + 0.2f);
                pc.Simulate(new Vector2(0f, Mathf.Clamp01(speed / pc.walkSpeed)), false, false, false, dt);
            }
            else if (!last) _path.RemoveAt(0);
            else
            {
                // 到了：轉成片段開頭的朝向
                float turn = Mathf.DeltaAngle(pc.transform.eulerAngles.y, wantYaw);
                pc.transform.Rotate(0f, Mathf.Clamp(turn, -240f * dt, 240f * dt), 0f);
                pc.Simulate(Vector2.zero, false, false, false, dt);
                if (Mathf.Abs(turn) < 12f) { StartEnterRpc(); _approachUntil = float.MaxValue; }
            }
            if (Time.time > _approachUntil) { StartEnterRpc(); _approachUntil = float.MaxValue; }
        }

        // ------------------------------------------------------------------ 上車動作 → 騎乘
        void TickEnter(State s)
        {
            if (!_rig) { _driverPlayer = FindPlayer(s.driver); _rig = _driverPlayer ? _driverPlayer.GetComponent<DSTWolfRig>() : null; if (!_rig) return; }
            float e = (float)(Now - s.start);
            bool local = IsLocalDriver(s);

            if (!_clipStarted && !_riding)
            {
                // 中途加入：早就上車了 → 直接騎
                if (e > _plan.hesitate + _plan.Duration + 0.5f) { StartRide(s, local, 0.05f); return; }
                if (e < _plan.hesitate)
                {
                    if (!_hesitating && library.sniff && library.alert)
                    {
                        _rig.PlayInPlace(_plan.sniff ? library.sniff : library.alert, 0.25f);
                        _hesitating = true;
                    }
                    return;
                }
                _hesitating = false;
                var from = anchor.InverseTransformPoint(_driverPlayer.transform.position);
                float fromYaw = Mathf.DeltaAngle(anchor.eulerAngles.y, _driverPlayer.transform.eulerAngles.y);
                _rig.PlayAction(anchor, _plan.clip, _plan.speed, 0.25f, from, fromYaw, _plan.warp);
                _clipStarted = true;
                if (local) _driverPlayer.SetInVehicle(true);
            }
            if (_clipStarted && !_riding && e >= _plan.hesitate + _plan.Duration) StartRide(s, local, 0.3f);
            if (_riding) TickRide(s, local);
        }
        bool _hesitating;

        void StartRide(State s, bool local, float fade)
        {
            var loop = Ride(DSTActionKind.Ride);
            DSTActionClip l = null, r = null;
            foreach (var c in library.Of(kind, DSTActionKind.Steer)) { if (c.steerDeg > 0f) l = c; else r = c; }
            if (loop != null) _rig.PlayRide(anchor, loop, l, r, fade);
            _riding = true;
            _clipStarted = true;
            _fidgetRng = new System.Random(s.seed ^ 0x5bd1e995);
            double rideStart = s.start + _plan.hesitate + _plan.Duration;
            _nextFidget = rideStart + Mathf.Lerp(4f, 9f, (float)_fidgetRng.NextDouble());
            while (_nextFidget < Now - 1.0) _nextFidget += Mathf.Lerp(7f, 16f, (float)_fidgetRng.NextDouble());
            if (local)
            {
                _driverPlayer.SetInVehicle(true);
                ForceView(false);
                _driverPlayer.SetViewCamera(false);
                _entry.BeginDriving(this);
            }
        }

        DSTActionClip Ride(DSTActionKind k)
        {
            foreach (var c in library.Of(kind, k)) return c;
            return null;
        }

        void TickRide(State s, bool local)
        {
            // 方向盤 / 龍頭：只有駕駛自己那台知道輸入（別人看不到前腳跟著轉，沒關係）
            if (_car) _rig.SetRideSteer(local ? -Mathf.Clamp(_car.SteeringWheelAngle / 60f, -1f, 1f) : 0f);
            if (_bike)
            {
                _rig.SetRideSteer(local ? -_bike.SteerInput : 0f);
                _rig.SetRidePhase(_bike.CrankAngle / 360f);
            }
            if (Now >= _nextFidget)
            {
                var list = new List<DSTActionClip>(library.Of(kind, DSTActionKind.Fidget));
                if (list.Count > 0 && Mathf.Abs(SpeedKmh()) < 25f) _rig.PlayFidget(list[_fidgetRng.Next(list.Count)]);
                _nextFidget += Mathf.Lerp(7f, 16f, (float)_fidgetRng.NextDouble());
            }
            // 角色本體（看不到的那個）跟著車走：其他系統（動物生成、地圖）用的是角色位置
            if (local) _driverPlayer.FollowVehicle(anchor);
        }

        float SpeedKmh() => _entry.Drivable != null ? _entry.Drivable.SpeedKmh : 0f;

        // ------------------------------------------------------------------ 下車動作
        void TickExit(State s)
        {
            if (!_rig) { _driverPlayer = FindPlayer(s.driver); _rig = _driverPlayer ? _driverPlayer.GetComponent<DSTWolfRig>() : null; if (!_rig) return; }
            float e = (float)(Now - s.start);
            bool local = IsLocalDriver(s);
            if (!_clipStarted)
            {
                _rig.PlayAction(anchor, _plan.clip, _plan.speed, 0.2f, _plan.clip.startPos, _plan.clip.startYaw, 0f);
                _clipStarted = true;
            }
            if (_exitDone || e < _plan.Duration) return;

            var end = EndWorld(_plan.clip, out float yaw);
            if (local)
            {
                _driverPlayer.Controller.Teleport(end, yaw);
                _driverPlayer.EndScripted();
                _driverPlayer.SetInVehicle(false);
                _rig.ReturnToLocomotion(0.15f);
                ForceView(false);
                _entry.ReleaseLocal(this);
                ExitDoneRpc();
                _exitDone = true;
                return;
            }
            // 別人的角色：等角色位置（連線同步）真的到了終點才把模型交回去，不然會閃一下
            _exitWait += Time.deltaTime;
            var p = _driverPlayer.transform.position;
            if (Vector3.Distance(new Vector3(p.x, 0, p.z), new Vector3(end.x, 0, end.z)) < 0.35f || _exitWait > 1.2f)
            {
                _rig.ReturnToLocomotion(0.15f);
                _exitDone = true;
            }
        }

        Vector3 EndWorld(DSTActionClip c, out float yaw)
        {
            var p = anchor.TransformPoint(new Vector3(c.endPos.x, 0f, c.endPos.z));
            if (Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore) && System.Array.IndexOf(_ownColliders, hit.collider) < 0)
                p = hit.point + Vector3.up * 0.02f;
            yaw = anchor.eulerAngles.y + c.endYaw;
            return p;
        }

        // ------------------------------------------------------------------ 車門
        void UpdateDoor(State s)
        {
            if (!door) return;
            float angle = _doorAngle.Value;
            if ((s.phase == Phase.Enter || s.phase == Phase.Exit) && _clipStarted && !_riding && _plan.clip != null && _plan.clip.HasDoor)
            {
                float t = ((float)(Now - s.start) - (s.phase == Phase.Enter ? _plan.hesitate : 0f)) * _plan.speed;
                angle = _plan.clip.doorAngle.Evaluate(Mathf.Clamp(t, 0f, _plan.clip.Length));
            }
            else if (s.phase == Phase.Approach || (s.phase == Phase.Enter && !_clipStarted))
            {
                // 還沒開始開門：維持原本的角度（片段開頭的角度）
                if (_plan.clip != null && _plan.clip.HasDoor) angle = _plan.clip.doorAngle.Evaluate(0f);
            }
            door.localRotation = _doorRest * Quaternion.AngleAxis(doorSign * angle, doorAxis);
        }
    }
}
