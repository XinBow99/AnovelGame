using System;
using System.Globalization;
using DUST.Player;
using DUST.World;
using Unity.Netcode;
using UnityEngine;

namespace DUST.UI
{
    // 聊天框裡的 / 指令。/help、/pos、/seed、/clear 誰都能用；其他是作弊指令：
    // 房主（單人）一律可以，其他玩家要房主先 /cheats on。
    public static class DSTCommands
    {
        static readonly string[] HelpLines =
        {
            "/help　指令列表",
            "/creative [on|off]　創造模式（連按兩下 Space 飛行，Space 上升、Ctrl 下降、Shift 加速）",
            "/fly　切換飛行　　/noclip　飛行時穿牆",
            "/speed <倍率>　移動速度（不填 = 恢復 1）",
            "/tp <x> <z> | /tp <x> <y> <z> | /tp <玩家編號>　傳送",
            "/time <0-24 | 6:30 | day | noon | night | midnight>　調整時間",
            "/pos　目前座標　　/seed　世界種子　　/clear　清除聊天",
            "/pop　這個地區的族群數量與演化　　/evolve <編號> <size|speed|notice> <標準差>　（測試）推動演化/history [筆數]　世界的事件紀錄　　/save　立刻存檔（每 30 秒、關房時也會自動存）",
            "/species　這個地區有哪些物種　　/summon <編號>　在前面生出一群　　/herds　附近動物群在哪、幾點出門　　/ssf　動物步伐選擇除錯畫面　　/hunger [0-1]　附近 60 m 動物的能量（不填 = 0.1，很餓）　　/events　接下來幾天的沙暴、流星、集體遷徙　　/wait <小時>　時間往前跳",
            "/cheats on|off　（房主）開放作弊指令給其他玩家",
        };

        public static void Run(string line)
        {
            var args = line.Substring(1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0) return;
            string cmd = args[0].ToLowerInvariant();
            var me = DSTNetPlayer.Local;
            if (!me) return;
            var pc = me.Controller;

            // 一般指令
            switch (cmd)
            {
                case "help": case "?":
                    foreach (var h in HelpLines) DSTChat.System(h);
                    return;
                case "pos":
                    var p = me.transform.position;
                    DSTChat.System($"座標 {p.x:0.0}, {p.y:0.0}, {p.z:0.0}　面向 {me.transform.eulerAngles.y:0}°");
                    return;
                case "seed":
                    var world = DSTWorld.Instance;
                    DSTChat.System(world && world.IsRandom ? $"世界種子 {world.Seed}" : "目前是遺跡二地圖（沒有種子）");
                    return;
                case "clear":
                    DSTChat.Clear();
                    return;

                case "pop":
                {
                    if (!HostOnly("族群在房主那邊推進")) return;
                    var c = DUST.Animals.DSTPopulation.RegionOf(me.transform.position);
                    var r = DUST.Animals.DSTPopulation.Get(c.x, c.y);
                    if (r == null) { DSTChat.System("這個地區還沒有族群紀錄（隨機世界、走進來幾秒後才會開始）"); return; }
                    DSTChat.System($"{r.name} 的族群（推進到第 {r.day + 1} 天，一代 = {DUST.Animals.DSTPopulation.GenerationDays:0} 天）：");
                    for (int i = 0; i < r.species.Count; i++)
                    {
                        var s = r.species[i];
                        var g = DUST.Animals.DSTGenome.Get(s.Form);
                        string diet = g.Carnivore ? "肉食" : "草食";
                        if (s.extinct) { DSTChat.System($"{i + 1}. {s.name}（{diet}）　已絕跡（原本 {s.n0:0} 隻）"); continue; }
                        int score = DUST.Animals.DSTEvolution.Score(s, out var d);
                        string from = s.form != 0 ? $"（從{DUST.Animals.DSTGenome.Get(s.key).name}演化來的）" : "";
                        DSTChat.System($"{i + 1}. {s.name}{from}（{diet}，{g.Mass * Mathf.Exp(3f * s.zSize):0} kg）　{s.n:0.#} 隻　原本 {s.n0:0} 隻（{s.n / Mathf.Max(1f, s.n0) * 100f:0}%）");
                        DSTChat.System($"　　演化：體型 {Pct(s.zSize)}（d {d.x:0.0}）　跑速 {Pct(s.zSpeed)}（d {d.y:0.0}）　警覺距離 {Pct(s.zNotice)}（d {d.z:0.0}）　分化分數 {score}/{DUST.Animals.DSTEvolution.SpeciesScore}　這一代死了 {s.selDeaths} 隻");
                    }
                    return;
                }

                case "evolve":
                {
                    // 測試用：直接把族群平均推一段（單位 = 個體標準差），看分化會不會觸發
                    if (!HostOnly("族群在房主那邊推進")) return;
                    if (!DSTNetPlayer.CheatsAllowed) { DSTChat.System("這是作弊指令"); return; }
                    if (args.Length < 4 || !int.TryParse(args[1], out int idx) || !TryFloat(args[3], out float sd))
                    { DSTChat.System("用法：/evolve <編號（/pop 的）> <size|speed|notice> <幾個標準差，可負>"); return; }
                    var c = DUST.Animals.DSTPopulation.RegionOf(me.transform.position);
                    var r = DUST.Animals.DSTPopulation.Get(c.x, c.y);
                    if (r == null || idx < 1 || idx > r.species.Count) { DSTChat.System("沒有這個編號（先 /pop）"); return; }
                    var s = r.species[idx - 1];
                    var cv = DUST.Animals.DSTEvolution.CV;
                    switch (args[2].ToLowerInvariant())
                    {
                        case "size": s.zSize += sd * cv.x; break;
                        case "speed": s.zSpeed += sd * cv.y; break;
                        case "notice": s.zNotice += sd * cv.z; break;
                        default: DSTChat.System("特徵只有 size、speed、notice"); return;
                    }
                    int score = DUST.Animals.DSTEvolution.Score(s, out _);
                    if (score >= DUST.Animals.DSTEvolution.SpeciesScore)
                    {
                        string old = s.name;
                        DUST.Animals.DSTPopulation.Speciate(r, s);
                        DSTChat.System($"{old} 分化成新物種：{s.name}（離開再回來，新出現的群就是新物種）");
                    }
                    else DSTChat.System($"{s.name} 分化分數 {score}/{DUST.Animals.DSTEvolution.SpeciesScore}");
                    return;
                }

                case "history":
                {
                    if (!HostOnly("事件紀錄存在房主那邊")) return;
                    int n = 10;
                    if (args.Length > 1 && (!int.TryParse(args[1], out n) || n < 1)) { DSTChat.System("用法：/history [筆數]"); return; }
                    var list = DSTSave.Recent(Mathf.Min(n, 50));
                    if (list.Count == 0) { DSTChat.System("還沒有事件"); return; }
                    foreach (var e in list) DSTChat.System(DSTSave.Describe(e));
                    return;
                }

                case "save":
                    if (!HostOnly("只有房主存檔")) return;
                    DSTSave.SaveAll();
                    DSTChat.System("已存檔：" + DSTSave.Dir);
                    return;
                case "species":
                    var roster = DUST.Animals.DSTEcology.At(me.transform.position);
                    float hour = DUST.Animals.DSTEcology.Hour;
                    DSTChat.System($"{roster.region} 的物種：");
                    for (int i = 0; i < roster.keys.Length; i++)
                    {
                        var g = DUST.Animals.DSTGenome.Get(roster.keys[i]);
                        bool awake = DUST.Animals.DSTEcology.ActiveAt(g.activity, hour);
                        int alive = DUST.Animals.DSTProcSpawner.Instance ? DUST.Animals.DSTProcSpawner.Instance.Alive(g.key) : 0;
                        string herd = g.group.y > 2 ? $"群居 {g.group.x}-{g.group.y} 隻" : "獨居";
                        DSTChat.System($"{i + 1}. {g.name}（{g.scientificName}）{(g.Carnivore ? (DUST.Animals.DSTGenome.CanEat(g, g.Mass, -1, DSTNetPlayer.BodyMass) ? "肉食（會獵玩家）" : "肉食") : "草食")} {g.Mass:0} kg、{DUST.Animals.DSTEcology.ActivityName(g.activity)}、{DUST.Animals.DSTEcology.TerrainName(g.terrain)}、{herd}　{(awake ? "活動中" : "休息中")}" +
                                       (NetworkManager.Singleton && NetworkManager.Singleton.IsServer ? $"　附近 {alive} 隻" : ""));
                    }
                    return;
            }

            if (!DSTNetPlayer.CheatsAllowed) { DSTChat.System("作弊指令沒有開放（房主可以輸入 /cheats on）"); return; }

            switch (cmd)
            {
                case "cheats":
                    if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsServer) { DSTChat.System("只有房主可以切換"); return; }
                    bool allow = args.Length < 2 || args[1] == "on" || args[1] == "1";
                    DSTNetPlayer.SetCheatsForAll(allow);
                    DSTChat.System(allow ? "已開放作弊指令給所有玩家" : "已關閉其他玩家的作弊指令");
                    return;

                case "creative": case "gm":
                    pc.Creative = Toggle(args, pc.Creative);
                    if (!pc.Creative && pc.Flying) pc.SetFlying(false);
                    DSTChat.System(pc.Creative ? "創造模式：連按兩下 Space 飛行" : "創造模式關閉");
                    return;
                case "survival":
                    pc.Creative = false;
                    if (pc.Flying) pc.SetFlying(false);
                    DSTChat.System("創造模式關閉");
                    return;

                case "fly":
                    if (!OnFoot(me)) return;
                    pc.SetFlying(Toggle(args, pc.Flying));
                    DSTChat.System(pc.Flying ? "飛行：Space 上升、Ctrl 下降、Shift 加速" : "停止飛行");
                    return;
                case "noclip":
                    if (!OnFoot(me)) return;
                    pc.SetNoClip(Toggle(args, pc.NoClip));
                    DSTChat.System(pc.NoClip ? "穿牆飛行" : "穿牆關閉（仍在飛行，/fly 落地）");
                    return;

                case "speed":
                    float mult = 1f;
                    if (args.Length >= 2 && !TryFloat(args[1], out mult)) { DSTChat.System("用法：/speed 2"); return; }
                    pc.SpeedMultiplier = Mathf.Clamp(mult, 0.1f, 10f);
                    DSTChat.System($"移動速度 ×{pc.SpeedMultiplier:0.##}");
                    return;

                case "tp":
                    if (!OnFoot(me)) return;
                    Teleport(me, args);
                    return;

                case "summon":
                    if (args.Length < 2 || !int.TryParse(args[1], out int slot)) { DSTChat.System("用法：/summon 1（編號看 /species）"); return; }
                    me.RequestSummon(slot - 1);
                    return;

                case "time":
                    if (args.Length < 2 || !TryHour(args[1], out float hour)) { DSTChat.System("用法：/time 6、/time 18:30、/time night"); return; }
                    me.RequestSetHour(hour);
                    return;

                case "wait":
                    if (args.Length < 2 || !TryFloat(args[1], out float wait) || wait <= 0f) { DSTChat.System("用法：/wait 3（往前 3 小時）、/wait 24（明天同一時間）"); return; }
                    me.RequestAdvance(wait);
                    return;

                case "events":
                {
                    // 發條沙漠：今天和接下來幾天的事件表
                    double t = DSTClockwork.Now;
                    int today = DSTClockwork.DayOf(t);
                    DSTChat.System($"現在：第 {today + 1} 天 {Clock(t)}");
                    for (int d = today; d < today + 4; d++)
                    {
                        var parts = new System.Collections.Generic.List<string>();
                        // 事件都是區域性的：只列這附近的
                        var me2 = DSTClockwork.Flat(me.transform.position);
                        int scx = DSTClockwork.Cell(me2.x, DSTClockwork.StormCell), scz = DSTClockwork.Cell(me2.y, DSTClockwork.StormCell);
                        for (int x = scx - 3; x <= scx + 3; x++)
                        for (int z = scz - 3; z <= scz + 3; z++)
                        {
                            if (!DSTClockwork.StormIn(d, x, z, out var s)) continue;
                            if (DSTClockwork.Pass(s, me2, out double tin, out double tout))
                                parts.Add($"沙暴經過這裡 {Clock(tin)}~{Clock(tout)}（從{DSTClockwork.Compass(s.CenterAt(tin - 0.5) - me2)}方來）");
                            else
                            {
                                // 沒經過，但離得不遠：遠遠看得到
                                float closest = float.MaxValue; double when = s.start;
                                for (double tt = s.start; tt <= s.End; tt += 0.25)
                                {
                                    float dd = (s.CenterAt(tt) - me2).magnitude - s.radius;
                                    if (dd < closest) { closest = dd; when = tt; }
                                }
                                if (closest < 2500f) parts.Add($"沙暴 {Clock(when)} 在{DSTClockwork.Compass(s.CenterAt(when) - me2)}方 {DSTClockwork.Distance(closest)}外掠過");
                            }
                        }
                        int acx = DSTClockwork.Cell(me2.x, DSTAnomaly.CellSize), acz = DSTClockwork.Cell(me2.y, DSTAnomaly.CellSize);
                        for (int x = acx - 1; x <= acx + 1; x++)
                        for (int z = acz - 1; z <= acz + 1; z++)
                        {
                            if (!DSTAnomaly.On(d, x, z, out var an)) continue;
                            var to = an.center - me2;
                            float edge = to.magnitude - an.radius;
                            if (edge > 5000f) continue;
                            string where = edge <= 0f ? "就在這裡" : $"{DSTClockwork.Compass(to)}方 {DSTClockwork.Distance(edge)}";
                            parts.Add($"異象{an.FullName} {Clock(an.StartAt)}~{Clock(an.EndAt)}（{where}）");
                        }
                        if (DSTClockwork.ShowerNight(d))
                        {
                            var met = new System.Collections.Generic.List<(double, Vector2)>();
                            DSTClockwork.MeteoritesNear(d, me2, met);
                            var falls = new System.Collections.Generic.List<string>();
                            foreach (var (land, w) in met)
                                if ((w - me2).magnitude < 3000f) falls.Add($"{Clock(land)} {DSTClockwork.Compass(w - me2)}方 {DSTClockwork.Distance((w - me2).magnitude)}");
                            parts.Add("流星雨 21:00~03:00" + (falls.Count > 0 ? "，隕石：" + string.Join("、", falls) : ""));
                        }
                        if (DUST.Animals.DSTMigration.Available)
                        {
                            var bands = new System.Collections.Generic.List<DUST.Animals.DSTBand>();
                            DUST.Animals.DSTMigration.Near(me.transform.position, 300f, bands);
                            var names = new System.Collections.Generic.HashSet<string>();
                            foreach (var b in bands) if (b.IsMassDay(d)) names.Add(b.genome.name);
                            if (names.Count > 0) parts.Add("集體遷徙：" + string.Join("、", names));
                        }
                        DSTChat.System($"第 {d + 1} 天：" + (parts.Count > 0 ? string.Join("；", parts) : "平靜"));
                    }
                    return;
                }

                case "ssf":
                    DUST.Animals.DSTStepDebug.Enabled = Toggle(args, DUST.Animals.DSTStepDebug.Enabled);
                    DSTChat.System(DUST.Animals.DSTStepDebug.Enabled
                        ? "步伐選擇除錯：開（點越大越紅 = 權重越高，白線 = 抽中的一步；右下角 = 玩家指數 W）" + (NetworkManager.Singleton && NetworkManager.Singleton.IsListening && !NetworkManager.Singleton.IsServer ? "　※動物 AI 在房主那邊跑，客戶端看不到" : "")
                        : "步伐選擇除錯：關");
                    return;

                case "hunger":
                {
                    if (NetworkManager.Singleton && NetworkManager.Singleton.IsListening && !NetworkManager.Singleton.IsServer) { DSTChat.System("動物 AI 在房主那邊跑，只有房主能用"); return; }
                    float e = 0.1f;
                    if (args.Length > 1 && !TryFloat(args[1], out e)) { DSTChat.System("用法：/hunger [0-1]"); return; }
                    int count = 0;
                    foreach (var a in DUST.Animals.DSTAnimal.All)
                        if (a && a.IsAlive && (a.transform.position - me.transform.position).sqrMagnitude < 3600f) { a.SetEnergy(e); count++; }
                    DSTChat.System($"附近 {count} 隻動物的能量設成 {Mathf.Clamp01(e):0.00}");
                    return;
                }

                case "herds":
                    if (!DUST.Animals.DSTMigration.Available) { DSTChat.System("只有隨機世界有遷徙"); return; }
                    var herds = new System.Collections.Generic.List<DUST.Animals.DSTBand>();
                    var here = me.transform.position;
                    DUST.Animals.DSTProcSpawner.Herds(here, 300f, herds);
                    double now = DUST.Animals.DSTMigration.Now;
                    DSTChat.System($"附近的動物群（{herds.Count}）：");
                    for (int i = 0; i < Mathf.Min(6, herds.Count); i++)
                    {
                        var b = herds[i];
                        var at = b.MemberAt(now, 0, out bool moving) - here;
                        string dirName = Compass(Mathf.Atan2(at.x, at.z) * Mathf.Rad2Deg);
                        float next = b.NextDeparture(now);
                        DSTChat.System($"{b.genome.name} ×{b.count}　{dirName} {new Vector2(at.x, at.z).magnitude:0} m　" +
                                       (moving ? "遷徙中" : $"在窩裡，{Mathf.FloorToInt(next):00}:{Mathf.FloorToInt(next % 1f * 60f):00} 出門") + $"　路線 {b.length:0} m");
                    }
                    return;
            }
            DSTChat.System($"沒有這個指令：/{cmd}（/help 看全部）");
        }

        static string Clock(double t)
        {
            double h = t - System.Math.Floor(t / 24.0) * 24.0;
            int hh = (int)h, mm = (int)((h - hh) * 60.0);
            return $"{hh:00}:{mm:00}";
        }

        static string Compass(float deg)
        {
            string[] n = { "北", "東北", "東", "東南", "南", "西南", "西", "西北" };
            return n[Mathf.RoundToInt(Mathf.Repeat(deg, 360f) / 45f) % 8];
        }

        static bool Toggle(string[] args, bool current)
        {
            if (args.Length < 2) return !current;
            var a = args[1].ToLowerInvariant();
            return a == "on" || a == "1" || a == "true";
        }

        static bool OnFoot(DSTNetPlayer me)
        {
            if (!me.InVehicle && me.Controller.enabled) return true;
            DSTChat.System("先下車再用");
            return false;
        }

        static string Pct(float logOffset) => $"{(Mathf.Exp(logOffset) - 1f) * 100f:+0.0;-0.0;0}%";

        // 存檔、族群只在房主那邊；隨機世界才有
        static bool HostOnly(string why)
        {
            if (NetworkManager.Singleton && NetworkManager.Singleton.IsListening && !NetworkManager.Singleton.IsServer) { DSTChat.System(why + "，只有房主能用"); return false; }
            if (!DSTSave.Active) { DSTChat.System("只有隨機世界有存檔"); return false; }
            return true;
        }

        static bool TryFloat(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        static bool TryHour(string s, out float hour)
        {
            switch (s.ToLowerInvariant())
            {
                case "day": case "morning": hour = 8f; return true;
                case "noon": hour = 12f; return true;
                case "evening": case "sunset": hour = 19f; return true;
                case "night": hour = 22f; return true;
                case "midnight": hour = 0f; return true;
            }
            var hm = s.Split(':');
            if (hm.Length == 2 && int.TryParse(hm[0], out int h) && int.TryParse(hm[1], out int m)) { hour = h + m / 60f; return h >= 0 && h <= 24 && m >= 0 && m < 60; }
            return TryFloat(s, out hour) && hour >= 0f && hour <= 24f;
        }

        static void Teleport(DSTNetPlayer me, string[] args)
        {
            var pc = me.Controller;
            Vector3 target;
            float yaw = me.transform.eulerAngles.y;
            if (args.Length == 2 && int.TryParse(args[1], out int n))
            {
                // 傳送到某個玩家旁邊（玩家編號 = 聊天裡顯示的「玩家 N」）
                DSTNetPlayer other = null;
                foreach (var p in DSTNetPlayer.All) if (p && p.OwnerClientId + 1 == (ulong)n) other = p;
                if (!other || other == me) { DSTChat.System($"找不到玩家 {n}"); return; }
                target = other.transform.position - other.transform.forward * 1.2f + Vector3.up * 0.3f;
                yaw = other.transform.eulerAngles.y;
            }
            else if (args.Length == 3 && TryFloat(args[1], out float x) && TryFloat(args[2], out float z))
                target = new Vector3(x, GroundY(x, z) + 0.1f, z);
            else if (args.Length == 4 && TryFloat(args[1], out x) && TryFloat(args[2], out float y) && TryFloat(args[3], out z))
                target = new Vector3(x, y, z);
            else { DSTChat.System("用法：/tp 10 20、/tp 10 5 20、/tp 2"); return; }

            bool noclip = pc.NoClip;
            pc.Teleport(target, yaw);
            if (noclip) pc.SetNoClip(true);       // Teleport 會把碰撞打開
            DSTChat.System($"傳送到 {target.x:0.0}, {target.y:0.0}, {target.z:0.0}");
        }

        static float GroundY(float x, float z)
        {
            if (Physics.Raycast(new Vector3(x, 500f, z), Vector3.down, out var hit, 1000f, ~0, QueryTriggerInteraction.Ignore)) return hit.point.y;
            var world = DSTWorld.Instance;
            return world && world.IsRandom ? world.GroundHeight(x, z) : 0f;
        }
    }
}
