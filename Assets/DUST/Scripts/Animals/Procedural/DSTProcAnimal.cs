using DUST.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace DUST.Animals
{
    // 程式生成物種的個體。連線只同步兩個東西：物種 key（外型、習性全部由它算出來）和目前姿勢（吃草 / 警戒 / 逃跑 / 死掉）。
    // 每台電腦自己用 key 組身體、自己算腳步動作。AI 照舊用 DSTAnimal（只在主機跑）。
    [RequireComponent(typeof(DSTAnimal))]
    public class DSTProcAnimal : NetworkBehaviour
    {
        public Material template;      // HDRP/Lit 範本材質（毛色貼圖執行時產生）

        readonly NetworkVariable<int> _key = new NetworkVariable<int>();
        readonly NetworkVariable<byte> _pose = new NetworkVariable<byte>();

        public DSTGenome Genome { get; private set; }
        public int Key => _key.Value;

        DSTAnimal _animal;
        DSTCreatureMotor _motor;
        int _pendingKey;
        bool _built;
        float _nextLook;

        void Awake() => _animal = GetComponent<DSTAnimal>();

        /// <summary>主機：Spawn 之前指定物種</summary>
        public void SetKey(int key)
        {
            _pendingKey = key;
            // 不連線（測試）時直接組
            if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsListening) Build(key);
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer) _key.Value = _pendingKey;
            Build(_key.Value);
        }

        void Build(int key)
        {
            if (_built) return;
            _built = true;
            Genome = DSTGenome.Get(key);
            var rig = DSTCreatureBuilder.Build(Genome, transform, template);
            // 同一物種的個體大小有一點差異（由連線 ID 決定，每台電腦一樣）
            float s = 1f + ((IsSpawned ? (int)(NetworkObjectId % 17) : 8) - 8) * 0.012f;
            rig.root.localScale = Vector3.one * s;
            _motor = gameObject.AddComponent<DSTCreatureMotor>();
            _motor.Init(Genome, rig);

            // 碰撞（揮拳打得到）與導航大小跟著體型
            var col = GetComponent<CapsuleCollider>();
            if (col)
            {
                col.direction = 2;
                col.center = new Vector3(0f, Genome.hipHeight * s, Genome.bodyLength * 0.1f * s);
                col.radius = Genome.bodyRadius * 1.1f * s;
                col.height = (Genome.bodyLength + Genome.headLength + Genome.neckLength * 0.5f) * s;
            }
            var agent = GetComponent<NavMeshAgent>();
            if (agent)
            {
                agent.radius = Mathf.Max(0.08f, Genome.bodyRadius * 1.2f * s);
                agent.height = (Genome.hipHeight + Genome.bodyRadius) * s;
            }
            _animal.species = Genome.Species;
        }

        void Update()
        {
            if (!_built || _motor == null) return;
            if (IsServer || !IsSpawned)
            {
                var p = _animal.IsAlive ? _animal.CurrentState switch
                {
                    DSTAnimal.State.Sniff => CreaturePose.Graze,
                    DSTAnimal.State.Alert => CreaturePose.Alert,
                    DSTAnimal.State.Flee => CreaturePose.Flee,
                    _ => CreaturePose.Normal,
                } : CreaturePose.Dead;
                if (IsSpawned && _pose.Value != (byte)p) _pose.Value = (byte)p;
                _motor.pose = p;
            }
            else _motor.pose = (CreaturePose)_pose.Value;

            // 警戒時看最近的玩家（每台電腦自己找）
            if (Time.time > _nextLook)
            {
                _nextLook = Time.time + 0.5f;
                Transform best = null; float bd = float.MaxValue;
                foreach (var pl in DSTNetPlayer.All)
                {
                    if (!pl) continue;
                    float d = (pl.transform.position - transform.position).sqrMagnitude;
                    if (d < bd) { bd = d; best = pl.transform; }
                }
                _motor.lookTarget = best;
            }
        }
    }
}
