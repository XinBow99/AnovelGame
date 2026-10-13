using UnityEngine;

namespace DUST.Animals
{
    // 動物的外觀貼地：NavMeshAgent 走在導航網格上，導航網格是體素烘出來的，跟真正的地面差幾公分（整隻浮起來或陷下去）。
    // 這裡在根節點下面多一層「GroundOffset」，模型都放在裡面，每幀往下打射線找真正的地面，把這一層平滑地移過去。
    // 只動外觀：導航、碰撞、連線同步的位置都不變（每台電腦各自算）。
    public class DSTGroundSnap : MonoBehaviour
    {
        public float maxOffset = 0.3f;
        public float smoothing = 12f;

        public Transform Pivot { get; private set; }

        readonly RaycastHit[] _hits = new RaycastHit[8];
        float _offset;
        bool _init;

        void Awake() => EnsurePivot();

        public Transform EnsurePivot()
        {
            if (Pivot) return Pivot;
            Pivot = new GameObject("GroundOffset").transform;
            // 原本的外觀（有 Renderer / Animator 的子物件）搬進來
            var kids = new System.Collections.Generic.List<Transform>();
            foreach (Transform c in transform)
                if (c.GetComponentInChildren<Renderer>(true) || c.GetComponentInChildren<Animator>(true)) kids.Add(c);
            Pivot.SetParent(transform, false);
            foreach (var c in kids) c.SetParent(Pivot, false);
            return Pivot;
        }

        void LateUpdate()
        {
            if (!Pivot) return;
            var origin = transform.position + Vector3.up * 0.6f;
            int n = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, 1.6f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue, ground = transform.position.y;
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.transform == transform || h.transform.IsChildOf(transform)) continue;
                if (h.collider.attachedRigidbody) continue;            // 箱子、車子不算地面
                if (h.distance < best) { best = h.distance; ground = h.point.y; }
            }
            float want = best < float.MaxValue ? Mathf.Clamp(ground - transform.position.y, -maxOffset, maxOffset) : 0f;
            _offset = _init ? Mathf.Lerp(_offset, want, 1f - Mathf.Exp(-smoothing * Time.deltaTime)) : want;
            _init = true;
            Pivot.localPosition = new Vector3(0f, _offset, 0f);
        }
    }
}
