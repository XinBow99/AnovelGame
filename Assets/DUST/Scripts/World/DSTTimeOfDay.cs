using DUST.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.World
{
    // 日夜循環：遊戲一天 dayLengthMinutes 分鐘（預設 20：白天約 12 分鐘、夜晚約 8 分鐘）。
    // 太陽依時間繞場景移動並改變亮度 / 色溫；夜晚換月光，天空與曝光一起變暗。
    // 連線：主機記下「某個伺服器時間點 = 幾點」，大家用 NetworkManager.ServerTime 推算現在幾點，不用每幀同步。
    public class DSTTimeOfDay : NetworkBehaviour
    {
        [Header("Time")]
        public float dayLengthMinutes = 20f;
        [Range(0f, 24f)] public float startHour = 9f;
        public float sunriseHour = 5f;
        public float sunsetHour = 19.4f;          // 日出到日落 14.4 小時 = 20 分鐘的一天裡約 12 分鐘

        [Header("Sun / Moon")]
        public Light sun;
        public Light moon;
        public float sunMaxElevation = 55f;
        public float sunNoonYaw = 219f;           // 正午時太陽光的水平方向（沿用原本打光的角度）
        public float sunLux = 100000f;
        public float moonLux = 4f;

        [Header("Sky / Exposure")]
        public Volume volume;                     // 這個 Volume 的 profile 只覆寫曝光、天空亮度、色調
        public float dayExposure = 13.8f;
        public float nightExposure = 3.5f;        // 曝光下限（最暗的夜晚）
        public float daySkyLux = 20000f;          // 白天天空帶來的環境光（估算曝光用）
        [Range(0f, 1f)] public float exposureAdaptation = 0.75f;   // 光線變暗時曝光補償幾成：1 = 跟白天一樣亮、0 = 不補償
        public float daySkyMultiplier = 46987f;  // 白天 HDRI 天空的 Multiplier（沿用 Volume_Global 的值，Setup 時自動帶入）
        public float nightSkyExposure = -13f;     // 夜晚天空相對白天暗幾格（EV）

        public static DSTTimeOfDay Instance { get; private set; }

        public float Hour { get; private set; }
        /// <summary>從開房起一直累加的時數（不會繞回 0）：發條世界（動物遷徙路線）用它算「第幾天的幾點」</summary>
        public double TotalHours { get; private set; }
        public float SunElevation { get; private set; }
        public bool IsNight => SunElevation < -4f;
        public float SecondsPerHour => dayLengthMinutes * 60f / 24f;

        // 錨點存的是累加時數（不是 0~24 的鐘點），大家才算得出同一個「第幾天」
        readonly NetworkVariable<double> _anchorHour = new NetworkVariable<double>();
        readonly NetworkVariable<double> _anchorServerTime = new NetworkVariable<double>();

        double _localAnchorHour;
        float _localAnchorTime;
        Exposure _exposure;
        HDRISky _sky;
        ColorAdjustments _color;
        float _appliedSkyEV = float.NaN;
        GUIStyle _clockStyle;

        float HoursPerSecond => 24f / (dayLengthMinutes * 60f);

        void Awake()
        {
            Instance = this;
            _localAnchorHour = startHour;
            _localAnchorTime = Time.time;
            if (volume)
            {
                // 用執行期複本，不要改到專案裡的 profile 資產
                var p = volume.profile;
                p.TryGet(out _exposure);
                p.TryGet(out _sky);
                p.TryGet(out _color);
            }
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            // 開房：有存檔就接著存檔的時間走，新世界接著選單畫面的時間
            if (IsServer) SetTotal(DSTSave.SavedHours ?? LocalTotal());
        }

        public override void OnNetworkDespawn()
        {
            // 離開房間回到選單：從現在的時間繼續在本機走
            _localAnchorHour = TotalHours;
            _localAnchorTime = Time.time;
        }

        // 主機跳到指定時間（測試、之後的睡覺功能）：時間只往前走，比現在早的鐘點 = 隔天
        public void SetHour(float hour)
        {
            hour = Mathf.Repeat(hour, 24f);
            double now = CurrentTotal();
            double t = System.Math.Floor(now / 24.0) * 24.0 + hour;
            if (t < now - 0.01) t += 24.0;
            SetTotal(t);
        }

        /// <summary>主機：時間往前跳幾小時（/wait）</summary>
        public void Advance(double hours)
        {
            if (hours > 0) SetTotal(CurrentTotal() + hours);
        }

        void SetTotal(double total)
        {
            if (IsSpawned)
            {
                if (!IsServer) return;
                _anchorHour.Value = total;
                _anchorServerTime.Value = NetworkManager.ServerTime.Time;
            }
            _localAnchorHour = total;
            _localAnchorTime = Time.time;
        }

        double LocalTotal() => _localAnchorHour + (Time.time - _localAnchorTime) * HoursPerSecond;

        double CurrentTotal()
        {
            if (!IsSpawned) return LocalTotal();
            double elapsed = NetworkManager.ServerTime.Time - _anchorServerTime.Value;
            return _anchorHour.Value + elapsed * HoursPerSecond;
        }

        void Update()
        {
            TotalHours = CurrentTotal();
            Hour = (float)(TotalHours - System.Math.Floor(TotalHours / 24.0) * 24.0);
            ApplyLighting();
        }

        // ------------------------------------------------------------------ 光線
        /// <summary>某個鐘點太陽的仰角與光線水平方向（光往 Euler(仰角, yaw) 的前方照）：遺跡刻文用它預算哪個時刻光會斜斜掃過碑面</summary>
        public void SunAngles(float hour, out float elevation, out float yaw)
        {
            float dayLen = sunsetHour - sunriseHour;
            if (hour >= sunriseHour && hour <= sunsetHour)
            {
                float t = (hour - sunriseHour) / dayLen;                 // 0 日出 → 1 日落
                elevation = sunMaxElevation * Mathf.Sin(t * Mathf.PI);
                yaw = sunNoonYaw + (t - 0.5f) * 180f;
            }
            else
            {
                float nightLen = 24f - dayLen;
                float t = Mathf.Repeat(hour - sunsetHour, 24f) / nightLen; // 0 日落 → 1 日出
                elevation = -30f * Mathf.Sin(t * Mathf.PI);
                yaw = sunNoonYaw + 90f + t * 180f;
            }
        }

        void ApplyLighting()
        {
            SunAngles(Hour, out float elevation, out float yaw);
            SunElevation = elevation;

            // 0 = 夜、1 = 白天；天空在日落後 8° 內還有餘暉
            float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-8f, 8f, elevation));
            float skyEV = Mathf.Lerp(nightSkyExposure, 0f, day);
            // 黃昏 / 清晨：太陽在地平線附近
            float golden = Mathf.Clamp01(1f - Mathf.Abs(elevation - 2f) / 12f);

            if (sun)
            {
                bool up = elevation > -1f;
                sun.enabled = up;
                if (up)
                {
                    sun.transform.rotation = Quaternion.Euler(Mathf.Max(elevation, 0.5f), yaw, 0f);
                    sun.intensity = sunLux * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-1f, 30f, elevation));
                    sun.useColorTemperature = true;
                    sun.colorTemperature = Mathf.Lerp(2400f, 5600f, Mathf.InverseLerp(0f, 25f, elevation));
                }
            }
            if (moon)
            {
                bool show = elevation < -1f;
                moon.enabled = show;
                if (show)
                {
                    // 月亮在太陽對面，夜越深越高
                    moon.transform.rotation = Quaternion.Euler(Mathf.Clamp(-elevation + 15f, 15f, 60f), yaw + 180f, 0f);
                    moon.intensity = moonLux;
                }
            }

            if (_exposure)
            {
                // 曝光跟著「實際有多少光」走（太陽 + 天空 + 月亮），只補償一部分（像眼睛適應），
                // 所以天色越暗畫面就越暗，黃昏不會忽亮忽暗
                float lux = (sun && sun.enabled ? sun.intensity : 0f) + daySkyLux * Mathf.Pow(2f, skyEV) + (moon && moon.enabled ? moon.intensity : 0f);
                float ev = dayExposure + exposureAdaptation * Mathf.Log(lux / (sunLux + daySkyLux), 2f);
                _exposure.fixedExposure.value = Mathf.Clamp(ev, nightExposure, dayExposure);
            }
            if (_sky)
            {
                // 天空每變一次要重算環境光，變化夠大才更新。
                // 原本的天空用 Multiplier 模式，exposure 參數不會生效，所以直接調 multiplier
                if (float.IsNaN(_appliedSkyEV) || Mathf.Abs(skyEV - _appliedSkyEV) > 0.15f)
                {
                    _sky.multiplier.value = daySkyMultiplier * Mathf.Pow(2f, skyEV);
                    _appliedSkyEV = skyEV;
                }
            }
            if (_color)
            {
                var warm = new Color(1f, 0.82f, 0.66f);
                var night = new Color(0.62f, 0.74f, 1f);
                var c = Color.Lerp(Color.white, warm, golden * day);
                _color.colorFilter.value = Color.Lerp(night, c, Mathf.Max(day, golden * 0.6f));
                _color.saturation.value = Mathf.Lerp(-35f, 0f, day);
            }
        }

        // ------------------------------------------------------------------ 時鐘
        void OnGUI()
        {
            if (DSTNetPlayer.Local == null) return;   // 選單畫面不顯示
            if (_clockStyle == null)
                _clockStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            int h = Mathf.FloorToInt(Hour), m = Mathf.FloorToInt((Hour - h) * 60f);
            // 第幾天：發條沙漠的事件（刻文的線索）都用天數講
            int dayNo = (int)System.Math.Floor(TotalHours / 24.0) + 1;
            string text = $"第 {dayNo} 天  {(IsNight ? "夜" : "日")}  {h:00}:{m:00}";
            var r = new Rect(Screen.width - 312, 10, 300, 32);
            var c = _clockStyle.normal.textColor;
            _clockStyle.normal.textColor = new Color(0f, 0f, 0f, 0.7f);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, _clockStyle);
            _clockStyle.normal.textColor = Color.white;
            GUI.Label(r, text, _clockStyle);
            _clockStyle.normal.textColor = c;
        }
    }
}
