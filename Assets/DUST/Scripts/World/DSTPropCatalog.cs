using System.Collections.Generic;
using UnityEngine;

namespace DUST.World
{
    // 隨機世界每一種擺設「被碰到會怎樣」：輕的（陶罐、木桶、木箱）一碰就倒、撞猛了會碎；
    // 重的（牆、大石頭、井、攤子）不會被推動，但車子撞得夠快會整塊碎掉；棕櫚樹會被撞倒。
    // 碎片是 Blender 預先切好的（Tools/Blender/fracture.py），由 DUST/World/Setup Interactive Props 建立。
    [CreateAssetMenu(menuName = "DUST/Prop Catalog")]
    public class DSTPropCatalog : ScriptableObject
    {
        public enum Mode { Loose, Sturdy, Topple }

        [System.Serializable]
        public class Entry
        {
            public string name;                 // 來源 Prefab（FBX）的名字；生成的物件叫「名字(Clone)」
            public Mode mode;
            public float mass = 20f;            // 原始大小時的質量（kg），縮放後乘上體積比例
            public float refVolume = 1f;        // 原始大小時包圍盒的體積（m³）
            public Vector3 sourceScale = Vector3.one;   // 來源 Prefab 根物件的縮放（碎片照「擺設縮放 / 這個」縮）
            public float breakSpeed = 6f;       // 輕的：自己的 Δv；重的：撞上來的東西的相對速度（m/s）
            public float minHitterMass;         // 重的：太輕的東西（人）撞不壞
            public GameObject debris;           // 預先切好的碎片（沒有就不會碎）
            public float debrisDensity = 2000f;
            public bool capsule;                // 輕的物件動起來時用膠囊碰撞體（罐子、桶子）
        }

        public List<Entry> entries = new List<Entry>();

        Dictionary<string, Entry> _map;

        public Entry Find(string cloneName)
        {
            if (_map == null)
            {
                _map = new Dictionary<string, Entry>();
                foreach (var e in entries) if (e != null && !string.IsNullOrEmpty(e.name)) _map[e.name] = e;
            }
            int k = cloneName.IndexOf("(Clone)", System.StringComparison.Ordinal);
            string key = k >= 0 ? cloneName.Substring(0, k).TrimEnd() : cloneName;
            return _map.TryGetValue(key, out var hit) ? hit : null;
        }

        void OnValidate() => _map = null;
    }
}
