using System;
using System.Collections.Generic;
using System.IO;
using DUST.Animals;
using DUST.Player;
using Unity.Netcode;
using UnityEngine;

namespace DUST.World
{
    // 世界存檔（只有房主寫）。格式參考 Minecraft 的資料夾結構（level.dat / region / playerdata）：
    //   saves/<種子>/world.json             種子、累加時數、存檔版本
    //   saves/<種子>/regions/<rx>_<rz>.json 1.2 km 地區格：族群快照 + 玩家活動累計（只有去過的地區才有檔案）
    //   saves/<種子>/events.log             只往後加的事件紀錄（一行一筆 JSON），同時是這個世界的歷史
    //   saves/<種子>/players/<id>.json      每位玩家的位置、血量
    // 同一個種子 = 同一個世界：選單輸入同樣的種子就會接著玩。遺跡二不存檔。
    public static class DSTSave
    {
        public const int Version = 1;
        const float AutosaveSeconds = 30f;

        [Serializable]
        public class WorldFile
        {
            public int version;
            public int seed;
            public double totalHours = -1;
            public string created;
            public string lastPlayed;
        }

        [Serializable]
        public class PlayerFile
        {
            public string id;
            public string name;
            public float x, y, z, yaw;
            public float health;
        }

        [Serializable]
        public class SpeciesPop
        {
            public int key;
            public string name;
            public float n;        // 現在的數量（族群推進用小數）
            public float n0;       // 發條世界原本的數量（= 草食動物的承載量）
            public bool extinct;

            // ---- 演化（第 5 階段）。key 永遠是發條世界的物種（群用它認），form 是現在的物種
            public int form;                   // 0 = 還是原本的物種
            public Vector3 lineage;            // 新物種相對 key 的累積倍率（大小、跑速、警覺距離）；(0,0,0) = 沒有
            public float zSize, zSpeed, zNotice;           // 族群平均相對「現在的物種」的偏移（對數）
            public float selSize, selSpeed, selNotice;     // 這一代死掉的個體偏差加總（天擇用）
            public int selDeaths;
            public int Form => form != 0 ? form : key;
        }

        [Serializable]
        public class RegionFile
        {
            public int rx, rz;
            public string name;
            public int day;        // 族群推進到第幾天（從 0 起算）
            public List<SpeciesPop> species = new List<SpeciesPop>();
            // 玩家活動累計（DSTPlayerActivity 的 16 m 格子，秒數）
            public List<int> wx = new List<int>();
            public List<int> wz = new List<int>();
            public List<float> ws = new List<float>();
        }

        [Serializable]
        public class EventRec
        {
            public double t;       // 累加時數
            public string type;    // hunt / death / starve / player_death / extinct / visit / open / speciate
            public string who;
            public string what;
            public int key;
            public int rx, rz;
            public float x, z;
        }

        public static string Root => Path.Combine(Application.persistentDataPath, "saves");
        public static string Dir { get; private set; }
        public static bool Active => Dir != null;
        public static int Seed { get; private set; }
        public static WorldFile World { get; private set; }
        public static bool IsNewWorld { get; private set; }

        static readonly Dictionary<string, PlayerFile> _players = new Dictionary<string, PlayerFile>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Dir = null; World = null; _players.Clear(); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var go = new GameObject("DSTSave");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<DSTSaveDriver>();
        }

        // ------------------------------------------------------------------ 開 / 關
        /// <summary>房主開房前（選單）：打開這個種子的存檔，沒有就建一個新的</summary>
        public static void Open(int seed)
        {
            Close();
            Seed = seed;
            Dir = Path.Combine(Root, seed.ToString());
            Directory.CreateDirectory(Path.Combine(Dir, "regions"));
            Directory.CreateDirectory(Path.Combine(Dir, "players"));
            World = Read<WorldFile>(Path.Combine(Dir, "world.json"));
            IsNewWorld = World == null;
            if (IsNewWorld) World = new WorldFile { version = Version, seed = seed, created = Stamp() };
            _players.Clear();
            Debug.Log($"[DUST] Save {(IsNewWorld ? "created" : "opened")}: {Dir}");
        }

        public static void Close()
        {
            Dir = null;
            World = null;
            _players.Clear();
        }

        /// <summary>存檔裡的時間（新世界 = null，照原本的 startHour）</summary>
        public static double? SavedHours => World != null && World.totalHours >= 0 ? World.totalHours : (double?)null;

        // ------------------------------------------------------------------ 存
        public static void SaveAll()
        {
            if (!Active) return;
            try
            {
                if (DSTTimeOfDay.Instance) World.totalHours = DSTTimeOfDay.Instance.TotalHours;
                World.lastPlayed = Stamp();
                Write(Path.Combine(Dir, "world.json"), World);
                foreach (var p in DSTNetPlayer.All) if (p && p.IsSpawned) StorePlayer(p);
                foreach (var kv in _players) Write(PlayerPath(kv.Key), kv.Value);
                foreach (var r in DSTPopulation.Regions())
                {
                    r.wx.Clear(); r.wz.Clear(); r.ws.Clear();
                    DSTPlayerActivity.Export(r.rx, r.rz, r.wx, r.wz, r.ws);
                    Write(RegionPath(r.rx, r.rz), r);
                }
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        // ------------------------------------------------------------------ 地區
        public static RegionFile LoadRegion(int rx, int rz) => Active ? Read<RegionFile>(RegionPath(rx, rz)) : null;

        public static IEnumerable<RegionFile> LoadAllRegions()
        {
            if (!Active) yield break;
            foreach (var f in Directory.GetFiles(Path.Combine(Dir, "regions"), "*.json"))
            {
                var r = Read<RegionFile>(f);
                if (r != null) yield return r;
            }
        }

        static string RegionPath(int rx, int rz) => Path.Combine(Dir, "regions", $"{rx}_{rz}.json");

        // ------------------------------------------------------------------ 玩家
        /// <summary>這台電腦的玩家身分（第一次玩時產生，之後都一樣）</summary>
        public static string LocalPlayerId
        {
            get
            {
                const string key = "dust.playerId";
                string id = PlayerPrefs.GetString(key, "");
                if (id.Length == 0)
                {
                    id = Guid.NewGuid().ToString("N");
                    PlayerPrefs.SetString(key, id);
                    PlayerPrefs.Save();
                }
                return id;
            }
        }

        public static PlayerFile LoadPlayer(string id)
        {
            if (!Active || string.IsNullOrEmpty(id)) return null;
            if (_players.TryGetValue(id, out var p)) return p;
            return Read<PlayerFile>(PlayerPath(id));
        }

        /// <summary>主機：記下玩家現在的狀態（離線時也會呼叫，關房時一起寫檔）</summary>
        public static void StorePlayer(DSTNetPlayer p)
        {
            if (!Active || !p || string.IsNullOrEmpty(p.SaveId)) return;
            var t = p.transform;
            _players[p.SaveId] = new PlayerFile
            {
                id = p.SaveId, name = p.DisplayName,
                x = t.position.x, y = t.position.y, z = t.position.z, yaw = t.eulerAngles.y,
                health = p.Health,
            };
        }

        static string PlayerPath(string id)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return Path.Combine(Dir, "players", id + ".json");
        }

        // ------------------------------------------------------------------ 事件紀錄
        static readonly List<EventRec> _recent = new List<EventRec>();

        public static void Log(string type, string who, string what, Vector3 at, int key = 0)
        {
            if (!Active) return;
            int rx = Mathf.FloorToInt(at.x / DSTWorldGen.RegionCell), rz = Mathf.FloorToInt(at.z / DSTWorldGen.RegionCell);
            var e = new EventRec
            {
                t = DSTTimeOfDay.Instance ? DSTTimeOfDay.Instance.TotalHours : 0.0,
                type = type, who = who ?? "", what = what ?? "", key = key, rx = rx, rz = rz, x = at.x, z = at.z,
            };
            try { File.AppendAllText(Path.Combine(Dir, "events.log"), JsonUtility.ToJson(e) + "\n"); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        /// <summary>最近 n 筆事件（從檔案尾端讀）</summary>
        public static List<EventRec> Recent(int n)
        {
            _recent.Clear();
            if (!Active) return _recent;
            string path = Path.Combine(Dir, "events.log");
            if (!File.Exists(path)) return _recent;
            var lines = File.ReadAllLines(path);
            for (int i = Mathf.Max(0, lines.Length - n); i < lines.Length; i++)
            {
                try { var e = JsonUtility.FromJson<EventRec>(lines[i]); if (e != null) _recent.Add(e); }
                catch { }
            }
            return _recent;
        }

        public static string Describe(EventRec e)
        {
            double day = Math.Floor(e.t / 24.0);
            double h = e.t - day * 24.0;
            string when = $"第 {day + 1} 天 {(int)h:00}:{(int)((h - (int)h) * 60):00}";
            string text = e.type switch
            {
                "hunt" => $"{e.who} 殺了一隻{e.what}",
                "death" => $"一隻{e.what}被{e.who}吃了",
                "starve" => $"一隻{e.what}餓死了",
                "player_death" => $"{e.who} 被{e.what}咬死了",
                "extinct" => $"{e.what}在{e.who}絕跡了",
                "visit" => $"{e.who} 第一次來到{e.what}",
                "speciate" => $"{e.who}演化成了新物種：{e.what}",
                "open" => $"{e.who} 開啟了這個世界",
                _ => $"{e.type} {e.who} {e.what}",
            };
            return when + "　" + text;
        }

        // ------------------------------------------------------------------ 選單：最近玩過的世界
        public static WorldFile Latest()
        {
            WorldFile best = null;
            try
            {
                if (!Directory.Exists(Root)) return null;
                foreach (var d in Directory.GetDirectories(Root))
                {
                    var w = Read<WorldFile>(Path.Combine(d, "world.json"));
                    if (w != null && w.lastPlayed != null && (best == null || string.CompareOrdinal(w.lastPlayed, best.lastPlayed) > 0)) best = w;
                }
            }
            catch (Exception e) { Debug.LogWarning("[DUST] List saves: " + e.Message); }
            return best;
        }

        // ------------------------------------------------------------------ 檔案
        static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        static T Read<T>(string path) where T : class
        {
            try { return File.Exists(path) ? JsonUtility.FromJson<T>(File.ReadAllText(path)) : null; }
            catch (Exception e) { Debug.LogWarning($"[DUST] Read {path}: {e.Message}"); return null; }
        }

        // 先寫暫存檔再換掉，寫到一半當掉也不會留下壞掉的存檔
        static void Write(string path, object o)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(o, true));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        internal static float AutosaveInterval => AutosaveSeconds;
    }

    // 房主：世界準備好以後載入地區、每 30 秒自動存檔、關房 / 關遊戲時存檔
    public class DSTSaveDriver : MonoBehaviour
    {
        bool _loaded;
        float _nextSave, _nextTick;

        static bool IsHost => NetworkManager.Singleton && NetworkManager.Singleton.IsListening && NetworkManager.Singleton.IsServer;

        void Update()
        {
            var world = DSTWorld.Instance;
            if (!DSTSave.Active || !IsHost || !world || !world.IsSpawned || !world.IsRandom || world.Seed != DSTSave.Seed)
            {
                _loaded = false;
                return;
            }
            if (!_loaded)
            {
                _loaded = true;
                DSTPopulation.Load(DSTSave.Seed);
                _nextSave = Time.time + DSTSave.AutosaveInterval;
                if (DSTSave.IsNewWorld) DSTSave.Log("open", DSTNetPlayer.Local ? DSTNetPlayer.Local.DisplayName : "", "", Vector3.zero);
            }
            if (Time.time >= _nextTick)
            {
                _nextTick = Time.time + 2f;
                DSTPopulation.Tick();
            }
            if (Time.time >= _nextSave)
            {
                _nextSave = Time.time + DSTSave.AutosaveInterval;
                DSTSave.SaveAll();
            }
        }

        void OnApplicationQuit()
        {
            if (_loaded) DSTSave.SaveAll();
        }
    }
}
