using System.Collections.Generic;
using UnityEngine;

namespace DUST.Player
{
    // 鬃狼的「動作系統」資料：每個片段在載具座標裡的起點 / 終點、車門曲線、條件與權重。
    // 由 DUST/Player/Build Maned Wolf Player 從 Blender 匯出的 player_actions.json + FBX 取樣建出來，不要手改。
    public enum DSTVehicleKind { Pickup, MeBike, Drone, Roadster }   // Roadster 只有騎乘片段（DSTSeatRider），沒有上下車動作
    public enum DSTActionKind { Enter, Exit, Ride, Steer, Fidget }
    public enum DSTActionSide { Left, Right, Rear, Any }
    public enum DSTDoorNeed { Any, Closed, Open }

    [System.Serializable]
    public class DSTActionClip
    {
        public string name;
        public AnimationClip clip;
        public DSTVehicleKind vehicle;
        public DSTActionKind kind;
        public DSTActionSide side = DSTActionSide.Any;
        public DSTDoorNeed door = DSTDoorNeed.Any;
        public bool leavesDoorOpen;
        public float weight = 1f;
        public float clearance = 0.8f;          // 起點 / 終點附近要空出來的半徑（公尺）
        public float steerDeg;                  // Steer 類：這個姿勢對應的方向盤 / 龍頭角度
        public string description;

        // 載具（anchor）座標，Unity 軸向：+Z = 車頭、+X = 車子右側
        public Vector3 startPos;
        public float startYaw;
        public Vector3 endPos;
        public float endYaw;
        public AnimationCurve doorAngle;        // 左前門開啟角度（度）；沒有門的載具為 null
        public List<DSTActionEvent> events = new List<DSTActionEvent>();

        public float Length => clip ? clip.length : 0f;
        public bool HasDoor => doorAngle != null && doorAngle.length > 0;
    }

    [System.Serializable]
    public struct DSTActionEvent
    {
        public string name;
        public float time;
    }

    public class DSTActionLibrary : ScriptableObject
    {
        public List<DSTActionClip> clips = new List<DSTActionClip>();
        public AnimationClip idle, walk, run, sniff, alert;

        public DSTActionClip Find(string name) => clips.Find(c => c.name == name);

        public IEnumerable<DSTActionClip> Of(DSTVehicleKind v, DSTActionKind k)
        {
            foreach (var c in clips)
                if (c.vehicle == v && c.kind == k) yield return c;
        }

        public int IndexOf(DSTActionClip c) => clips.IndexOf(c);
        public DSTActionClip At(int i) => i >= 0 && i < clips.Count ? clips[i] : null;
    }
}
