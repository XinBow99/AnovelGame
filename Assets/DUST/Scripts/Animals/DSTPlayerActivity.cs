using System.Collections.Generic;
using DUST.World;
using Unity.Netcode;
using UnityEngine;

namespace DUST.Animals
{
    // 玩家活動指數 W：玩家（和有人開的車）在哪裡待過多久。
    // 參考 Minecraft 每個 chunk 的 InhabitedTime（玩家在附近時每 tick 加 1、多人累加、只增不減），
    // 用途對應 Fortin 2005 的「狼指數」：W 高的地方，動物從水源改去遮蔽處（見 DSTStepSelection）。
    // 只在主機累計；存在地區檔裡（DSTSave），換世界就清掉。
    public class DSTPlayerActivity : MonoBehaviour
    {
        public const float Cell = 16f;              // 格子大小（公尺）
        const float Interval = 0.5f;                // 每 0.5 秒累計一次
        const float Saturation = 600f;              // 待了 10 分鐘（含周圍擴散）W 約 0.95【建議，可調】
        const float MaxW = 1.5f;                    // 麋鹿資料的狼指數大約落在 0 ~ 1.2

        // 擴散核：中心格 + 周圍兩圈，權重 exp(−d²/2)
        static readonly float[,] Kernel = MakeKernel();

        static readonly Dictionary<long, float> _seconds = new Dictionary<long, float>();
        static readonly List<(Vector3 pos, ThreatKind kind)> _threats = new List<(Vector3, ThreatKind)>();
        static int _seed = int.MinValue;
        float _next;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _seconds.Clear(); _seed = int.MinValue; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var go = new GameObject("DSTPlayerActivity");
            DontDestroyOnLoad(go);
            go.AddComponent<DSTPlayerActivity>();
            go.AddComponent<DSTStepDebug>();
        }

        static bool Authority => !NetworkManager.Singleton || !NetworkManager.Singleton.IsListening || NetworkManager.Singleton.IsServer;

        void Update()
        {
            if (Time.time < _next || !Authority) return;
            _next = Time.time + Interval;

            int seed = DSTWorld.Instance ? DSTWorld.Instance.Seed : 0;
            if (seed != _seed) { _seconds.Clear(); _seed = seed; }

            DSTThreats.All(_threats);
            foreach (var (pos, kind) in _threats)
            {
                // 開車的動靜比走路大；停著的車算一半
                float amount = Interval * kind switch
                {
                    ThreatKind.MovingVehicle => 2f,
                    ThreatKind.IdleVehicle => 0.5f,
                    _ => 1f,
                };
                Deposit(pos, amount);
            }
        }

        static void Deposit(Vector3 p, float seconds)
        {
            int cx = Mathf.FloorToInt(p.x / Cell), cz = Mathf.FloorToInt(p.z / Cell);
            int r = Kernel.GetLength(0) / 2;
            for (int i = -r; i <= r; i++)
            for (int j = -r; j <= r; j++)
            {
                long k = Key(cx + i, cz + j);
                _seconds.TryGetValue(k, out float v);
                _seconds[k] = v + seconds * Kernel[i + r, j + r];
            }
        }

        /// <summary>這個位置的玩家指數 W（0 ~ 1.5），格子之間雙線性內插</summary>
        public static float W(Vector3 p)
        {
            if (_seconds.Count == 0) return 0f;
            float fx = p.x / Cell - 0.5f, fz = p.z / Cell - 0.5f;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            float tx = fx - x0, tz = fz - z0;
            float s = Mathf.Lerp(Mathf.Lerp(Get(x0, z0), Get(x0 + 1, z0), tx), Mathf.Lerp(Get(x0, z0 + 1), Get(x0 + 1, z0 + 1), tx), tz);
            return MaxW * (1f - Mathf.Exp(-s / Saturation));
        }

        /// <summary>某一格累計的秒數（除錯畫面用）</summary>
        public static float CellW(int cx, int cz) => MaxW * (1f - Mathf.Exp(-Get(cx, cz) / Saturation));

        // ------------------------------------------------------------------ 存檔（DSTSave 的地區檔）
        /// <summary>地區格 (rx, rz) 裡累計過的格子</summary>
        public static void Export(int rx, int rz, List<int> xs, List<int> zs, List<float> secs)
        {
            float region = DSTWorldGen.RegionCell;
            foreach (var kv in _seconds)
            {
                int x = (int)(kv.Key >> 32), z = (int)kv.Key;
                if (Mathf.FloorToInt(x * Cell / region) != rx || Mathf.FloorToInt(z * Cell / region) != rz || kv.Value < 0.5f) continue;
                xs.Add(x); zs.Add(z); secs.Add(Mathf.Round(kv.Value * 10f) / 10f);
            }
        }

        public static void Import(int seed, List<int> xs, List<int> zs, List<float> secs)
        {
            if (seed != _seed) { _seconds.Clear(); _seed = seed; }
            int n = Mathf.Min(xs.Count, Mathf.Min(zs.Count, secs.Count));
            for (int i = 0; i < n; i++) _seconds[Key(xs[i], zs[i])] = secs[i];
        }

        static float Get(int cx, int cz) => _seconds.TryGetValue(Key(cx, cz), out float v) ? v : 0f;
        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        static float[,] MakeKernel()
        {
            const int r = 2;
            var k = new float[r * 2 + 1, r * 2 + 1];
            for (int i = -r; i <= r; i++)
            for (int j = -r; j <= r; j++)
                k[i + r, j + r] = Mathf.Exp(-(i * i + j * j) / 2f);
            return k;
        }
    }
}
