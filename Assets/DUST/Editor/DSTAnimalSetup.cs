using System.Collections.Generic;
using System.IO;
using System.Linq;
using DUST.Animals;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering.HighDefinition;

namespace DUST.EditorTools
{
    // 鬃狼：FBX → 材質 / Animator / Prefab / 物種資料；地圖 NavMesh；場景生成器
    public static class DSTAnimalSetup
    {
        const string Dir = "Assets/DUST/Animals/ManedWolf";
        const string Fbx = Dir + "/SK_DST_ManedWolf.fbx";
        const string ControllerPath = Dir + "/AC_ManedWolf.controller";
        const string PrefabPath = Dir + "/PF_Animal_ManedWolf.prefab";
        const string SpeciesPath = Dir + "/SPC_ManedWolf.asset";

        // 半尺寸世界（與玩家、載具相同）
        const float WalkSpeed = 0.75f;
        const float RunSpeed = 4.0f;

        static readonly Dictionary<string, string> TakeToClip = new Dictionary<string, string>
        {
            { "MW2_Idle", "Idle" }, { "MW2_Walk", "Walk" }, { "MW2_Run", "Run" }, { "MW2_Sniff", "Sniff" },
            { "MW2_Alert", "Alert" }, { "MW2_Death", "Death" },
        };

        [MenuItem("DUST/Animals/Build Maned Wolf")]
        public static DSTSpeciesDefinition BuildManedWolf()
        {
            SetupImporter();
            var controller = BuildController();
            var prefab = BuildPrefab(controller);
            var species = AssetDatabase.LoadAssetAtPath<DSTSpeciesDefinition>(SpeciesPath);
            if (species == null)
            {
                species = ScriptableObject.CreateInstance<DSTSpeciesDefinition>();
                AssetDatabase.CreateAsset(species, SpeciesPath);
            }
            species.displayName = "鬃狼";
            species.scientificName = "Chrysocyon brachyurus";
            species.prefab = prefab;
            species.maxHealth = 8f;
            species.walkSpeed = WalkSpeed;
            species.runSpeed = RunSpeed;            // 比玩家衝刺（4.8）慢一點，追得上
            species.turnSpeedDeg = 320f;
            species.acceleration = 12f;
            species.temperament = Temperament.Shy;  // 害羞、獨居，看到人先停下來觀察，太近就跑
            species.activity = Activity.Crepuscular;
            species.noticeRadius = 9f;
            species.fleeRadius = 4.5f;
            species.calmRadius = 16f;
            species.wanderRadius = 10f;
            species.fleeDistance = 14f;
            species.sniffChance = 0.4f;             // 雜食，常低頭找果實和小動物
            species.maxAlive = 4;
            species.groupSize = new Vector2Int(1, 1);
            species.minSpawnDistance = 14f;
            species.maxSpawnDistance = 40f;
            species.despawnDistance = 65f;
            EditorUtility.SetDirty(species);
            AssetDatabase.SaveAssets();
            return species;
        }

        static void SetupImporter()
        {
            var matDir = Dir + "/Materials";
            var texDir = Dir + "/Textures";
            Directory.CreateDirectory(matDir);
            SetupTexture(texDir + "/T_MW_Fur_BaseColor.png", true, false, 2048);
            SetupTexture(texDir + "/T_MW_Fur_Normal.png", false, true, 2048);
            SetupTexture(texDir + "/T_MW_HairStrands.png", true, false, 1024);
            SetupTexture(texDir + "/T_MW_FurShell.png", true, false, 4096);
            var furColor = AssetDatabase.LoadAssetAtPath<Texture2D>(texDir + "/T_MW_Fur_BaseColor.png");
            var furNormal = AssetDatabase.LoadAssetAtPath<Texture2D>(texDir + "/T_MW_Fur_Normal.png");
            var strands = AssetDatabase.LoadAssetAtPath<Texture2D>(texDir + "/T_MW_HairStrands.png");
            var shell = AssetDatabase.LoadAssetAtPath<Texture2D>(texDir + "/T_MW_FurShell.png");

            var mats = new Dictionary<string, Material>
            {
                { "MW2_Fur", Lit(matDir, "M_DST_ManedWolf_Fur", Color.white, 0.18f, furColor, furNormal) },
                { "MW2_Eye", Lit(matDir, "M_DST_ManedWolf_Eye", new Color(0.12f, 0.06f, 0.02f), 0.92f) },
                { "MW2_Nose", Lit(matDir, "M_DST_ManedWolf_Nose", new Color(0.02f, 0.02f, 0.02f), 0.55f) },
                { "MW2_EarInner", Lit(matDir, "M_DST_ManedWolf_EarInner", new Color(0.78f, 0.70f, 0.60f), 0.1f) },
                { "MW2_EarOuter", Lit(matDir, "M_DST_ManedWolf_EarOuter", new Color(0.55f, 0.20f, 0.06f), 0.12f) },
                { "MW2_Hair_Red", Hair(matDir, "M_DST_ManedWolf_Hair_Red", new Color(0.82f, 0.42f, 0.16f), strands) },
                { "MW2_Hair_Black", Hair(matDir, "M_DST_ManedWolf_Hair_Black", new Color(0.08f, 0.07f, 0.065f), strands) },
                { "MW2_Hair_White", Hair(matDir, "M_DST_ManedWolf_Hair_White", new Color(1f, 0.98f, 0.93f), strands) },
                { "MW2_Hair_Pale", Hair(matDir, "M_DST_ManedWolf_Hair_Pale", new Color(0.93f, 0.78f, 0.56f), strands) },
            };
            // 殼層毛皮：越外層越稀疏（與 Blender 的 cutoff 一致：0.45 + 0.15k）
            for (int k = 1; k <= 3; k++)
                mats["MW2_FurShell_" + k] = Shell(matDir, "M_DST_ManedWolf_FurShell_" + k, shell, 0.45f + 0.15f * k);

            var mi = (ModelImporter)AssetImporter.GetAtPath(Fbx);
            mi.bakeAxisConversion = true;
            mi.globalScale = 1f;
            mi.useFileScale = true;
            mi.importCameras = false;
            mi.importLights = false;
            mi.animationType = ModelImporterAnimationType.Generic;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            foreach (var kv in mats)
                mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            var clips = new List<ModelImporterClipAnimation>();
            foreach (var c in mi.defaultClipAnimations)
            {
                var key = TakeToClip.Keys.FirstOrDefault(k => c.takeName.EndsWith(k));
                if (key == null) continue;
                c.name = TakeToClip[key];
                c.loopTime = key != "MW_Death";
                c.loopPose = false;
                clips.Add(c);
            }
            mi.clipAnimations = clips.ToArray();
            mi.SaveAndReimport();
        }

        static void SetupTexture(string path, bool srgb, bool normal, int maxSize)
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            if (ti == null) { Debug.LogWarning("[DUST] missing texture " + path); return; }
            ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            ti.sRGBTexture = srgb;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.maxTextureSize = maxSize;
            ti.mipmapEnabled = true;
            ti.mipMapsPreserveCoverage = !normal;      // 毛的 alpha 在遠處 mip 不會整片消失
            ti.alphaTestReferenceValue = 0.5f;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();
        }

        static Material Get(string dir, string name)
        {
            var path = dir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("HDRP/Lit")); AssetDatabase.CreateAsset(m, path); }
            return m;
        }

        static Material Lit(string dir, string name, Color c, float smooth, Texture2D baseMap = null, Texture2D normalMap = null)
        {
            var m = Get(dir, name);
            m.SetColor("_BaseColor", c);
            m.SetTexture("_BaseColorMap", baseMap);
            m.SetTexture("_NormalMap", normalMap);
            m.SetFloat("_NormalScale", 1.3f);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Smoothness", smooth);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        // 毛髮面片：透明裁切、雙面
        static Material Hair(string dir, string name, Color tint, Texture2D strands)
        {
            var m = Get(dir, name);
            m.SetColor("_BaseColor", tint);
            m.SetTexture("_BaseColorMap", strands);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Smoothness", 0.25f);
            m.SetFloat("_DoubleSidedEnable", 1f);
            m.SetFloat("_DoubleSidedNormalMode", 1f);   // mirror
            HDMaterial.SetAlphaClipping(m, true);
            HDMaterial.SetAlphaCutoff(m, 0.35f);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        // 殼層毛皮：RGB = 毛色、A = 毛尖密度，cutoff 決定這一層剩多少毛
        static Material Shell(string dir, string name, Texture2D shell, float cutoff)
        {
            var m = Get(dir, name);
            m.SetColor("_BaseColor", Color.white);
            m.SetTexture("_BaseColorMap", shell);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Smoothness", 0.1f);
            HDMaterial.SetAlphaClipping(m, true);
            HDMaterial.SetAlphaCutoff(m, cutoff);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static AnimationClip Clip(string name) =>
            AssetDatabase.LoadAllAssetsAtPath(Fbx).OfType<AnimationClip>().First(c => c.name == name);

        static AnimatorController BuildController()
        {
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath)
                       ?? AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            foreach (var p in ctrl.parameters.ToArray()) ctrl.RemoveParameter(p);
            ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("Sniff", AnimatorControllerParameterType.Bool);
            ctrl.AddParameter("Alert", AnimatorControllerParameterType.Bool);
            ctrl.AddParameter("Dead", AnimatorControllerParameterType.Trigger);

            var sm = ctrl.layers[0].stateMachine;
            foreach (var s in sm.states.ToArray()) sm.RemoveState(s.state);
            foreach (var t in sm.anyStateTransitions.ToArray()) sm.RemoveAnyStateTransition(t);

            var tree = new BlendTree { name = "Locomotion", blendParameter = "Speed", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(tree, ctrl);
            tree.AddChild(Clip("Idle"), 0f);
            tree.AddChild(Clip("Walk"), WalkSpeed);
            tree.AddChild(Clip("Run"), RunSpeed);
            var children = tree.children;
            children[2].timeScale = 1.4f;   // 跑步步幅較小，加快一點減少腳滑
            tree.children = children;

            var loco = sm.AddState("Locomotion"); loco.motion = tree;
            var sniff = sm.AddState("Sniff"); sniff.motion = Clip("Sniff");
            var dead = sm.AddState("Dead"); dead.motion = Clip("Death");
            var alert = sm.AddState("Alert"); alert.motion = Clip("Alert");
            sm.defaultState = loco;
            var toAlert = loco.AddTransition(alert); toAlert.hasExitTime = false; toAlert.duration = 0.25f;
            toAlert.AddCondition(AnimatorConditionMode.If, 0, "Alert");
            var fromAlert = alert.AddTransition(loco); fromAlert.hasExitTime = false; fromAlert.duration = 0.2f;
            fromAlert.AddCondition(AnimatorConditionMode.IfNot, 0, "Alert");

            var toSniff = loco.AddTransition(sniff); toSniff.hasExitTime = false; toSniff.duration = 0.3f;
            toSniff.AddCondition(AnimatorConditionMode.If, 0, "Sniff");
            var fromSniff = sniff.AddTransition(loco); fromSniff.hasExitTime = false; fromSniff.duration = 0.3f;
            fromSniff.AddCondition(AnimatorConditionMode.IfNot, 0, "Sniff");
            var toDead = sm.AddAnyStateTransition(dead); toDead.hasExitTime = false; toDead.duration = 0.15f; toDead.canTransitionToSelf = false;
            toDead.AddCondition(AnimatorConditionMode.If, 0, "Dead");
            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            return ctrl;
        }

        static GameObject BuildPrefab(AnimatorController controller)
        {
            var root = new GameObject("PF_Animal_ManedWolf");
            var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Fbx));
            model.name = "Model";
            model.transform.SetParent(root.transform, false);
            // NavMeshAgent 以 +Z 為前方：頭如果在 -Z 就把模型轉過來
            var headBone = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Head");
            if (headBone != null && root.transform.InverseTransformPoint(headBone.position).z < 0f)
                model.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var anim = model.GetComponent<Animator>() ?? model.AddComponent<Animator>();
            anim.runtimeAnimatorController = controller;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            var col = root.AddComponent<CapsuleCollider>();
            col.direction = 2;                // Z = 身體長軸
            col.center = new Vector3(0f, 0.37f, 0.0f);
            col.radius = 0.09f;
            col.height = 0.62f;
            var head = root.AddComponent<SphereCollider>();   // 頭頸另外一顆，揮拳比較好打中
            head.center = new Vector3(0f, 0.42f, 0.33f);   // 頭比舊版低、靠前（自然的低頭姿勢）
            head.radius = 0.07f;

            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = 0.15f;
            agent.height = 0.6f;
            agent.speed = WalkSpeed;
            agent.stoppingDistance = 0.15f;
            agent.autoBraking = true;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;

            root.AddComponent<DSTAnimal>();
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        [MenuItem("DUST/Animals/Bake NavMesh + Add Spawner To Open Scene")]
        public static void SetupScene()
        {
            var species = AssetDatabase.LoadAssetAtPath<DSTSpeciesDefinition>(SpeciesPath) ?? BuildManedWolf();

            var map = GameObject.Find("DR2_Map");
            var surface = map.GetComponent<NavMeshSurface>() ?? map.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.BuildNavMesh();
            var scenePath = map.scene.path;
            var navPath = Path.GetDirectoryName(scenePath).Replace('\\', '/') + "/NavMesh_DR2.asset";
            AssetDatabase.CreateAsset(surface.navMeshData, navPath);
            EditorUtility.SetDirty(surface);

            var sp = Object.FindAnyObjectByType<DSTAnimalSpawner>();
            if (sp == null) sp = new GameObject("AnimalSpawner").AddComponent<DSTAnimalSpawner>();
            if (!sp.species.Contains(species)) sp.species.Add(species);
            EditorUtility.SetDirty(sp);
            EditorSceneManager.MarkSceneDirty(map.scene);
        }
    }
}
