using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.World
{
    // 沙暴（發條沙漠，見 DSTClockwork）：強度由時鐘算出來，每台電腦自己畫——霧拉近變黃、太陽變暗、
    // 鏡頭周圍飛沙、風聲。不用同步任何東西。
    public class DSTWeather : MonoBehaviour
    {
        public Material dustMaterial;              // M_DST_StormDust（HDRP/Unlit 透明）
        public float stormFogDistance = 14f;       // 最濃時的霧（mean free path）
        public Color stormFogColor = new Color(0.86f, 0.62f, 0.38f);
        public Color stormTint = new Color(1.6f, 0.95f, 0.5f);       // 乘上天空的顏色（藍天 × 這個 ≈ 黃褐）
        [Range(0f, 1f)] public float stormSunDim = 0.15f;
        public float windSpeed = 14f;

        public static float Intensity { get; private set; }

        Fog _fog;
        float _baseMfp, _baseHeight, _baseMaxHeight;
        Color _baseAlbedo, _baseTint;
        bool _touched;
        ParticleSystem _dust;
        AudioSource _wind;
        Vector3 _windDir;

        void Start()
        {
            BuildDust();
            BuildWind();
        }

        void Update()
        {
            float k = DSTClockwork.Storm(DSTClockwork.Now);
            Intensity = k;
            // 風向：每天不一樣，沙暴當中慢慢擺動
            float day = DSTClockwork.DayOf(DSTClockwork.Now);
            float yaw = DSTNoise.Hash01(DSTClockwork.Seed, (int)day, 0, 7110) * 360f + Mathf.Sin(Time.time * 0.07f) * 20f;
            _windDir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;

            ApplyFog(k);
            UpdateDust(k);
            if (_wind)
            {
                float gust = 0.75f + 0.25f * Mathf.PerlinNoise(Time.time * 0.35f, 3.1f);
                _wind.volume = Mathf.Lerp(_wind.volume, k * 0.55f * gust, Time.deltaTime * 2f);
                if (_wind.volume > 0.005f && !_wind.isPlaying) _wind.Play();
                else if (_wind.volume <= 0.005f && _wind.isPlaying) _wind.Stop();
            }
        }

        // 太陽亮度由 DSTTimeOfDay 每幀設定，這裡在它之後壓暗
        void LateUpdate()
        {
            var tod = DSTTimeOfDay.Instance;
            if (Intensity > 0f && tod && tod.sun && tod.sun.enabled)
                tod.sun.intensity *= Mathf.Lerp(1f, stormSunDim, Intensity);
        }

        void ApplyFog(float k)
        {
            if (!_fog)
            {
                var w = DSTWorld.Instance;
                if (!w || !w.fogVolume || !w.fogVolume.profile.TryGet(out _fog)) return;
            }
            if (k <= 0f)
            {
                if (_touched)
                {
                    _fog.meanFreePath.value = _baseMfp;
                    _fog.albedo.value = _baseAlbedo;
                    _fog.tint.value = _baseTint;
                    _fog.baseHeight.value = _baseHeight;
                    _fog.maximumHeight.value = _baseMaxHeight;
                    _touched = false;
                }
                return;
            }
            if (!_touched)
            {
                _baseMfp = _fog.meanFreePath.value;
                _baseAlbedo = _fog.albedo.value;
                _baseTint = _fog.tint.value;
                _baseHeight = _fog.baseHeight.value;
                _baseMaxHeight = _fog.maximumHeight.value;
                _touched = true;
            }
            _fog.meanFreePath.overrideState = true;
            _fog.albedo.overrideState = true;
            _fog.tint.overrideState = true;
            _fog.baseHeight.overrideState = true;
            _fog.maximumHeight.overrideState = true;
            // 指數內插：遠景先被吞掉，最後連眼前都看不清
            _fog.meanFreePath.value = Mathf.Exp(Mathf.Lerp(Mathf.Log(_baseMfp), Mathf.Log(stormFogDistance), Mathf.Pow(k, 0.8f)));
            _fog.albedo.value = Color.Lerp(_baseAlbedo, stormFogColor, k);
            // 霧的顏色取自天空（藍色），用 tint 染成沙色；霧層拉高，天空也被沙蓋住
            _fog.tint.value = Color.Lerp(_baseTint, stormTint, k);
            _fog.baseHeight.value = Mathf.Lerp(_baseHeight, _baseHeight + 80f, k);
            _fog.maximumHeight.value = Mathf.Lerp(_baseMaxHeight, _baseMaxHeight + 400f, k);
        }

        // ------------------------------------------------------------------ 飛沙：跟著鏡頭的一團粒子，順著風吹
        void BuildDust()
        {
            if (!dustMaterial) return;
            var go = new GameObject("StormDust");
            go.transform.SetParent(transform, false);
            _dust = go.AddComponent<ParticleSystem>();
            _dust.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = _dust.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.2f);
            main.startSpeed = 0f;
            main.startSize3D = true;
            main.startSizeX = new ParticleSystem.MinMaxCurve(0.04f, 0.1f);
            main.startSizeY = new ParticleSystem.MinMaxCurve(0.8f, 2.2f);
            main.startSizeZ = 1f;
            main.maxParticles = 4000;
            var em = _dust.emission;
            em.rateOverTime = 0f;
            var shape = _dust.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(36f, 10f, 36f);
            var vel = _dust.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.material = dustMaterial;
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.velocityScale = 0.05f;
            r.lengthScale = 2f;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        void UpdateDust(float k)
        {
            if (!_dust) return;
            var cam = Camera.main;
            if (cam) _dust.transform.position = cam.transform.position - _windDir * 8f;
            var em = _dust.emission;
            em.rateOverTime = k * 2200f;
            var vel = _dust.velocityOverLifetime;
            var v = _windDir * windSpeed * (0.6f + 0.4f * k);
            vel.x = new ParticleSystem.MinMaxCurve(v.x * 0.8f, v.x * 1.2f);
            vel.y = new ParticleSystem.MinMaxCurve(-0.6f, 0.8f);
            vel.z = new ParticleSystem.MinMaxCurve(v.z * 0.8f, v.z * 1.2f);
            if (k > 0.01f && !_dust.isPlaying) _dust.Play();
            else if (k <= 0.01f && _dust.isPlaying) _dust.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        // ------------------------------------------------------------------ 風聲：合成的低沉噪音（棕噪）+ 一點高頻沙沙聲
        void BuildWind()
        {
            const int rate = 22050, len = rate * 4;
            var data = new float[len];
            var rng = new System.Random(7);
            float brown = 0f, hiss = 0f;
            for (int i = 0; i < len; i++)
            {
                float w = (float)rng.NextDouble() * 2f - 1f;
                brown = Mathf.Clamp(brown + w * 0.02f, -1f, 1f) * 0.998f;
                hiss = hiss * 0.6f + w * 0.4f;
                data[i] = brown * 0.9f + (w - hiss) * 0.08f;
            }
            // 頭尾交疊，循環時不會喀一聲
            const int fade = rate / 4;
            for (int i = 0; i < fade; i++)
            {
                float t = i / (float)fade;
                data[i] = data[i] * t + data[len - fade + i] * (1f - t);
            }
            var clip = AudioClip.Create("Wind", len - fade, 1, rate, false);
            var trimmed = new float[len - fade];
            System.Array.Copy(data, trimmed, trimmed.Length);
            clip.SetData(trimmed, 0);
            _wind = gameObject.AddComponent<AudioSource>();
            _wind.clip = clip;
            _wind.loop = true;
            _wind.spatialBlend = 0f;
            _wind.volume = 0f;
            _wind.playOnAwake = false;
        }
    }
}
