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
    // 血量由主機算（咬人的動物 AI 也在主機）；死掉後倒地，幾秒後回出生點重生。
    [RequireComponent(typeof(DSTPlayerController))]
    public class DSTNetPlayer : NetworkBehaviour, IDSTDamageable
    {
        // ------------------------------------------------------------------ 血量
        public const float MaxHealth = 20f;          // 【建議】大型肉食動物一口約 5，四、五口會死
        public const float BodyMass = 23f;           // 鬃狼平均體重（Wikipedia：20–30 kg，平均約 23 kg）；肉食動物用它判斷吃不吃得下
        const float RegenDelay = 8f;                 // 多久沒受傷才開始回血【建議】
        const float RegenPerSecond = 0.5f;           // 【建議】空血到滿約 40 秒
        const float RespawnDelay = 5f;

        readonly NetworkVariable<float> _health = new NetworkVariable<float>(MaxHealth);
        readonly NetworkVariable<bool> _down = new NetworkVariable<bool>(false);
        readonly NetworkVariable<bool> _creative = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public float Health => _health.Value;
        public bool IsAlive => !_down.Value;
        public bool IsCreative => _creative.Value;
        float _lastHurt = -999f;
        float _respawnAt, _hurtFlash;
        string _deathCause;

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
        // 只有房主自己的角色用到：房主有沒有開放作弊指令給其他人（/cheats on）
        readonly NetworkVariable<bool> _cheatsForAll = new NetworkVariable<bool>(false);

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

        public static Color ColorOf(ulong clientId) => ScarfColors[(int)(clientId % (ulong)ScarfColors.Length)];
        public static string NameOf(ulong clientId) => $"玩家 {clientId + 1}";
        public string DisplayName => NameOf(OwnerClientId);

        void TintScarf()
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (!r.name.Contains("Scarf")) continue;
                var mpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", ColorOf(OwnerClientId));
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
                HelloRpc(DSTSave.LocalPlayerId);   // 告訴主機我是誰（存檔裡找得到就放回上次的位置）
            }
            _inVehicle.OnValueChanged += OnInVehicleChanged;
            _down.OnValueChanged += OnDownChanged;
            Apply(_inVehicle.Value);
            OnDownChanged(false, _down.Value);
        }

        public override void OnNetworkDespawn()
        {
            _inVehicle.OnValueChanged -= OnInVehicleChanged;
            _down.OnValueChanged -= OnDownChanged;
            if (IsServer) DSTSave.StorePlayer(this);   // 離線（或關房）時記下位置
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
            var (pos, yaw) = _savedPose ?? SpawnPose();
            _savedPose = null;
            _pc.Teleport(pos, yaw);
            _pc.enabled = !_inVehicle.Value;
        }

        // ------------------------------------------------------------------ 存檔裡的玩家
        /// <summary>主機：這位玩家在存檔裡的身分（players/&lt;id&gt;.json）</summary>
        public string SaveId { get; private set; }
        (Vector3, float)? _savedPose;

        [Rpc(SendTo.Server)]
        void HelloRpc(string id, RpcParams rpc = default)
        {
            if (rpc.Receive.SenderClientId != OwnerClientId || string.IsNullOrEmpty(id)) return;
            // 同一台電腦開兩個視窗測試時身分會一樣：第二個加上編號
            string unique = id;
            for (int n = 2; All.Exists(p => p && p != this && p.SaveId == unique); n++) unique = id + "_" + n;
            SaveId = unique;
            var rec = DSTSave.LoadPlayer(unique);
            if (rec == null) return;
            if (rec.health > 0f) _health.Value = Mathf.Min(rec.health, MaxHealth);
            PlaceRpc(new Vector3(rec.x, rec.y + 0.1f, rec.z), rec.yaw);
        }

        [Rpc(SendTo.Owner)]
        void PlaceRpc(Vector3 pos, float yaw)
        {
            if (_needsPlacement) { _savedPose = (pos, yaw); return; }
            if (!_inVehicle.Value && IsAlive) _pc.Teleport(pos, yaw);
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
            bool on = !_inVehicle.Value && !_scripted && !_down.Value;
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
            if (IsServer) Regenerate();
            if (IsOwner)
            {
                TryPlace();
                if (Mathf.Abs(_pitch.Value - _pc.Pitch) > 0.5f) _pitch.Value = _pc.Pitch;
                if (_crouching.Value != _pc.IsCrouching) _crouching.Value = _pc.IsCrouching;
                if (_sprinting.Value != _pc.IsSprinting) _sprinting.Value = _pc.IsSprinting;
                if (_creative.Value != _pc.Creative) _creative.Value = _pc.Creative;
                if (_down.Value && _respawnAt > 0f && Time.time >= _respawnAt) Respawn();
                return;
            }

            // 別人的角色：頭的仰角、蹲下高度（碰撞用；外觀由鬃狼模型負責）
            if (_pc.head) _pc.head.localRotation = Quaternion.Euler(_pitch.Value, 0f, 0f);
            float target = _crouching.Value ? _pc.crouchHeight : _pc.standHeight;
            _remoteHeight = Mathf.MoveTowards(_remoteHeight, target, 6f * Time.deltaTime);
            if (_cc && _cc.enabled)
            {
                _cc.height = _remoteHeight;
                _cc.center = Vector3.up * (_remoteHeight * 0.5f + _cc.skinWidth);
            }
        }

        // ------------------------------------------------------------------ 聊天 / 作弊指令
        public const int MaxChatLength = 120;

        /// <summary>自己這台電腦可以用作弊指令嗎：房主（單人）一律可以；其他人要房主 /cheats on</summary>
        public static bool CheatsAllowed
        {
            get
            {
                var nm = NetworkManager.Singleton;
                if (!nm || nm.IsServer) return true;
                foreach (var p in All)
                    if (p && p.OwnerClientId == NetworkManager.ServerClientId) return p._cheatsForAll.Value;
                return false;
            }
        }

        /// <summary>房主：開放 / 關閉其他玩家的作弊指令</summary>
        public static bool SetCheatsForAll(bool on)
        {
            var nm = NetworkManager.Singleton;
            if (!nm || !nm.IsServer || Local == null) return false;
            Local._cheatsForAll.Value = on;
            return true;
        }

        public void SendChat(string text)
        {
            if (!IsOwner || string.IsNullOrWhiteSpace(text)) return;
            ChatRpc(text.Length > MaxChatLength ? text.Substring(0, MaxChatLength) : text);
        }

        [Rpc(SendTo.Server)]
        void ChatRpc(string text, RpcParams rpc = default)
        {
            if (rpc.Receive.SenderClientId != OwnerClientId || string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > MaxChatLength) text = text.Substring(0, MaxChatLength);
            ChatBroadcastRpc(text);
        }

        [Rpc(SendTo.Everyone)]
        void ChatBroadcastRpc(string text) => DUST.UI.DSTChat.Receive(DisplayName, ColorOf(OwnerClientId), text);

        /// <summary>/time：時間是主機在算，交給主機改</summary>
        public void RequestSetHour(float hour)
        {
            if (IsOwner) SetHourRpc(hour);
        }

        [Rpc(SendTo.Server)]
        void SetHourRpc(float hour, RpcParams rpc = default)
        {
            ulong sender = rpc.Receive.SenderClientId;
            if (sender != NetworkManager.ServerClientId && !HostAllowsCheats()) return;
            if (DSTTimeOfDay.Instance) DSTTimeOfDay.Instance.SetHour(hour);
            NoticeRpc($"{NameOf(sender)} 把時間調到 {Mathf.FloorToInt(hour):00}:{Mathf.FloorToInt((hour % 1f) * 60f):00}");
        }

        /// <summary>/wait：時間往前跳（發條沙漠：等到某一天、某個時刻）</summary>
        public void RequestAdvance(float hours)
        {
            if (IsOwner) AdvanceRpc(hours);
        }

        [Rpc(SendTo.Server)]
        void AdvanceRpc(float hours, RpcParams rpc = default)
        {
            ulong sender = rpc.Receive.SenderClientId;
            if (sender != NetworkManager.ServerClientId && !HostAllowsCheats()) return;
            hours = Mathf.Clamp(hours, 0f, 24f * 30f);
            if (DSTTimeOfDay.Instance) DSTTimeOfDay.Instance.Advance(hours);
            NoticeRpc($"{NameOf(sender)} 讓時間往前走了 {hours:0.#} 小時");
        }

        bool HostAllowsCheats() => Local != null && Local._cheatsForAll.Value;

        /// <summary>/summon：在自己前面生出這個地區名冊裡的第 slot 種（生成在主機上做）</summary>
        public void RequestSummon(int slot)
        {
            if (IsOwner) SummonRpc(slot);
        }

        [Rpc(SendTo.Server)]
        void SummonRpc(int slot, RpcParams rpc = default)
        {
            ulong sender = rpc.Receive.SenderClientId;
            if (sender != NetworkManager.ServerClientId && !HostAllowsCheats()) return;
            var spawner = DUST.Animals.DSTProcSpawner.Instance;
            if (!spawner) return;
            var roster = DUST.Animals.DSTEcology.At(transform.position);
            if (slot < 0 || slot >= roster.keys.Length) return;
            var at = transform.position + transform.forward * 4f;
            if (UnityEngine.AI.NavMesh.SamplePosition(at, out var hit, 4f, UnityEngine.AI.NavMesh.AllAreas)) at = hit.position;
            var g = DUST.Animals.DSTGenome.Get(roster.keys[slot]);
            spawner.SpawnGroup(g.key, at, g.group.y);
        }

        [Rpc(SendTo.Everyone)]
        void NoticeRpc(string text) => DUST.UI.DSTChat.System(text);

        // ------------------------------------------------------------------ 受傷 / 死亡 / 重生
        /// <summary>只在主機上有效（動物咬人在主機上判定）。在車上、創造模式、已經倒下都不會受傷。</summary>
        public void TakeDamage(float amount, Transform source)
        {
            if (!IsServer || !IsAlive || InVehicle || IsCreative || amount <= 0f) return;
            _lastHurt = Time.time;
            _health.Value = Mathf.Max(0f, _health.Value - amount);
            Vector3 from = source ? source.position : transform.position;
            HurtRpc(from);
            if (_health.Value > 0f) return;

            _down.Value = true;
            string killer = "不明的東西";
            if (source)
            {
                var a = source.GetComponentInParent<DUST.Animals.DSTAnimal>();
                if (a) killer = a.Genome != null ? a.Genome.name : a.species ? a.species.displayName : a.name;
            }
            NoticeRpc($"{DisplayName} 被{killer}咬死了");
            DSTSave.Log("player_death", DisplayName, killer, transform.position);
            DiedRpc(killer);
        }

        void Regenerate()
        {
            if (!IsAlive || _health.Value >= MaxHealth || Time.time - _lastHurt < RegenDelay) return;
            _health.Value = Mathf.Min(MaxHealth, _health.Value + RegenPerSecond * Time.deltaTime);
        }

        [Rpc(SendTo.Owner)]
        void HurtRpc(Vector3 from) => _hurtFlash = 1f;

        [Rpc(SendTo.Owner)]
        void DiedRpc(string killer)
        {
            _deathCause = killer;
            _respawnAt = Time.time + RespawnDelay;
        }

        // 自己的電腦：回出生點（位置是自己權威），再請主機補滿血
        void Respawn()
        {
            _respawnAt = 0f;
            var (pos, yaw) = SpawnPose();
            _pc.Teleport(pos, yaw);
            RespawnRpc();
        }

        [Rpc(SendTo.Server)]
        void RespawnRpc(RpcParams rpc = default)
        {
            if (rpc.Receive.SenderClientId != OwnerClientId || IsAlive) return;
            _health.Value = MaxHealth;
            _lastHurt = -999f;
            _down.Value = false;
        }

        // 倒下：所有人看到身體側倒；自己不能動
        void OnDownChanged(bool _, bool down)
        {
            var rig = GetComponent<DSTWolfRig>();
            if (rig) rig.SetDown(down);
            if (IsOwner) UpdateLocalControl();
        }

        // ------------------------------------------------------------------ 血量畫面（只畫自己的）
        GUIStyle _bigText;

        void OnGUI()
        {
            if (!IsOwner || !IsSpawned) return;
            if (Event.current.type != EventType.Repaint) return;
            float w = Screen.width, h = Screen.height;

            // 受傷：畫面邊緣閃紅
            if (_hurtFlash > 0f)
            {
                _hurtFlash = Mathf.Max(0f, _hurtFlash - Time.deltaTime * 1.5f);
                GUI.color = new Color(0.7f, 0f, 0f, 0.35f * _hurtFlash);
                float e = Mathf.Min(w, h) * 0.08f;
                GUI.DrawTexture(new Rect(0, 0, w, e), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(0, h - e, w, e), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(0, 0, e, h), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(w - e, 0, e, h), Texture2D.whiteTexture);
            }

            // 血條（左下角；滿血就藏起來）
            if ((_health.Value < MaxHealth - 0.01f || _down.Value) && !_inVehicle.Value)
            {
                var r = new Rect(24f, h - 48f, 220f, 14f);
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(new Rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f), Texture2D.whiteTexture);
                float f = _health.Value / MaxHealth;
                GUI.color = Color.Lerp(new Color(0.75f, 0.1f, 0.08f), new Color(0.85f, 0.75f, 0.35f), f);
                GUI.DrawTexture(new Rect(r.x, r.y, r.width * f, r.height), Texture2D.whiteTexture);
            }

            // 死亡畫面
            if (_down.Value)
            {
                GUI.color = new Color(0.25f, 0f, 0f, 0.45f);
                GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
                _bigText ??= new GUIStyle(GUI.skin.label) { fontSize = 34, alignment = TextAnchor.MiddleCenter, richText = true };
                GUI.color = Color.white;
                float left = Mathf.Max(0f, _respawnAt - Time.time);
                GUI.Label(new Rect(0, h * 0.4f, w, 60f), "你死了", _bigText);
                GUI.Label(new Rect(0, h * 0.4f + 50f, w, 40f), $"<size=20>被{_deathCause ?? "不明的東西"}咬死　{Mathf.CeilToInt(left)} 秒後在出生點重生</size>", _bigText);
            }
            GUI.color = Color.white;
        }

        // 揮拳打到東西：交給主機判定，動物的血量只在主機上算（不能打其他玩家）
        public void Punch(IDSTDamageable target, float damage)
        {
            if (target is DSTNetPlayer) return;
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
            if (no.GetComponent<DSTNetPlayer>() || !IsAlive) return;
            var d = no.GetComponent<IDSTDamageable>();
            if (d != null && d.IsAlive) d.TakeDamage(damage, transform);
        }
    }
}
