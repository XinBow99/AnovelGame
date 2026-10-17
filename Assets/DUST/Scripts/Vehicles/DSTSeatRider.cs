using DUST.Player;
using UnityEngine;

namespace DUST.Vehicles
{
    // 沒有專屬上下車動作的載具（米格-25、Roadster）：駕駛的鬃狼要真的坐進去。
    // 以前這兩台只關掉角色的碰撞和輸入，鬃狼模型（和影子）留在上車的地方（玩家回報）。
    // 做法：每台電腦都看 DSTVehicleEntry 的駕駛是誰，把那位玩家的鬃狼模型掛到座位錨點上，播皮卡的「坐著開車」循環
    //（動作是在皮卡座標裡做的；錨點 = 這台的駕駛眼睛 − 皮卡的駕駛眼睛，所以頭會在這台的駕駛眼睛附近）。
    // 下車（駕駛換掉）時模型掛回玩家身上。自己開、切到駕駛座視角時身體只投影子（鏡頭就在頭裡）。
    [RequireComponent(typeof(DSTVehicleEntry))]
    public class DSTSeatRider : MonoBehaviour
    {
        public DSTActionLibrary library;
        public Transform anchor;
        public string loopClip = "PL_Pickup_Drive";
        public string steerLeftClip = "PL_Pickup_Drive_SteerLeft";
        public string steerRightClip = "PL_Pickup_Drive_SteerRight";

        const ulong NoDriver = ulong.MaxValue;
        DSTVehicleEntry _entry;
        DSTRoadsterController _roadster;
        DSTWolfRig _rig;
        DSTNetPlayer _player;
        ulong _seated = NoDriver;
        bool _shadowOnly;

        void Awake()
        {
            _entry = GetComponent<DSTVehicleEntry>();
            _roadster = GetComponent<DSTRoadsterController>();
        }

        void OnDisable() => Release();

        void LateUpdate()
        {
            if (!_entry || !library || !anchor) return;
            ulong want = _entry.IsSpawned && _entry.HasDriver ? _entry.DriverId : NoDriver;
            if (want != _seated || (_rig && _rig.model.parent != anchor))
            {
                Release();
                if (want != NoDriver) Seat(want);
            }
            if (!_rig) return;
            // 方向盤：+1 = 左轉姿勢（跟皮卡一樣）
            float steer = _roadster ? -Mathf.Clamp(_roadster.SteerDeg / Mathf.Max(_roadster.maxSteerDeg, 1f), -1f, 1f) : 0f;
            _rig.SetRideSteer(steer);
            bool cockpit = _player && _player.IsOwner && _entry.vehicleCamera && _entry.vehicleCamera.cockpit && _entry.vehicleCamera.isActiveAndEnabled;
            if (cockpit != _shadowOnly) { _shadowOnly = cockpit; _rig.SetShadowOnly(cockpit); }
        }

        void Seat(ulong id)
        {
            foreach (var p in DSTNetPlayer.All)
                if (p && p.OwnerClientId == id) { _player = p; break; }
            _rig = _player ? _player.GetComponent<DSTWolfRig>() : null;
            if (!_rig) { _player = null; return; }
            var loop = library.Find(loopClip);
            if (loop == null || !loop.clip) { _rig = null; return; }
            _rig.PlayRide(anchor, loop, library.Find(steerLeftClip), library.Find(steerRightClip), 0.2f);
            // 走路時如果是第一人稱，身體被設成「只投影子」；上車後 DSTPlayerView 的鏡頭關掉、不會再改回來
            // → 坐在車裡變隱形（實測：車外看不到鬃狼）。這裡一律先顯示，駕駛座視角時再交給 LateUpdate。
            _rig.SetShadowOnly(false);
            _shadowOnly = false;
            _seated = id;
        }

        void Release()
        {
            if (_rig)
            {
                if (_shadowOnly) _rig.SetShadowOnly(false);
                if (_rig.InAction && _rig.model && _rig.model.parent == anchor) _rig.ReturnToLocomotion(0.15f);
            }
            _rig = null;
            _player = null;
            _shadowOnly = false;
            _seated = NoDriver;
        }
    }
}
