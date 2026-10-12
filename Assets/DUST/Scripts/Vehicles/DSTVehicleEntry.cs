using DUST.Player;
using Unity.Netcode;
using UnityEngine;

namespace DUST.Vehicles
{
    // 上下車：誰在開車由主機決定（一次一個駕駛），上車後車的物理交給駕駛那台電腦跑（Ownership 轉移）。
    // 有 DSTRideActions（鬃狼的上下車動作）時，上下車交給它播動作；沒有的話維持舊做法（瞬間上車、下車點傳送）。
    public class DSTVehicleEntry : NetworkBehaviour
    {
        public MonoBehaviour vehicle;                  // 實作 IDSTDrivable 的元件（皮卡 / 無人機）；留空 → 自動找
        public DSTChaseCamera vehicleCamera;           // 留空 → 執行時在場景裡找
        public Transform[] exitPoints;                 // 依優先順序：駕駛側、乘客側、車尾
        public float maxExitSpeedKmh = 12f;
        public float chaseDistance;                    // 追車鏡頭距離 / 高度；0 = 鏡頭預設值（腳踏車要近一點）
        public float chaseHeight;

        const ulong NoDriver = ulong.MaxValue;
        readonly NetworkVariable<ulong> _driver = new NetworkVariable<ulong>(NoDriver);

        public bool HasDriver => _driver.Value != NoDriver;
        public bool CanEnter => !HasDriver && Time.time >= _requestPendingUntil && (!_actions || _actions.CurrentPhase == DSTRideActions.Phase.None);
        public bool IsLocalDriver => _localDriver != null;
        public IDSTDrivable Drivable { get; private set; }

        DSTPlayerController _localDriver;
        DSTRideActions _actions;
        float _requestPendingUntil;
        float _enterTime;
        float _blockedMessageUntil;
        string _blockedMessage;

        public static readonly System.Collections.Generic.List<DSTVehicleEntry> All = new System.Collections.Generic.List<DSTVehicleEntry>();

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        void Awake()
        {
            if (!vehicle) vehicle = GetComponent<IDSTDrivable>() as MonoBehaviour;
            Drivable = vehicle as IDSTDrivable;
            _actions = GetComponent<DSTRideActions>();
            if (_actions && !_actions.library) _actions = null;
            if (!vehicleCamera) vehicleCamera = FindAnyObjectByType<DSTChaseCamera>(FindObjectsInactive.Include);
        }

        public override void OnNetworkSpawn()
        {
            _driver.OnValueChanged += OnDriverChanged;
            if (IsServer)
            {
                NetworkManager.OnClientDisconnectCallback += OnClientLeft;
                // 場景裡的車關房後不會重建，上一場離開時還在車上的駕駛會留在變數裡 → 新開房要清掉
                _driver.Value = NoDriver;
            }
            _requestPendingUntil = 0f;
        }

        public override void OnNetworkDespawn()
        {
            _driver.OnValueChanged -= OnDriverChanged;
            if (IsServer && NetworkManager) NetworkManager.OnClientDisconnectCallback -= OnClientLeft;
            if (_localDriver != null) LocalExit();   // 斷線時還在車上
        }

        // ------------------------------------------------------------------ 上車
        public void Enter(DSTPlayerController player)
        {
            if (!CanEnter || player == null) return;
            _requestPendingUntil = Time.time + 1f;     // 等主機回覆，避免連按
            if (_actions) _actions.RequestEnter(player);
            else EnterRpc();
        }

        [Rpc(SendTo.Server)]
        void EnterRpc(RpcParams rpc = default)
        {
            ServerAssignDriver(rpc.Receive.SenderClientId);
        }

        /// <summary>主機：指定駕駛（物理交給他）。別人已經在開就失敗。</summary>
        public bool ServerAssignDriver(ulong id)
        {
            if (!IsServer || HasDriver) return false;
            _driver.Value = id;
            if (NetworkObject.OwnerClientId != id) NetworkObject.ChangeOwnership(id);
            return true;
        }

        void OnDriverChanged(ulong previous, ulong current)
        {
            _requestPendingUntil = 0f;
            if (current == NetworkManager.LocalClientId && _localDriver == null)
            {
                var me = DSTNetPlayer.Local;
                if (me == null) return;
                _localDriver = me.Controller;
                // 有動作系統：等動作播完才開始開車（DSTRideActions 會呼叫 BeginDriving）
                if (!_actions) LocalEnter();
            }
        }

        void LocalEnter()
        {
            var me = DSTNetPlayer.Local;
            if (me == null) return;
            me.SetInVehicle(true);
            me.SetViewCamera(false);
            BeginDriving(null);
        }

        /// <summary>開始操控：輸入、追車鏡頭</summary>
        internal void BeginDriving(DSTRideActions from)
        {
            if (_localDriver == null && DSTNetPlayer.Local) _localDriver = DSTNetPlayer.Local.Controller;
            Drivable.PlayerControlled = true;
            if (vehicleCamera)
            {
                // 鏡頭是所有載具共用的，每次上車都換成這台的目標與駕駛視角
                vehicleCamera.target = transform;
                vehicleCamera.cockpitEye = transform.Find("Seat_Driver_Eye");
                vehicleCamera.SetFraming(chaseDistance, chaseHeight);
                vehicleCamera.gameObject.SetActive(true);
            }
            _enterTime = Time.time;
        }

        /// <summary>停止操控（下車動作開始）：車子自己停好、追車鏡頭關掉</summary>
        internal void StopDriving(DSTRideActions from)
        {
            Drivable.PlayerControlled = false;
            if (vehicleCamera) vehicleCamera.gameObject.SetActive(false);
        }

        /// <summary>下車動作做完（本地）</summary>
        internal void ReleaseLocal(DSTRideActions from)
        {
            _localDriver = null;
        }

        // ------------------------------------------------------------------ 下車
        void Update()
        {
            if (_localDriver == null) return;
            if (_actions && _actions.Busy) return;
            if (Time.time - _enterTime > 0.25f && DSTPlayerInteractor.InteractPressed())
            {
                DSTPlayerInteractor.ConsumeInteract();
                TryExit();
            }
        }

        public bool TryExit()
        {
            if (_localDriver == null) return false;
            if (!Drivable.CanExit(maxExitSpeedKmh, out var reason))
            {
                Block(reason);
                return false;
            }
            if (_actions)
            {
                _actions.RequestExit();
                return true;
            }
            var cc = _localDriver.GetComponent<CharacterController>();
            foreach (var p in exitPoints)
            {
                if (!p) continue;
                if (!FindStandPoint(p.position, cc, out var stand)) continue;

                var player = _localDriver;
                LocalExit();
                // 面向車頭方向，方便看到車
                player.Teleport(stand, Quaternion.LookRotation(Vector3.ProjectOnPlane(transform.forward, Vector3.up)).eulerAngles.y);
                ExitRpc();
                return true;
            }
            Block("下車位置被擋住了");
            return false;
        }

        void LocalExit()
        {
            _localDriver = null;
            Drivable.PlayerControlled = false;
            if (vehicleCamera) vehicleCamera.gameObject.SetActive(false);
            if (DSTNetPlayer.Local && DSTNetPlayer.Local.IsSpawned)
            {
                DSTNetPlayer.Local.SetInVehicle(false);
                DSTNetPlayer.Local.SetViewCamera(true);
            }
        }

        [Rpc(SendTo.Server)]
        void ExitRpc(RpcParams rpc = default)
        {
            if (_driver.Value == rpc.Receive.SenderClientId) Release();
        }

        void OnClientLeft(ulong clientId)
        {
            if (_driver.Value == clientId) Release();
        }

        /// <summary>主機：車交還主機</summary>
        public void ServerRelease()
        {
            if (IsServer) Release();
        }

        // 車交還主機（DontDestroyWithOwner，駕駛斷線車也不會消失）
        void Release()
        {
            _driver.Value = NoDriver;
            if (NetworkObject.OwnerClientId != NetworkManager.ServerClientId) NetworkObject.RemoveOwnership();
            if (_actions) _actions.ServerClear();
        }

        // 從下車點往下找地面，確認角色膠囊放得下
        public bool FindStandPoint(Vector3 around, CharacterController cc, out Vector3 stand)
        {
            stand = around;
            float r = cc ? cc.radius : 0.35f, h = cc ? cc.height : 1.8f;
            Vector3 probe = around + Vector3.up * 1.5f;
            if (Physics.Raycast(probe, Vector3.down, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(transform))
                stand = hit.point + Vector3.up * 0.02f;
            else
                return false;
            Vector3 a = stand + Vector3.up * (r + 0.05f), b = stand + Vector3.up * (h - r);
            foreach (var c in Physics.OverlapCapsule(a, b, r, ~0, QueryTriggerInteraction.Ignore))
            {
                if (cc && (c.transform == cc.transform || c.transform.IsChildOf(cc.transform))) continue;
                return false;
            }
            return true;
        }

        void Block(string msg)
        {
            _blockedMessage = msg;
            _blockedMessageUntil = Time.time + 1.5f;
        }

        void OnGUI()
        {
            if (_localDriver == null) return;
            if (_actions && _actions.Busy) return;
            if (Time.time < _blockedMessageUntil) DSTHud.CenterPrompt(_blockedMessage);
            DSTHud.BottomHint(Drivable.ControlsHint);
        }
    }
}
