using DUST.Player;
using DUST.Vehicles;
using UnityEditor;
using UnityEngine;

namespace DUST.EditorTools
{
    // 給沒有專屬上下車動作的載具（米格-25、Roadster）加 DSTSeatRider：駕駛的鬃狼坐進座位播皮卡的開車循環。
    // 錨點 = 這台的駕駛眼睛 − 皮卡的駕駛眼睛（兩台都是車頭 +Z 的車身座標）+ 可調偏移。
    public static class DSTSeatRiderSetup
    {
        public static DSTSeatRider Add(GameObject root, Transform eye, Vector3 offset)
        {
            var lib = AssetDatabase.LoadAssetAtPath<DSTActionLibrary>(DSTPlayerWolfSetup.LibraryPath);
            if (!lib) { Debug.LogWarning("[DUST] 找不到鬃狼動作庫（先跑 DUST/Player/Build Maned Wolf Player），駕駛不會坐進去"); return null; }
            Vector3 pickupEye = new Vector3(0f, 0.84f, 0f);
            var pickup = AssetDatabase.LoadAssetAtPath<GameObject>(DSTVehicleSetup.PrefabPath);
            if (pickup)
                foreach (var t in pickup.GetComponentsInChildren<Transform>(true))
                    if (t.name == "Seat_Driver_Eye") { pickupEye = pickup.transform.InverseTransformPoint(t.position); break; }
            var anchor = new GameObject("Seat_Rider_Anchor").transform;
            anchor.SetParent(root.transform, false);
            anchor.localPosition = root.transform.InverseTransformPoint(eye.position) - pickupEye + offset;
            var rider = root.AddComponent<DSTSeatRider>();
            rider.library = lib;
            rider.anchor = anchor;
            return rider;
        }

        /// <summary>這台有自己的騎乘片段（直接在這台車的座標裡做的，例如 Roadster 的臥姿）：錨點 = 車的根，不用對齊皮卡</summary>
        public static DSTSeatRider AddNative(GameObject root, string loop, string steerLeft, string steerRight)
        {
            var lib = AssetDatabase.LoadAssetAtPath<DSTActionLibrary>(DSTPlayerWolfSetup.LibraryPath);
            if (!lib) { Debug.LogWarning("[DUST] 找不到鬃狼動作庫（先跑 DUST/Player/Build Maned Wolf Player），駕駛不會坐進去"); return null; }
            if (lib.Find(loop) == null) Debug.LogWarning($"[DUST] 動作庫裡沒有 {loop}（重跑 DUST/Player/Build Maned Wolf Player）");
            var anchor = new GameObject("Seat_Rider_Anchor").transform;
            anchor.SetParent(root.transform, false);
            var rider = root.AddComponent<DSTSeatRider>();
            rider.library = lib;
            rider.anchor = anchor;
            rider.loopClip = loop;
            rider.steerLeftClip = steerLeft;
            rider.steerRightClip = steerRight;
            return rider;
        }
    }
}
