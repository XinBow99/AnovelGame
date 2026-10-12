using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DUST.Player;
using DUST.Vehicles;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.EditorTools
{
    // 玩家鬃狼：FBX 匯入設定 → 走路 Animator → 動作資料庫（player_actions.json + 片段取樣）→ 玩家 Prefab → 三台載具掛上動作系統。
    // Blender 原始檔：DUST/blender/SK_DST_Player_ManedWolf.blend（動作都是在載具座標裡做的）。可以重複執行。
    public static class DSTPlayerWolfSetup
    {
        const string Dir = "Assets/DUST/Player/ManedWolf";
        public const string Fbx = Dir + "/SK_DST_Player_ManedWolf.fbx";
        const string Json = Dir + "/player_actions.json";
        public const string ControllerPath = Dir + "/AC_PlayerWolf.controller";
        public const string LibraryPath = Dir + "/ACT_PlayerWolf.asset";
        const string MatDir = Dir + "/Materials";
        const string AnimalMatDir = "Assets/DUST/Animals/ManedWolf/Materials";

        static readonly HashSet<string> Loops = new HashSet<string>
        {
            "MW2_Idle", "MW2_Walk", "MW2_Run", "PL_Pickup_Drive", "PL_Bike_Ride", "PL_Drone_Ride",
        };

        [MenuItem("DUST/Player/Build Maned Wolf Player")]
        public static void BuildAll()
        {
            SetupImporter();
            BuildController();
            var lib = BuildLibrary();
            DSTPlayerSetup.BuildPrefab();
            DSTVehicleSetup.Build();                 // 皮卡（新的 FBX 有可以開的車門）
            SetupVehicle(DSTVehicleSetup.PrefabPath, DSTVehicleKind.Pickup, lib);
            SetupVehicle(DSTMeBikeSetup.PrefabPath, DSTVehicleKind.MeBike, lib);
            SetupVehicle(DSTDroneSetup.PrefabPath, DSTVehicleKind.Drone, lib);
            DSTNetSetup.SetupAll();
            Debug.Log($"[DUST] 鬃狼玩家完成：{lib.clips.Count} 個動作片段");
        }

        // ------------------------------------------------------------------ 匯入
        static void SetupImporter()
        {
            Directory.CreateDirectory(MatDir);
            var remap = new Dictionary<string, Material>();
            // 毛皮材質跟野生鬃狼共用
            foreach (var (src, dst) in new[]
            {
                ("MW2_Fur", "M_DST_ManedWolf_Fur"), ("MW2_Eye", "M_DST_ManedWolf_Eye"), ("MW2_Nose", "M_DST_ManedWolf_Nose"),
                ("MW2_EarInner", "M_DST_ManedWolf_EarInner"), ("MW2_EarOuter", "M_DST_ManedWolf_EarOuter"),
                ("MW2_Hair_Red", "M_DST_ManedWolf_Hair_Red"), ("MW2_Hair_Black", "M_DST_ManedWolf_Hair_Black"),
                ("MW2_Hair_White", "M_DST_ManedWolf_Hair_White"), ("MW2_Hair_Pale", "M_DST_ManedWolf_Hair_Pale"),
                ("MW2_FurShell_1", "M_DST_ManedWolf_FurShell_1"), ("MW2_FurShell_2", "M_DST_ManedWolf_FurShell_2"), ("MW2_FurShell_3", "M_DST_ManedWolf_FurShell_3"),
            })
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>($"{AnimalMatDir}/{dst}.mat");
                if (m) remap[src] = m; else Debug.LogWarning("[DUST] 缺少野生鬃狼材質 " + dst + "（先跑 DUST/Animals/Build Maned Wolf）");
            }
            remap["M_DST_Player_Scarf"] = DSTVehicleSetup.LitAt(MatDir, "M_DST_Player_Scarf", new Color(0.62f, 0.06f, 0.05f), 0f, 0.15f);
            remap["M_DST_Player_GoggleStrap"] = DSTVehicleSetup.LitAt(MatDir, "M_DST_Player_GoggleStrap", new Color(0.12f, 0.09f, 0.07f), 0f, 0.1f);
            remap["M_DST_Player_GoggleFrame"] = DSTVehicleSetup.LitAt(MatDir, "M_DST_Player_GoggleFrame", new Color(0.55f, 0.42f, 0.22f), 1f, 0.65f);
            remap["M_DST_Player_GoggleLens"] = DSTVehicleSetup.LitAt(MatDir, "M_DST_Player_GoggleLens", new Color(0.05f, 0.09f, 0.08f), 0.6f, 0.92f);

            var mi = (ModelImporter)AssetImporter.GetAtPath(Fbx);
            mi.bakeAxisConversion = true;
            mi.globalScale = 1f;
            mi.useFileScale = true;
            mi.importCameras = false;
            mi.importLights = false;
            mi.animationType = ModelImporterAnimationType.Generic;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            foreach (var kv in remap)
                mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            var clips = new List<ModelImporterClipAnimation>();
            foreach (var c in mi.defaultClipAnimations)
            {
                c.name = c.takeName.Split('|').Last();
                c.loopTime = Loops.Contains(c.name);
                c.loopPose = false;
                clips.Add(c);
            }
            mi.clipAnimations = clips.ToArray();
            mi.SaveAndReimport();
        }

        static Dictionary<string, AnimationClip> Clips() =>
            AssetDatabase.LoadAllAssetsAtPath(Fbx).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview")).ToDictionary(c => c.name);

        // ------------------------------------------------------------------ 走路
        static void BuildController()
        {
            var clips = Clips();
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) ?? AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            foreach (var p in ctrl.parameters.ToArray()) ctrl.RemoveParameter(p);
            ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
            var sm = ctrl.layers[0].stateMachine;
            foreach (var s in sm.states.ToArray()) sm.RemoveState(s.state);
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(ControllerPath).OfType<BlendTree>().ToArray()) Object.DestroyImmediate(o, true);

            var tree = new BlendTree { name = "Locomotion", blendParameter = "Speed", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(tree, ctrl);
            tree.AddChild(clips["MW2_Idle"], 0f);
            tree.AddChild(clips["MW2_Walk"], 0.9f);      // 走路片段一步約 0.9 m/s
            tree.AddChild(clips["MW2_Run"], 3.0f);       // 玩家走路（約 3 m/s）就用小跑步
            tree.AddChild(clips["MW2_Run"], 4.8f);       // 衝刺：同一個片段加快
            var ch = tree.children;
            ch[3].timeScale = 1.45f;
            tree.children = ch;
            var loco = sm.AddState("Locomotion");
            loco.motion = tree;
            sm.defaultState = loco;
            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------ 動作資料庫
        static DSTActionLibrary BuildLibrary()
        {
            var clips = Clips();
            var lib = AssetDatabase.LoadAssetAtPath<DSTActionLibrary>(LibraryPath);
            if (!lib) { lib = ScriptableObject.CreateInstance<DSTActionLibrary>(); AssetDatabase.CreateAsset(lib, LibraryPath); }
            lib.clips.Clear();
            lib.idle = clips["MW2_Idle"]; lib.walk = clips["MW2_Walk"]; lib.run = clips["MW2_Run"];
            lib.sniff = clips["MW2_Sniff"]; lib.alert = clips["MW2_Alert"];

            var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Json, Encoding.UTF8));
            var meta = (Dictionary<string, object>)root["clips"];
            float fps = System.Convert.ToSingle(root.TryGetValue("fps", out var f) ? f : 30, CultureInfo.InvariantCulture);

            // 取樣用：模型轉 180° 掛在原點的 anchor 底下（跟執行時一樣）
            var anchor = new GameObject("__anchor");
            var model = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Fbx), anchor.transform);
            model.transform.localRotation = DSTWolfRig.ModelFlip;
            var bones = model.GetComponentsInChildren<Transform>();
            var rootBone = bones.First(t => t.name == "Root");
            var hips = bones.First(t => t.name == "Hips");
            var head = bones.First(t => t.name == "Head");

            foreach (var kv in meta.OrderBy(k => k.Key))
            {
                if (!clips.TryGetValue(kv.Key, out var clip)) { Debug.LogWarning("[DUST] FBX 裡沒有 " + kv.Key); continue; }
                var m = (Dictionary<string, object>)kv.Value;
                var a = new DSTActionClip { name = kv.Key, clip = clip };
                a.vehicle = Get(m, "vehicle", "Pickup") switch { "MeBike" => DSTVehicleKind.MeBike, "Drone" => DSTVehicleKind.Drone, _ => DSTVehicleKind.Pickup };
                string kind = Get(m, "kind", "Ride");
                a.kind = kind switch
                {
                    "Enter" => DSTActionKind.Enter, "Exit" => DSTActionKind.Exit, "Fidget" => DSTActionKind.Fidget,
                    _ => m.ContainsKey("steer_deg") ? DSTActionKind.Steer : DSTActionKind.Ride,
                };
                a.weight = Num(m, "weight", 1f);
                a.steerDeg = Num(m, "steer_deg", 0f);
                a.description = Get(m, "desc", "");
                if (m.TryGetValue("conditions", out var co) && co is Dictionary<string, object> cond)
                {
                    a.side = Get(cond, "side", Get(cond, "door_side", "")) switch { "L" => DSTActionSide.Left, "R" => DSTActionSide.Right, "Rear" => DSTActionSide.Rear, _ => DSTActionSide.Any };
                    a.door = Get(cond, "door_state", "") switch { "closed" => DSTDoorNeed.Closed, "open" => DSTDoorNeed.Open, _ => DSTDoorNeed.Any };
                    a.clearance = Num(cond, "clearance_m", 0.8f);
                    a.leavesDoorOpen = cond.TryGetValue("leaves_door_open", out var lo) && lo is bool b && b;
                }
                if (m.TryGetValue("door_L", out var dl) && dl is List<object> door && door.Count > 0)
                {
                    var keys = new Keyframe[door.Count];
                    for (int i = 0; i < door.Count; i++) keys[i] = new Keyframe(i / fps, System.Convert.ToSingle(door[i], CultureInfo.InvariantCulture));
                    a.doorAngle = new AnimationCurve(keys);
                    for (int i = 0; i < keys.Length; i++) a.doorAngle.SmoothTangents(i, 0f);
                    // 需要的車門狀態直接看曲線開頭：關著開始 = 門要關著、開著開始 = 門要開著
                    if (a.kind == DSTActionKind.Enter || a.kind == DSTActionKind.Exit)
                        a.door = keys[0].value < 20f ? DSTDoorNeed.Closed : keys[0].value > 40f ? DSTDoorNeed.Open : DSTDoorNeed.Any;
                    // 車門變化只看「上車前 / 下車後」：下車結束時門開著 = 會留著開的門
                    a.leavesDoorOpen = keys[keys.Length - 1].value > 30f;
                }
                if (m.TryGetValue("events", out var ev) && ev is Dictionary<string, object> events)
                    foreach (var e in events)
                        a.events.Add(new DSTActionEvent { name = e.Key, time = System.Convert.ToSingle(e.Value, CultureInfo.InvariantCulture) / fps });

                // 起點 / 終點：直接從片段取樣（Root 骨頭的位置、臀部→頭的方向）
                Sample(clip, 0f, model, rootBone, hips, head, out a.startPos, out a.startYaw);
                Sample(clip, clip.length, model, rootBone, hips, head, out a.endPos, out a.endYaw);
                lib.clips.Add(a);
            }
            Object.DestroyImmediate(anchor);
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            return lib;
        }

        static void Sample(AnimationClip clip, float t, GameObject model, Transform root, Transform hips, Transform head, out Vector3 pos, out float yaw)
        {
            clip.SampleAnimation(model, t);
            pos = root.position;
            var d = head.position - hips.position; d.y = 0f;
            yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        static string Get(Dictionary<string, object> m, string k, string def) => m.TryGetValue(k, out var v) && v != null ? v.ToString() : def;
        static float Num(Dictionary<string, object> m, string k, float def) => m.TryGetValue(k, out var v) && v != null ? System.Convert.ToSingle(v, CultureInfo.InvariantCulture) : def;

        // ------------------------------------------------------------------ 載具
        static void SetupVehicle(string path, DSTVehicleKind kind, DSTActionLibrary lib)
        {
            if (!AssetDatabase.LoadAssetAtPath<GameObject>(path)) return;
            var root = PrefabUtility.LoadPrefabContents(path);
            var act = root.TryGetComponent<DSTRideActions>(out var existing) ? existing : root.AddComponent<DSTRideActions>();
            act.library = lib;
            act.kind = kind;
            act.anchor = kind == DSTVehicleKind.MeBike ? (root.transform.Find("Visual") ?? root.transform) : root.transform;
            act.door = kind == DSTVehicleKind.Pickup ? root.transform.Find("Door_L") : null;
            if (act.door) SetupDoor(root.transform, act);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
        }

        // 車門：鉸鏈軸 = 車子的上方向（換到車門自己的座標）；開門方向 = 門的後緣往車子左側（-X）轉出去
        static void SetupDoor(Transform car, DSTRideActions act)
        {
            var door = act.door;
            act.doorAxis = Quaternion.Inverse(door.rotation) * car.up;
            var rest = door.localRotation;
            var mf = door.GetComponent<MeshFilter>();
            var c = mf ? mf.sharedMesh.bounds.center : Vector3.zero;
            float XAt(float sign)
            {
                door.localRotation = rest * Quaternion.AngleAxis(sign * 60f, act.doorAxis);
                float x = car.InverseTransformPoint(door.TransformPoint(c)).x;
                door.localRotation = rest;
                return x;
            }
            act.doorSign = XAt(1f) < XAt(-1f) ? 1f : -1f;
            Debug.Log($"[DUST] 車門軸 {act.doorAxis} 方向 {act.doorSign}");
        }

        // ------------------------------------------------------------------ 極簡 JSON（只給這支編輯器腳本用）
        static class MiniJson
        {
            public static object Parse(string s) { int i = 0; return Value(s, ref i); }

            static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

            static object Value(string s, ref int i)
            {
                Ws(s, ref i);
                char c = s[i];
                if (c == '{')
                {
                    var d = new Dictionary<string, object>(); i++;
                    while (true)
                    {
                        Ws(s, ref i);
                        if (s[i] == '}') { i++; return d; }
                        var k = Str(s, ref i); Ws(s, ref i); i++;   // ':'
                        d[k] = Value(s, ref i); Ws(s, ref i);
                        if (s[i] == ',') i++;
                    }
                }
                if (c == '[')
                {
                    var l = new List<object>(); i++;
                    while (true)
                    {
                        Ws(s, ref i);
                        if (s[i] == ']') { i++; return l; }
                        l.Add(Value(s, ref i)); Ws(s, ref i);
                        if (s[i] == ',') i++;
                    }
                }
                if (c == '"') return Str(s, ref i);
                if (s.Substring(i).StartsWith("true")) { i += 4; return true; }
                if (s.Substring(i).StartsWith("false")) { i += 5; return false; }
                if (s.Substring(i).StartsWith("null")) { i += 4; return null; }
                int st = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                return double.Parse(s.Substring(st, i - st), CultureInfo.InvariantCulture);
            }

            static string Str(string s, ref int i)
            {
                var sb = new StringBuilder(); i++;
                while (s[i] != '"')
                {
                    if (s[i] == '\\')
                    {
                        i++;
                        char e = s[i];
                        if (e == 'u') { sb.Append((char)System.Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                        else sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
                    }
                    else sb.Append(s[i]);
                    i++;
                }
                i++;
                return sb.ToString();
            }
        }
    }
}
