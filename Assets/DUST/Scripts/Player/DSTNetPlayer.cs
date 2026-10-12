using System.Collections.Generic;
using DUST.Core;
using DUST.Net;
using DUST.World;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace DUST.Player
{
    // 連線玩家：自己的角色才吃輸入、開攝影機；別人的角色只顯示身體，視角與蹲姿從網路同步。
    // 位置由 NetworkTransform（Owner 權威）同步，所以移動手感跟單機一樣。
    [RequireComponent(typeof(DSTPlayerController))]
    public class DSTNetPlayer : NetworkBehaviour
    {
        public Renderer[] body;              // 別人看得到的身體；自己只投影子
        public float spawnSpacing = 0.8f;    // 多人出生時左右錯開

        public static DSTNetPlayer Local { get; private set; }
        public static readonly List<DSTNetPlayer> All = new List<DSTNetPlayer>();

        public DSTPlayerController Controller => _pc;
        public bool InVehicle => _inVehicle.Value;
        public bool IsCrouching => IsOwner ? _pc.IsCrouching : _crouching.Value;
        public bool IsSprinting => IsOwner ? _pc.IsSprinting : _sprinting.Value;

        readonly NetworkVariable<float> _pitch = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<bool> _crouching = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<bool> _sprinting = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<bool> _inVehicle = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        DSTPlayerController _pc;
        DSTPlayerInteractor _interactor;
        CharacterController _cc;
        Camera _cam;
        AudioListener _listener;
        float _remoteHeight;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Local = null;
            All.Clear();
        }

        void Awake()
        {
            _pc = GetComponent<DSTPlayerController>();
            _interactor = GetComponent<DSTPlayerInteractor>();
            _cc = GetComponent<CharacterController>();
            if (_pc.head)
            {
                _cam = _pc.head.GetComponentInChildren<Camera>(true);
                _listener = _pc.head.GetComponentInChildren<AudioListener>(true);
            }
            _remoteHeight = _pc.standHeight;
        }

        // 領巾顏色：每個玩家不一樣，一眼認得出誰是誰
        static readonly Color[] ScarfColors =
        {
            new Color(0.62f, 0.06f, 0.05f), new Color(0.06f, 0.30f, 0.62f), new Color(0.10f, 0.45f, 0.18f), new Color(0.85f, 0.62f, 0.05f),
            new Color(0.45f, 0.12f, 0.55f), new Color(0.05f, 0.50f, 0.50f), new Color(0.90f, 0.40f, 0.65f), new Color(0.15f, 0.15f, 0.15f),
        };

        void TintScarf()
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (!r.name.Contains("Scarf")) continue;
                var mpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", ScarfColors[(int)(OwnerClientId % (ulong)ScarfColors.Length)]);
                r.SetPropertyBlock(mpb);
            }
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            TintScarf();
            if (IsOwner)
            {
                Local = this;
                DSTNetSession.SetLobbyCamera(false);
                _needsPlacement = true;
                TryPlace();
            }
            _inVehicle.OnValueChanged += OnInVehicleChanged;
            Apply(_inVehicle.Value);
        }

        public override void OnNetworkDespawn()
        {
            _inVehicle.OnValueChanged -= OnInVehicleChanged;
            All.Remove(this);
            if (Local == this)
            {
                Local = null;
                SetLocalControl(false);
                DSTNetSession.SetLobbyCamera(true);
            }
        }

        // 世界（地面）還沒準備好之前先不要放下去，不然會一路掉出地圖
        bool _needsPlacement;

        void TryPlace()
        {
            if (!_needsPlacement) return;
            if (!DSTWorld.IsReady)
            {
                _pc.enabled = false;
                return;
            }
            _needsPlacement = false;
            var (pos, yaw) = SpawnPose();
            _pc.Teleport(pos, yaw);
            _pc.enabled = !_inVehicle.Value;
        }

        (Vector3, float) SpawnPose()
        {
            var world = DSTWorld.Instance;
            if (world && world.IsRandom)
            {
                // 隨機世界：出生點在原點，面向車子
                int k = (int)(OwnerClientId % 8);
                float s = (k + 1) / 2 * (k % 2 == 0 ? -1f : 1f) * spawnSpacing;
                return (new Vector3(s, world.GroundHeight(s, 0f) + 0.05f, 0f), 0f);
            }
            var spawn = GameObject.Find("DR2_Spawn_T");
            Vector3 p = spawn ? spawn.transform.position : transform.position;
            float yaw = spawn ? spawn.transform.eulerAngles.y : 0f;
            Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            // 0, +1, -1, +2 … 錯開，不會卡在彼此身上
            int i = (int)(OwnerClientId % 8);
            float side = (i + 1) / 2 * (i % 2 == 0 ? -1f : 1f);
            return (p + right * side * spawnSpacing + Vector3.up * 0.05f, yaw);
        }

        public void SetInVehicle(bool inside)
        {
            if (!IsOwner) return;
            _inVehicle.Value = inside;
            Apply(inside);
        }

        void OnInVehicleChanged(bool _, bool inside) => Apply(inside);

        // 上車時碰撞收起來、不吃走路輸入（身體不藏：鬃狼模型會掛到車上播騎乘動作）
        void Apply(bool inVehicle)
        {
            if (_cc) _cc.enabled = !inVehicle;
            if (IsOwner)
            {
                UpdateLocalControl();
                // 不在車上時一定用自己的鏡頭；在車上時由上下車動作 / 追車鏡頭決定
                if (!inVehicle) SetViewCamera(true);
            }
        }

        bool _scripted;

        void UpdateLocalControl()
        {
            bool on = !_inVehicle.Value && !_scripted;
            _pc.enabled = on;
            if (_interactor) _interactor.enabled = on;
        }

        void SetLocalControl(bool on)
        {
            _pc.enabled = on;
            if (_interactor) _interactor.enabled = on;
            SetViewCamera(on);
        }

        /// <summary>自己角色的鏡頭（開車時改用追車鏡頭）</summary>
        public void SetViewCamera(bool on)
        {
            if (!IsOwner) return;
            if (_cam) _cam.enabled = on;
            if (_listener) _listener.enabled = on;
        }

        /// <summary>腳本控制角色（上車前自己走過去）：不吃玩家輸入</summary>
        public void BeginScripted() { _scripted = true; if (IsOwner) UpdateLocalControl(); }
        public void EndScripted() { _scripted = false; if (IsOwner) UpdateLocalControl(); }

        /// <summary>開車時，看不到的角色本體跟著車走（動物生成、地圖、連線距離都用角色位置）</summary>
        public void FollowVehicle(Transform anchor)
        {
            if (!IsOwner || _cc && _cc.enabled) return;
            transform.SetPositionAndRotation(anchor.position, Quaternion.Euler(0f, anchor.eulerAngles.y, 0f));
        }

        void Update()
        {
            if (!IsSpawned) return;
            if (IsOwner)
            {
                TryPlace();
                if (Mathf.Abs(_pitch.Value - _pc.Pitch) > 0.5f) _pitch.Value = _pc.Pitch;
                if (_crouching.Value != _pc.IsCrouching) _crouching.Value = _pc.IsCrouching;
                if (_sprinting.Value != _pc.IsSprinting) _sprinting.Value = _pc.IsSprinting;
                return;
            }

            // 別人的角色：頭的仰角、蹲下高度（碰撞用；外觀由鬃狼模型負責）
            if (_pc.head) _pc.head.localRotation = Quaternion.Euler(_pitch.Value, 0f, 0f);
            float target = _crouching.Value ? _pc.crouchHeight : _pc.standHeight;
            _remoteHeight = Mathf.MoveTowards(_remoteHeight, target, 6f * Time.deltaTime);
            if (_cc && _cc.enabled)
            {
                _cc.height = _remoteHeight;
                _cc.center = Vector3.up * (_remoteHeight * 0.5f);
            }
        }

        // 揮拳打到東西：交給主機判定，動物的血量只在主機上算
        public void Punch(IDSTDamageable target, float damage)
        {
            if (target is Component c && c.TryGetComponent<NetworkObject>(out var no) && no.IsSpawned)
                PunchRpc(no, damage);
            else
                target.TakeDamage(damage, transform);
        }

        [Rpc(SendTo.Server)]
        void PunchRpc(NetworkObjectReference target, float damage)
        {
            if (!target.TryGet(out var no)) return;
            // 簡單防呆：距離太遠的拳不算
            if (Vector3.Distance(no.transform.position, transform.position) > 5f) return;
            var d = no.GetComponent<IDSTDamageable>();
            if (d != null && d.IsAlive) d.TakeDamage(damage, transform);
        }
    }
}
