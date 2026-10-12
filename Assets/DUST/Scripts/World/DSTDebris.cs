using UnityEngine;

namespace DUST.World
{
    // 箱子碎片（Blender 預先切好的木板 / 框條）。Burst 時每塊加上剛體與初速：
    // 撞擊方向（離撞擊點越近越快）＋ 從中心往外炸開 ＋ 一點隨機旋轉；同一個 seed 每台電腦看起來差不多。
    // 純視覺、不連線同步；lifetime 秒後沉進地面消失。
    public class DSTDebris : MonoBehaviour
    {
        public float density = 550f;              // 木頭 kg/m³
        public float burstOutward = 1.6f;         // m/s，從中心往外
        public float impactShare = 0.8f;          // 撞擊 Δv 有幾成傳給碎片
        public float maxPieceSpeed = 14f;         // m/s；太快會穿過地面 / 牆
        public float lifetime = 10f;
        public float sinkSeconds = 1.5f;

        float _bornAt;
        Rigidbody[] _bodies;

        public void Burst(Vector3 baseVelocity, Vector3 dir, float deltaV, Vector3 point, int seed)
        {
            var rng = new System.Random(seed);
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);

            _bornAt = Time.time;
            var center = GetComponentInChildren<Renderer>() ? Bounds().center : transform.position;
            float size = Mathf.Max(0.1f, Bounds().extents.magnitude);
            var mfs = GetComponentsInChildren<MeshFilter>();
            _bodies = new Rigidbody[mfs.Length];
            for (int i = 0; i < mfs.Length; i++)
            {
                var piece = mfs[i].gameObject;
                // 碰撞體通常已在 Prefab 裡建好（建置版執行期不一定能讀網格）
                if (!piece.TryGetComponent<MeshCollider>(out var col))
                {
                    col = piece.AddComponent<MeshCollider>();
                    col.sharedMesh = mfs[i].sharedMesh;
                    col.convex = true;
                }
                var rb = piece.AddComponent<Rigidbody>();
                var s = Vector3.Scale(mfs[i].sharedMesh.bounds.size, piece.transform.lossyScale);
                rb.mass = Mathf.Max(0.05f, s.x * s.y * s.z * density);
                rb.linearDamping = 0.05f;
                rb.angularDamping = 0.2f;
                rb.maxDepenetrationVelocity = 1f;   // 碎片一開始貼在一起，不要被推開得像爆炸
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;   // 薄木板高速飛也不會穿地

                Vector3 p = piece.transform.position;
                float near = Mathf.Max(0.25f, 1f - Vector3.Distance(p, point) / (size * 2.2f));
                Vector3 radial = p - center;
                radial = radial.sqrMagnitude > 1e-4f ? radial.normalized : Vector3.up;
                rb.linearVelocity = Vector3.ClampMagnitude(baseVelocity
                                  + dir * (deltaV * impactShare * near)
                                  + radial * burstOutward * Mathf.Clamp01(deltaV / 6f)
                                  + new Vector3(R(-0.5f, 0.5f), R(0.4f, 1.6f), R(-0.5f, 0.5f)), maxPieceSpeed);
                rb.angularVelocity = new Vector3(R(-1f, 1f), R(-1f, 1f), R(-1f, 1f)) * 8f;
                _bodies[i] = rb;
            }
        }

        Bounds Bounds()
        {
            var rs = GetComponentsInChildren<Renderer>();
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        void Update()
        {
            if (_bodies == null) return;
            float age = Time.time - _bornAt;
            if (age < lifetime) return;
            // 沉進地面：關掉碰撞往下移，然後刪掉
            float t = (age - lifetime) / sinkSeconds;
            foreach (var rb in _bodies)
            {
                if (!rb) continue;
                if (!rb.isKinematic)
                {
                    rb.isKinematic = true;
                    var c = rb.GetComponent<Collider>();
                    if (c) c.enabled = false;
                }
                rb.transform.position += Vector3.down * (0.25f * Time.deltaTime / sinkSeconds);
            }
            if (t >= 1f) Destroy(gameObject);
        }
    }
}
