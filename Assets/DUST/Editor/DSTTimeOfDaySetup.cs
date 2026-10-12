using DUST.World;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.EditorTools
{
    // 在目前場景加上日夜循環：TimeOfDay（連線同步時間）、月光、覆寫曝光 / 天空亮度 / 色調的 Volume。可以重複執行。
    public static class DSTTimeOfDaySetup
    {
        const string ProfilePath = "Assets/DUST/DR2_Map/VP_DR2_TimeOfDay.asset";

        [MenuItem("DUST/World/Setup Day-Night Cycle")]
        public static void Setup()
        {
            var sun = GameObject.Find("Sun")?.GetComponent<Light>();
            if (!sun) { Debug.LogError("[DUST] 場景裡找不到 Sun"); return; }

            // ---------------- 月光 ----------------
            var moonGo = GameObject.Find("Moon") ?? new GameObject("Moon");
            var moon = Ensure<Light>(moonGo);
            Ensure<HDAdditionalLightData>(moonGo);
            moon.type = LightType.Directional;
            moon.lightUnit = LightUnit.Lux;
            moon.intensity = 4f;
            moon.useColorTemperature = true;
            moon.colorTemperature = 9500f;
            moon.shadows = LightShadows.Soft;
            moon.transform.rotation = Quaternion.Euler(45f, sun.transform.eulerAngles.y + 180f, 0f);
            moon.enabled = false;                       // 白天由 DSTTimeOfDay 關掉

            // ---------------- 覆寫用的 Volume ----------------
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (!profile)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }
            var exposure = Get<Exposure>(profile);
            exposure.mode.Override(ExposureMode.Fixed);
            exposure.fixedExposure.Override(13.8f);
            // 原本的天空是 Multiplier 模式：日夜只調 multiplier
            float dayMultiplier = 46987f;
            var baseVol = GameObject.Find("Volume_Global")?.GetComponent<Volume>();
            if (baseVol && baseVol.sharedProfile.TryGet<HDRISky>(out var baseSky)) dayMultiplier = baseSky.multiplier.value;
            var sky = Get<HDRISky>(profile);
            sky.exposure.overrideState = false;
            sky.multiplier.Override(dayMultiplier);
            var color = Get<ColorAdjustments>(profile);
            color.colorFilter.Override(Color.white);
            color.saturation.Override(0f);
            EditorUtility.SetDirty(profile);

            var volGo = GameObject.Find("Volume_TimeOfDay") ?? new GameObject("Volume_TimeOfDay");
            var vol = Ensure<Volume>(volGo);
            vol.isGlobal = true;
            vol.priority = 10f;                         // 蓋過 Volume_Global 的同名參數
            vol.sharedProfile = profile;

            // ---------------- 時間（連線同步） ----------------
            var todGo = GameObject.Find("TimeOfDay") ?? new GameObject("TimeOfDay");
            Ensure<NetworkObject>(todGo);
            var tod = Ensure<DSTTimeOfDay>(todGo);
            tod.sun = sun;
            tod.moon = moon;
            tod.volume = vol;
            tod.daySkyMultiplier = dayMultiplier;
            EditorUtility.SetDirty(tod);

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(todGo.scene);
            EditorSceneManager.SaveScene(todGo.scene);
            Debug.Log("[DUST] Day-night cycle set up");
        }

        // 編輯器裡 GetComponent 找不到時會回傳假 null，不能用 ??
        static T Ensure<T>(GameObject go) where T : Component => go.TryGetComponent<T>(out var c) ? c : go.AddComponent<T>();

        static T Get<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (profile.TryGet<T>(out var c)) return c;
            c = profile.Add<T>();
            c.name = typeof(T).Name;
            AssetDatabase.AddObjectToAsset(c, profile);
            return c;
        }
    }
}
