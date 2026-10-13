using DUST.World;
using Unity.Netcode;
using UnityEngine;

namespace DUST.Vehicles
{
    // 放在載具上：這台電腦在跑這台載具的物理時（自己在開），把前方 / 附近的物理物件 Ownership 先要過來，
    // 撞上去的時候才是真的剛體互撞（不然撞到的是不會動的同步複本，像撞牆）。
    // 主機自己開車時物件本來就是主機的，不用做事。
    // 隨機世界的擺設（DSTWorldProp）也在這裡處理：輕的先叫醒變成剛體，重的撞得夠快就碎、車子穿過去只減速。
    [RequireComponent(typeof(Rigidbody))]
    public class DSTPhysicsClaimer : MonoBehaviour
    {
        public float baseRadius = 1.5f;
        public float lookAheadSeconds = 0.4f;     // 速度越快，看得越遠（涵蓋要 Ownership 的來回時間）
        public float interval = 0.1f;
        [Range(0f, 1f)] public float keepSpeedOnBreak = 0.6f;   // 撞碎牆 / 石頭後保留多少速度

        Rigidbody _rb;
        NetworkObject _no;
        float _next;
        readonly Collider[] _hits = new Collider[32];
        Vector3 _lastVel, _lastAng;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _no = GetComponent<NetworkObject>();
        }

        // 這台電腦在跑這台載具的物理
        bool Driving => !_rb.isKinematic && (!_no || !_no.IsSpawned || _no.IsOwner);

        void FixedUpdate()
        {
            if (!Driving) return;
            // 碰撞發生時速度已經被吃掉了；記下撞之前的速度
            _lastVel = _rb.linearVelocity;
            _lastAng = _rb.angularVelocity;

            if (Time.time < _next) return;
            _next = Time.time + interval;

            Vector3 v = _rb.linearVelocity;
            float radius = baseRadius + v.magnitude * lookAheadSeconds * 0.5f;
            Vector3 center = _rb.worldCenterOfMass + v * lookAheadSeconds * 0.5f;
            int n = Physics.OverlapSphereNonAlloc(center, radius, _hits, ~0, QueryTriggerInteraction.Ignore);
            bool server = _no && _no.IsSpawned && _no.NetworkManager.IsServer;
            for (int i = 0; i < n; i++)
            {
                var wp = _hits[i].GetComponentInParent<DSTWorldProp>();
                if (wp) { wp.Wake(); continue; }       // 輕的：先變成剛體，撞上去才推得動
                if (server || !_no || !_no.IsSpawned) continue;
                var prop = _hits[i].GetComponentInParent<DSTPhysicsProp>();
                if (prop) prop.RequestOwnership();
            }
        }

        void OnCollisionEnter(Collision c)
        {
            if (!Driving) return;
            var wp = c.collider.GetComponentInParent<DSTWorldProp>();
            if (!wp || wp.CanMove) return;
            // 重的：這一下撞得夠猛就碎，車子穿過去（把被牆吃掉的速度還一部分回來）
            var contact = c.GetContact(0);
            float speed = Mathf.Max(Vector3.Dot(_lastVel, -contact.normal), c.relativeVelocity.magnitude * 0.8f);
            if (wp.Impact(_lastVel, speed, _rb.mass, contact.point))
            {
                _rb.linearVelocity = _lastVel * keepSpeedOnBreak;
                _rb.angularVelocity = _lastAng * keepSpeedOnBreak;
            }
        }
    }
}
