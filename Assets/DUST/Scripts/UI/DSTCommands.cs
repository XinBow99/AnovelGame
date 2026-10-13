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
            "/species　這個地區有哪些物種　　/summon <編號>　在前面生出一群　　/herds　附近動物群在哪、幾點出門　　/events　接下來幾天的沙暴、流星、集體遷徙　　/wait <小時>　時間往前跳",
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
                        DSTChat.System($"{i + 1}. {g.name}（{g.scientificName}）{DUST.Animals.DSTEcology.ActivityName(g.activity)}、{DUST.Animals.DSTEcology.TerrainName(g.terrain)}、{herd}　{(awake ? "活動中" : "休息中")}" +
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
                        if (DSTClockwork.StormOn(d, out float st, out float du)) parts.Add($"沙暴 {Clock(d * 24.0 + st)}~{Clock(d * 24.0 + st + du)}");
                        if (DSTClockwork.Meteorite(d, out double land, out var where))
                            parts.Add($"流星雨 21:00~03:00，隕石 {Clock(land)} 落在 ({where.x:0}, {where.y:0})");
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
