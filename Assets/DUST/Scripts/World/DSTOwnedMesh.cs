using UnityEngine;

namespace DUST.World
{
    // 執行期建的網格（街道、水池）不會跟著物件一起刪，掛這個在物件被刪時一起釋放
    public class DSTOwnedMesh : MonoBehaviour
    {
        public Mesh mesh;

        void OnDestroy()
        {
            if (mesh) Destroy(mesh);
        }

        // 執行期一直改網格的物件不要走 GPU Resident Drawer：它快取的實例資料不會跟著網格更新，
        // 換細節之後陰影 / 顯示會停在舊網格（整塊地莫名變暗、路面像凹下去）。有 MaterialPropertyBlock 的物件會改走一般繪製。
        public static void OptOutGpuDriven(Renderer r)
        {
            var block = new MaterialPropertyBlock();
            block.SetFloat("_DSTRuntimeMesh", 1f);
            r.SetPropertyBlock(block);
        }
    }
}
