using UnityEngine;

namespace DUST.World
{
    // 隨機世界用到的材質與擺設 Prefab（由 DUST/World/Setup Random World 自動建立）
    [CreateAssetMenu(menuName = "DUST/World Palette")]
    public class DSTWorldPalette : ScriptableObject
    {
        [Header("Terrain")]
        public Material sand;
        public Material rock;

        [Header("Scatter")]
        public GameObject[] rocks;           // Blender 做的砂岩石塊
        public GameObject[] smallRocks;
        public GameObject[] drifts;          // 沙堆
        public GameObject[] rubble;
        public GameObject[] pots;
        public GameObject wreck;
        public GameObject brokenCrate;

        [Header("Ruins (DR2 模組，4 m 一格)")]
        public GameObject[] wallStraight;
        public GameObject[] wallDamaged;
        public GameObject[] wallWindow;
        public GameObject wallGate;
        public GameObject wallCorner;
        public GameObject[] wallHalf;        // 2 m 寬的牆，用來做倒塌一半的牆

        [Header("Settlements")]
        public Material road;                // 聚落街道（夯實的土）
        public Material water;               // 綠洲水池
        public GameObject roofCap;
        public GameObject corniceStraight;
        public GameObject corniceCorner;
        public GameObject plinthStraight;
        public GameObject plinthCorner;
        public GameObject well;              // Blender：SM_DST_World_Settlement.blend
        public GameObject[] stalls;
        public GameObject[] palms;
        public GameObject[] barrels;
        public GameObject[] crates;          // 靜態的木箱（地圖上會碎的那些是另一套）
    }
}
