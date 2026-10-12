using UnityEngine;

namespace DUST.Vehicles
{
    // 可以上下車的載具（皮卡、無人機…）；上下車、連線、追車鏡頭都只認這個介面
    public interface IDSTDrivable
    {
        Transform transform { get; }
        // 開著的時候讀玩家輸入；設成 false 時要自己停好（拉手煞車、關引擎）
        bool PlayerControlled { get; set; }
        float SpeedKmh { get; }
        // 下車條件：皮卡要減速，無人機還要降落
        bool CanExit(float maxExitSpeedKmh, out string reason);
        string ControlsHint { get; }
    }
}
