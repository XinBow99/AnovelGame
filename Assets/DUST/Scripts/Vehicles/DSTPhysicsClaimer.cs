using DUST.World;
using Unity.Netcode;
using UnityEngine;

namespace DUST.Vehicles
{
    // 放在載具上：這台電腦在跑這台載具的物理時（自己在開），把前方 / 附近的物理物件 Ownership 先要過來，
    // 撞上去的時候才是真的剛體互撞（不然撞到的是不會動的同步複本，像撞牆）。
    // 主機自己開車時物件本來就是主機的，不用做事。
    [RequireComponent(typeof(Rigidbody))]
    public class DSTPhysicsClaimer : MonoBehaviour
    {
        public float baseRadius = 1.5f;
        public float lookAheadSeconds = 0.4f;     // 速度越快，看得越遠（涵蓋要 Ownership 的來回時間）
        public float interval = 0.1f;

        Rigidbody _rb;
        NetworkObject _no;
        float _next;
        readonly Collider[] _hits = new Collider[32];

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _no = GetComponent<NetworkObject>();
        }

        void FixedUpdate()
        {
            if (Time.time < _next) return;
            _next = Time.time + interval;
            if (!_no || !_no.IsSpawned || !_no.IsOwner || _no.NetworkManager.IsServer) return;
            if (_rb.isKinematic) return;

            Vector3 v = _rb.linearVelocity;
            float radius = baseRadius + v.magnitude * lookAheadSeconds * 0.5f;
            Vector3 center = _rb.worldCenterOfMass + v * lookAheadSeconds * 0.5f;
            int n = Physics.OverlapSphereNonAlloc(center, radius, _hits, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var prop = _hits[i].GetComponentInParent<DSTPhysicsProp>();
                if (prop) prop.RequestOwnership();
            }
        }
    }
}
