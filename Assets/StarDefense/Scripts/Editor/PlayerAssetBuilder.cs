using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// Turns the three exported FBX into game-ready Unity assets.
    ///
    /// Scope: import settings, shared materials, the player Animator Controller and the
    /// three prefabs. It deliberately does NOT touch gameplay scripts or the generated
    /// scene — replacing the scene's capsule player with the full-body prefab is a
    /// camera/design decision that has to be made deliberately.
    ///
    /// Run from the menu (Star Defense / Build Player Assets) or headless:
    ///   Unity.exe -batchmode -nographics -quit -projectPath &lt;proj&gt;
    ///             -executeMethod StarDefense.EditorTools.PlayerAssetBuilder.BuildPlayerAssets
    /// </summary>
    public static class PlayerAssetBuilder
    {
        private const string ArtRoot = "Assets/StarDefense/Art";
        private const string MatDir = ArtRoot + "/Materials";
        private const string AnimDir = ArtRoot + "/Animations/Characters";
        private const string PrefabDir = ArtRoot + "/Prefabs";

        private const string PlayerFbx = ArtRoot + "/Models/Characters/Player/CHR_Player_Astronaut_A_v1.fbx";
        private const string PickaxeFbx = ArtRoot + "/Models/Equipment/WPN_Pickaxe_A_v1.fbx";
        private const string GunFbx = ArtRoot + "/Models/Equipment/WPN_Gun_A_v1.fbx";

        private const string ControllerPath = AnimDir + "/CTRL_Player_Astronaut_A.controller";
        private const string PlayerPrefabPath = PrefabDir + "/PFB_Player_Astronaut_A.prefab";
        private const string GunPrefabPath = PrefabDir + "/PFB_WPN_Gun_A.prefab";
        private const string PickaxePrefabPath = PrefabDir + "/PFB_WPN_Pickaxe_A.prefab";

        private const string HandSocket = "Socket_RightHandTool";
        private const string MuzzleSocket = "Socket_Muzzle";
        private const string MineHitSocket = "Socket_MineHit";

        // ------------------------------------------------------------------ //
        // authoring data
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Clip id -> loop flag. Frame ranges come from the FBX takes themselves.
        ///
        /// This list drives **two** things, and they do not want the same set:
        ///   · the import settings (which clips loop) — wants every clip;
        ///   · the state machine (one state per entry) — must NOT see the Gun*
        ///     clips, because those are inputs to a blend tree, not states.
        /// <see cref="GunBlendPairs"/> picks them out for the second use.
        /// </summary>
        private static readonly (string Id, bool Loop)[] PlayerClips =
        {
            ("ANM_Player_Idle", true),
            ("ANM_Player_GunIdle", true),
            ("ANM_Player_Walk", true),
            ("ANM_Player_GunWalk", true),
            ("ANM_Player_Run", true),
            ("ANM_Player_GunRun", true),
            ("ANM_Player_JumpStart", false),
            ("ANM_Player_JumpLoop", true),
            ("ANM_Player_JumpLand", false),
            ("ANM_Player_Shoot", false),
            ("ANM_Player_Mine_Start", false),
            ("ANM_Player_Mine_Hit", false),
            ("ANM_Player_Mine_End", false),
        };

        /// <summary>
        /// 地面状态（left）与它的举枪版本（right）。
        ///
        /// 持枪与否不该把地面状态翻倍成六个："枪一直举着"是一个**姿态**问题，
        /// 不是**拓扑**问题。所以这里用 1D 混合树，由 <c>HoldingGun</c> 当权重，
        /// 把两套姿态混在同三个状态里。好处不只是省状态：
        ///
        ///   · 转移数与原来一模一样，AnyState 的来源分析、`check_anim_graph.py`
        ///     的图模拟都不用改；
        ///   · 权重**没有条件语义** —— HoldingGun 没人写时退化成 0（枪垂着，
        ///     少做一件事），而不会像布尔条件那样让某条转移恒真。
        ///
        /// 两半的**帧数必须相同**（Idle 48 / Walk 32 / Run 24）：混合树按归一化
        /// 时间对齐两条输入，长度不同会让腿的相位在参数扫过时错开，走路一顿一顿。
        /// 帧数由 anim_clips.py 的 CLIPS 保证，那边的注释也写了同一条约束。
        /// </summary>
        private static readonly (string Base, string Raised)[] GunBlendPairs =
        {
            ("ANM_Player_Idle", "ANM_Player_GunIdle"),
            ("ANM_Player_Walk", "ANM_Player_GunWalk"),
            ("ANM_Player_Run", "ANM_Player_GunRun"),
        };

        /// <summary>
        /// Blender Principled base colours are linear RGB and the project is in Linear
        /// colour space, so the values transfer 1:1 into the Standard shader's _Color.
        /// Sourced from the authoring scripts (mat_out.txt / wpn_build.py PALETTE).
        /// </summary>
        private static readonly MaterialSpec[] Materials =
        {
            new MaterialSpec("MAT_Orange_Jacket", new Color(0.930f, 0.430f, 0.055f), 0.70f),
            new MaterialSpec("MAT_Gray_Mid", new Color(0.455f, 0.465f, 0.485f), 0.70f),
            new MaterialSpec("MAT_Gray_Dark", new Color(0.195f, 0.205f, 0.225f), 0.75f),
            new MaterialSpec("MAT_Orange_Accent", new Color(0.960f, 0.500f, 0.090f), 0.62f),
            new MaterialSpec("MAT_Dark_Charcoal", new Color(0.048f, 0.048f, 0.058f), 0.55f),
            new MaterialSpec("MAT_Cream_White", new Color(0.930f, 0.910f, 0.855f), 0.66f),
            new MaterialSpec("MAT_Goggle_Glass", new Color(0.100f, 0.120f, 0.140f), 0.25f),
            new MaterialSpec("MAT_Olive_Green", new Color(0.325f, 0.375f, 0.195f), 0.80f),
            new MaterialSpec(
                "MAT_Tech_Teal",
                new Color(0.050f, 0.520f, 0.550f),
                0.30f,
                new Color(0.100f, 0.850f, 0.900f),
                2.5f
            ),
        };

        /// <summary>Simple keyword table so moving from Stand to Run reads at a glance.</summary>
        private const float WalkThreshold = 0.15f;
        private const float RunThreshold = 6.5f;

        private readonly struct MaterialSpec
        {
            public readonly string Name;
            public readonly Color BaseColor;
            public readonly float Roughness;
            public readonly Color Emission;
            public readonly float EmissionStrength;

            public MaterialSpec(
                string name,
                Color baseColor,
                float roughness,
                Color emission = default,
                float emissionStrength = 0f
            )
            {
                Name = name;
                BaseColor = baseColor;
                Roughness = roughness;
                Emission = emission;
                EmissionStrength = emissionStrength;
            }
        }

        // ------------------------------------------------------------------ //
        // entry point
        // ------------------------------------------------------------------ //

        private static readonly List<string> Log = new List<string>();

        [MenuItem("Star Defense/Build Player Assets")]
        public static void BuildPlayerAssets()
        {
            Log.Clear();
            try
            {
                EnsureFolder(MatDir);
                EnsureFolder(AnimDir);
                EnsureFolder(PrefabDir);

                ConfigureModelImporters();
                var materialMap = CreateAndRemapMaterials();
                ConfigureAnimatorController();

                var gunPrefab = BuildWeaponPrefab(GunFbx, GunPrefabPath, "PFB_WPN_Gun_A");
                var pickaxePrefab = BuildWeaponPrefab(PickaxeFbx, PickaxePrefabPath, "PFB_WPN_Pickaxe_A");
                BuildPlayerPrefab(gunPrefab, pickaxePrefab);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                FlushLog("BUILD OK");
            }
            catch (Exception e)
            {
                FlushLog("BUILD FAILED");
                Debug.LogError("[PlayerAssetBuilder] " + e);
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
                throw;
            }
        }

        private static void FlushLog(string headline)
        {
            Debug.Log("[PlayerAssetBuilder] ===== " + headline + " =====");
            foreach (var line in Log)
                Debug.Log("[PlayerAssetBuilder] " + line);
        }

        private static void Note(string line)
        {
            Log.Add(line);
        }

        // ------------------------------------------------------------------ //
        // 1) model import settings
        // ------------------------------------------------------------------ //

        private static void ConfigureModelImporters()
        {
            // --- weapons: static meshes, no animation, no colliders ------------
            foreach (var path in new[] { GunFbx, PickaxeFbx })
            {
                var imp = Importer(path);

                imp.animationType = ModelImporterAnimationType.None;
                imp.importAnimation = false;
                imp.importBlendShapes = false;
                imp.importCameras = false;
                imp.importLights = false;
                imp.addCollider = false;
                imp.isReadable = false;
                imp.meshCompression = ModelImporterMeshCompression.Off;
                imp.weldVertices = false; // keep authored hard edges on the low-poly silhouettes
                imp.importNormals = ModelImporterNormals.Import;
                imp.SaveAndReimport();
                Note("weapon import configured: " + path);
            }

            // --- player: Generic rig + clips split straight off the FBX takes --
            var player = Importer(PlayerFbx);
            player.animationType = ModelImporterAnimationType.Generic;
            player.importAnimation = true;
            player.importBlendShapes = false;
            player.importCameras = false;
            player.importLights = false;
            player.addCollider = false;
            player.isReadable = false;
            player.meshCompression = ModelImporterMeshCompression.Off;
            player.weldVertices = false;
            player.animationCompression = ModelImporterAnimationCompression.Off;
            player.importAnimatedCustomProperties = false;
            player.resampleCurves = true;
            player.optimizeGameObjects = false; // sockets must stay as real transforms
            player.SaveAndReimport();

            // SaveAndReimport invalidates the importer wrapper, so re-fetch before reading.
            player = Importer(PlayerFbx);

            // Discover what takes the FBX actually produced before naming anything.
            var takes = player.defaultClipAnimations;
            Note("player takes discovered: " + takes.Length);
            foreach (var t in takes)
                Note($"  take '{t.takeName}' frames {t.firstFrame}..{t.lastFrame}");

            var defs = new List<ModelImporterClipAnimation>();
            var unmatched = new List<string>();

            foreach (var (id, loop) in PlayerClips)
            {
                var take = takes.FirstOrDefault(t => ExtractClipId(t.takeName) == id);
                if (take == null)
                {
                    unmatched.Add(id);
                    continue;
                }

                defs.Add(
                    new ModelImporterClipAnimation
                    {
                        name = id,
                        takeName = take.takeName,
                        firstFrame = take.firstFrame,
                        lastFrame = take.lastFrame,
                        loopTime = loop,
                        loopPose = loop,
                    }
                );
            }

            if (unmatched.Count > 0)
                throw new InvalidOperationException(
                    "FBX is missing expected takes: " + string.Join(", ", unmatched)
                );

            // Only keep the takes we recognise as named clips.
            player.clipAnimations = defs.ToArray();
            player.SaveAndReimport();

            var imported = AssetDatabase
                .LoadAllAssetsAtPath(PlayerFbx)
                .OfType<AnimationClip>()
                .OrderBy(c => c.name)
                .ToList();
            Note("clips after split: " + imported.Count);
            foreach (var c in imported)
                Note(
                    $"  clip '{c.name}' length {c.length:F3}s loop {c.isLooping} "
                        + $"@ {c.frameRate}fps"
                );

            // 上面刚触发过一次重新导入，FBX 里上一批以 take 名
            // （CHR_Player_Astronaut_A|ANM_Player_Idle）命名的剪辑对象有时不会被清掉，
            // 于是这里会数出两倍数量的剪辑。这不是错误 —— 只要每个期望的 id 都能解析到
            // 一个剪辑就够了。多余的副本不被任何 Animator 引用，构建时会被剥离，不会进包。
            // 所以断言只查"该有的在不在"，不查总数相等。
            var byId = CollectClipsById();
            var missing = PlayerClips.Where(p => !byId.ContainsKey(p.Id)).Select(p => p.Id).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "导入后找不到这些剪辑: " + string.Join(", ", missing)
                );

            if (imported.Count != byId.Count)
                Note(
                    $"note: {imported.Count} 个剪辑对象归纳为 {byId.Count} 个 id，"
                        + $"多出的 {imported.Count - byId.Count} 个是上一次导入的残留，已忽略。"
                );
        }

        /// <summary>
        /// 按剪辑 id 归纳 FBX 导入出来的所有 AnimationClip。
        ///
        /// 不能拿名字直接当键：设置 clipAnimations 之后 FBX 会再导入一次，而旧的一批以
        /// take 名命名的剪辑对象有时会残留，于是同一次 LoadAllAssetsAtPath 会返回两套
        /// 名字 —— <c>ANM_Player_Idle</c> 和 <c>CHR_Player_Astronaut_A|ANM_Player_Idle</c>。
        /// 两者都含同一个 id，所以统一用 ExtractClipId 解析，并优先采用名字正好等于 id
        /// 的那一个（即 clipAnimations 定义的正式名）。
        ///
        /// 验证与 Animator 装配共用本方法，避免两处对"哪个剪辑算数"给出不同答案。
        /// </summary>
        private static Dictionary<string, AnimationClip> CollectClipsById()
        {
            var map = new Dictionary<string, AnimationClip>();

            foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(PlayerFbx).OfType<AnimationClip>())
            {
                var id = ExtractClipId(clip.name);
                if (id == null)
                    continue;

                if (!map.TryGetValue(id, out var existing))
                {
                    map[id] = clip;
                    continue;
                }

                // 正式名优先；两个都不是正式名时保留先到的那个，保证结果可复现。
                if (clip.name == id && existing.name != id)
                    map[id] = clip;
            }

            return map;
        }

        private static ModelImporter Importer(string path)
        {
            var imp = (ModelImporter)AssetImporter.GetAtPath(path);
            if (imp == null)
                throw new InvalidOperationException("No ModelImporter at " + path);
            return imp;
        }

        /// <summary>
        /// Take 名形如 "CHR_Player_Astronaut_A|ANM_Player_Idle"，把剪辑 id 抠出来。
        /// internal 是为了让 PlayerAssetVerifier 用同一套解析，两处口径一致。
        /// </summary>
        internal static string ExtractClipId(string takeName)
        {
            if (string.IsNullOrEmpty(takeName))
                return null;
            var m = Regex.Match(takeName, @"ANM_Player_[A-Za-z_]+");
            return m.Success ? m.Value : null;
        }

        // ------------------------------------------------------------------ //
        // 2) materials
        // ------------------------------------------------------------------ //

        private static Dictionary<string, Material> CreateAndRemapMaterials()
        {
            var map = new Dictionary<string, Material>();

            foreach (var spec in Materials)
            {
                var path = MatDir + "/" + spec.Name + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(Shader.Find("Standard"));
                    AssetDatabase.CreateAsset(mat, path);
                }

                mat.shader = Shader.Find("Standard");
                mat.color = spec.BaseColor; // Standard _Color in a Linear project == Blender linear RGB
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Glossiness", 1f - spec.Roughness);

                if (spec.EmissionStrength > 0f)
                {
                    mat.EnableKeyword("_EMISSION");
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    mat.SetColor("_EmissionColor", spec.Emission * spec.EmissionStrength);
                }
                else
                {
                    mat.DisableKeyword("_EMISSION");
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                    mat.SetColor("_EmissionColor", Color.black);
                }

                EditorUtility.SetDirty(mat);
                map[spec.Name] = mat;
            }

            AssetDatabase.SaveAssets();
            Note("materials written: " + map.Count);

            foreach (var fbx in new[] { PlayerFbx, GunFbx, PickaxeFbx })
                RemapFbxMaterials(fbx, map);

            return map;
        }

        private static void RemapFbxMaterials(string fbxPath, Dictionary<string, Material> map)
        {
            var imp = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            if (imp == null)
                return;

            // Keep materials embedded in the imported prefab so the project does not
            // accumulate a duplicate "Materials" folder next to every FBX.
            imp.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            imp.materialLocation = ModelImporterMaterialLocation.InPrefab;
            imp.materialName = ModelImporterMaterialName.BasedOnMaterialName;
            imp.materialSearch = ModelImporterMaterialSearch.Local;

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (model == null)
                throw new InvalidOperationException("FBX did not import a GameObject: " + fbxPath);

            var sourceNames = model
                .GetComponentsInChildren<Renderer>(true)
                .SelectMany(r => r.sharedMaterials)
                .Where(m => m != null)
                .Select(m => Regex.Replace(m.name, @"\.\d+$", ""))
                .Distinct()
                .ToList();

            foreach (var name in sourceNames)
            {
                if (!map.TryGetValue(name, out var target))
                {
                    Note($"  !! unmapped material on {fbxPath}: {name}");
                    continue;
                }

                imp.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), name), target);
            }

            imp.SaveAndReimport();
            Note($"remapped {sourceNames.Count} material slot(s) on {fbxPath}: {string.Join(", ", sourceNames)}");
        }

        // ------------------------------------------------------------------ //
        // 3) animator controller
        // ------------------------------------------------------------------ //

        private static void ConfigureAnimatorController()
        {
            // Rebuild from scratch so repeated runs stay deterministic.
            AssetDatabase.DeleteAsset(ControllerPath);

            // 参数名取自 AnimatorParameters —— 运行时脚本引用的是同一张表。
            // 这里各写一份字面量的话，改名时两边会静默错开：控制器在等条件成立，
            // 脚本在往一个不存在的参数里写，唯一的痕迹是 Console 里一句警告。
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            ctrl.AddParameter(AnimatorParameters.SpeedName, AnimatorControllerParameterType.Float);
            ctrl.AddParameter(AnimatorParameters.AirborneName, AnimatorControllerParameterType.Bool);
            ctrl.AddParameter(AnimatorParameters.AttackName, AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter(AnimatorParameters.SpecialName, AnimatorControllerParameterType.Trigger);
            // Float 而不是 Bool：它的用途是 1D 混合树的权重（0 = 镐，1 = 枪），
            // 而混合树只接受 Float 参数。肯定式的名字同样是为了"忘了接线"的安全
            // 退化 —— 没人写时取 0，表现是枪垂着（少做一件事），不是拿着镐却举着枪。
            ctrl.AddParameter(AnimatorParameters.HoldingGunName, AnimatorControllerParameterType.Float);

            var sm = ctrl.layers[0].stateMachine;
            // 与 ConfigureModelImporters 的验证共用同一套解析，两边不会对"哪个剪辑算数"
            // 得出不同结论（FBX 重新导入后可能同时存在正式名与 take 名两套剪辑）。
            var clips = CollectClipsById();

            var states = new Dictionary<string, AnimatorState>();
            var x = 0;
            foreach (var (id, loop) in PlayerClips)
            {
                // 举枪版剪辑是混合树的输入，不是状态 —— 给它们各建一个状态会让
                // 控制器里多出三个永远不会被进入的孤儿状态，而"多出来的状态"在
                // Inspector 里看起来完全正常。
                if (IsGunBlendInput(id))
                    continue;

                if (!clips.TryGetValue(id, out var clip))
                    throw new InvalidOperationException("Clip missing for state: " + id);

                var st = sm.AddState(id, new Vector3(280f * (x % 4), 80f * (x / 4), 0f));
                st.motion = ClipOrGunBlend(ctrl, clips, id, clip);
                st.writeDefaultValues = false;
                // The clip's own loop flag is authoritative; do not re-time it here.
                st.speed = 1f;
                states[id] = st;
                x++;
                _ = loop;
            }

            AnimatorState S(string id) => states[id];

            var idle = S("ANM_Player_Idle");
            var walk = S("ANM_Player_Walk");
            var run = S("ANM_Player_Run");
            var jumpStart = S("ANM_Player_JumpStart");
            var jumpLoop = S("ANM_Player_JumpLoop");
            var jumpLand = S("ANM_Player_JumpLand");
            var shoot = S("ANM_Player_Shoot");
            var mineStart = S("ANM_Player_Mine_Start");
            var mineHit = S("ANM_Player_Mine_Hit");
            var mineEnd = S("ANM_Player_Mine_End");

            // 三个"站在地上"的状态。跳跃和两个动作都只从它们出发 —— 这条约束是
            // 整张图可终止的保证，下面两段会分别说明它挡掉了哪两类故障。
            var grounded = new[] { idle, walk, run };

            // --- 站立 / 行走 / 奔跑：只由 Speed 驱动 -----------------------------
            Link(idle, walk, 0.15f, AnimatorConditionMode.Greater, WalkThreshold, AnimatorParameters.SpeedName);
            Link(walk, run, 0.15f, AnimatorConditionMode.Greater, RunThreshold, AnimatorParameters.SpeedName);
            Link(run, walk, 0.15f, AnimatorConditionMode.Less, RunThreshold, AnimatorParameters.SpeedName);
            Link(walk, idle, 0.15f, AnimatorConditionMode.Less, WalkThreshold, AnimatorParameters.SpeedName);

            // --- 跳跃链 ---------------------------------------------------------
            //
            // 这里**必须**从三个地面状态出发，不能挂 AnyState。曾经的写法是
            //     AddAnyStateTransition(JumpStart) + [Grounded == false]
            // 而 Grounded 没有任何脚本写它，永远停在 Unity 的初值 false 上，于是：
            //
            //   1. 条件恒真 → 进场景第一帧就 Idle → JumpStart；
            //   2. JumpLoop 唯一的出口也是 Grounded（这次要它 == true），同样永远不成立；
            //   3. AnyState 只要求"当前不在目标状态"，所以在 JumpLoop 里它**依然成立** ——
            //      canTransitionToSelf=false 只挡 JumpStart→JumpStart，挡不住 JumpLoop→JumpStart。
            //
            // 三者合起来就是 JumpStart → JumpLoop → JumpStart → … 的无限跳跃，
            // 而且编辑器里怎么看都正常（图是对的，缺的只是没人写那个参数）。
            //
            // 改成从地面状态出发后，跳跃链里的任何状态都不再是指向跳跃链的转移的源，
            // 环就从结构上不存在了 —— 就算将来又忘了写参数，最坏结果也只是不播跳跃。
            foreach (var ground in grounded)
                Link(ground, jumpStart, 0.08f, AnimatorConditionMode.If, 0f, AnimatorParameters.AirborneName);

            LinkOnExit(jumpStart, jumpLoop, 0.02f);
            Link(jumpLoop, jumpLand, 0.08f, AnimatorConditionMode.IfNot, 0f, AnimatorParameters.AirborneName);
            LinkOnExit(jumpLand, idle, 0.05f);

            // --- 开火 / 挥镐：同样只从地面状态出发 -------------------------------
            //
            // 顺带解决了"链式动作被自己截断"：挥镐是 Mine_Start → Hit → End 三拍，
            // 而游戏侧的挥击间隔只有 0.32 s，短于整条链。挂 AnyState 的话，链条跑到
            // Mine_Hit 时 AnyState 又会把它拽回 Mine_Start，看起来是原地抽搐。
            // 从地面状态出发就没这个问题 —— 链条本身不是地面状态，这些转移在链条
            // 运行期间根本不会被求值。
            foreach (var ground in grounded)
            {
                Link(ground, shoot, 0.05f, AnimatorConditionMode.If, 0f, AnimatorParameters.AttackName);
                Link(ground, mineStart, 0.05f, AnimatorConditionMode.If, 0f, AnimatorParameters.SpecialName);
            }

            LinkOnExit(shoot, idle, 0.02f);
            LinkOnExit(mineStart, mineHit, 0.02f);
            LinkOnExit(mineHit, mineEnd, 0.02f);
            LinkOnExit(mineEnd, idle, 0.02f);

            sm.defaultState = idle;

            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            Note("animator controller written: " + ControllerPath + " —— " + ControllerSummary());
        }

        /// <summary>该剪辑是否只作为混合树的输入（因而不该有自己的状态）。</summary>
        private static bool IsGunBlendInput(string clipId)
        {
            for (int i = 0; i < GunBlendPairs.Length; i++)
            {
                if (GunBlendPairs[i].Raised == clipId)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 地面状态该用的 motion：原本是 <paramref name="clip"/>，若这个状态在
        /// <see cref="GunBlendPairs"/> 里，就换成一个由 <c>HoldingGun</c> 驱动的 1D 混合树。
        /// </summary>
        private static Motion ClipOrGunBlend(
            AnimatorController ctrl,
            Dictionary<string, AnimationClip> clips,
            string id,
            AnimationClip clip
        )
        {
            for (int i = 0; i < GunBlendPairs.Length; i++)
            {
                if (GunBlendPairs[i].Base != id)
                    continue;

                string raisedId = GunBlendPairs[i].Raised;
                if (!clips.TryGetValue(raisedId, out var raised))
                {
                    throw new InvalidOperationException(
                        $"混合树缺一半：{id} 需要 {raisedId}，而 FBX 里没有这个剪辑。"
                            + "它由 scripts/anim_clips.py 的 CLIPS 生成 —— 缺了说明动画没重新导出。"
                    );
                }

                var tree = new BlendTree
                {
                    name = id + "_GunBlend",
                    blendType = BlendTreeType.Simple1D,
                    blendParameter = AnimatorParameters.HoldingGunName,
                    // 手动阈值：自动阈值会把两个子节点放到 0 和 1 以外的位置，
                    // 于是脚本写 1 也未必落在举枪那一端上。
                    useAutomaticThresholds = false,
                };
                tree.AddChild(clip, 0f);
                tree.AddChild(raised, 1f);

                // 混合树必须挂成控制器的子资产。只把它赋给 state.motion 而不 AddObjectToAsset，
                // 编辑器里当场看起来是对的，保存后就变成 missing。
                AssetDatabase.AddObjectToAsset(tree, ctrl);
                Note(
                    $"  {id}: 1D 混合树 {AnimatorParameters.HoldingGunName}"
                        + $" 0→{clip.name} / 1→{raised.name}"
                );
                return tree;
            }

            return clip;
        }

        /// <summary>
        /// 只重建动画控制器，**不碰任何 FBX**。
        ///
        /// 为什么单独开一个入口：控制器是纯代码产物（状态、连线、参数全由本文件决定），
        /// 却和 FBX 导入共用同一个"已建好"标记。改了连线而标记没变时，
        /// <see cref="StarDefensePipeline"/> 会连整个 2/5 步一起跳过，于是编辑器里
        /// 表现成"代码明明改了，角色的动作纹丝不动"，且不报任何错。
        /// 摘出来单独重刷，代价只有一次资产写入，不必为了改一条连线重导 2 万多顶点的角色。
        /// </summary>
        public static string RebuildAnimatorController()
        {
            Log.Clear();
            ConfigureAnimatorController();
            RepointControllerOnPlayerPrefab();
            AssetDatabase.SaveAssets();
            FlushLog("CONTROLLER REBUILT");
            return ControllerSummary();
        }

        /// <summary>
        /// 重建控制器走的是"删掉旧资产再建"，GUID 会变，而 Prefab 上的 Animator 是按
        /// GUID 引用控制器的。不把引用接回去，Prefab 还在、模型照样渲染，只是 Animator
        /// 指向一个已经不存在的资产 —— 表现是角色永远定格在绑定姿势上滑行，
        /// 而且 Unity 不会输出任何错误。
        /// </summary>
        private static void RepointControllerOnPlayerPrefab()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath) == null)
            {
                // 还没建过 Prefab：BuildPlayerAssets 装配时会自己接上，这里不必管。
                Note("controller repoint: 跳过（玩家 Prefab 尚未生成）");
                return;
            }

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
                throw new InvalidOperationException("控制器未生成: " + ControllerPath);

            var root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            try
            {
                var animator = root.GetComponent<Animator>();
                if (animator == null)
                    throw new InvalidOperationException("玩家 Prefab 上没有 Animator: " + PlayerPrefabPath);

                if (animator.runtimeAnimatorController == controller)
                {
                    Note("controller repoint: 无需改动（引用未失效）");
                    return;
                }

                animator.runtimeAnimatorController = controller;
                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
                Note("controller repoint: " + PlayerPrefabPath + " -> " + ControllerPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>一句话描述控制器内容，给管线日志用 —— 事后核对"到底是哪一版图生效了"。</summary>
        private static string ControllerSummary()
        {
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (ctrl == null)
                return "控制器缺失";

            var sm = ctrl.layers[0].stateMachine;
            var links = sm.anyStateTransitions.Length;
            foreach (var s in sm.states)
                links += s.state.transitions.Length;

            var parameters = string.Join("/", ctrl.parameters.Select(p => p.name));
            var fallback = sm.defaultState != null ? sm.defaultState.name : "无";
            return $"参数 {parameters}，状态 {sm.states.Length}，连线 {links}，默认 {fallback}";
        }

        private static void Link(
            AnimatorState from,
            AnimatorState to,
            float duration,
            AnimatorConditionMode mode,
            float threshold,
            string param
        )
        {
            var t = from.AddTransition(to);
            t.hasExitTime = false;
            t.duration = duration;
            t.canTransitionToSelf = false;
            t.AddCondition(mode, threshold, param);
        }

        private static void LinkOnExit(AnimatorState from, AnimatorState to, float duration)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = true;
            t.exitTime = 1f;
            t.duration = duration;
            t.canTransitionToSelf = false;
        }

        // ------------------------------------------------------------------ //
        // 4) prefabs
        // ------------------------------------------------------------------ //

        private static GameObject BuildWeaponPrefab(string fbxPath, string prefabPath, string rootName)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (model == null)
                throw new InvalidOperationException("Missing model for prefab: " + fbxPath);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            instance.name = rootName;

            // No colliders anywhere on a held weapon: the player raycasts from the
            // camera and a weapon collider would eat its own shots.
            foreach (var c in instance.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.DestroyImmediate(c);

            var sockets = FindTransforms(instance.transform)
                .Where(t => t.name.StartsWith("Socket_", StringComparison.Ordinal))
                .Select(t => t.name)
                .ToList();

            var saved = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            UnityEngine.Object.DestroyImmediate(instance);

            Note($"weapon prefab written: {prefabPath} (sockets: {string.Join(", ", sockets)})");
            return saved;
        }

        private static void BuildPlayerPrefab(GameObject gunPrefab, GameObject pickaxePrefab)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerFbx);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            instance.name = "PFB_Player_Astronaut_A";

            foreach (var c in instance.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.DestroyImmediate(c);

            // --- Animator ------------------------------------------------------
            var animator = instance.GetComponent<Animator>();
            if (animator == null)
                animator = instance.AddComponent<Animator>();

            // 骨架按 Generic 导入（见 ConfigureModelImporters 里的 animationType），
            // 而 Generic 不生成 Avatar 子资产 —— importer 的 avatarSetup 是 NoAvatar，
            // .meta 里 human / skeleton 也都是空的。Animator 并不需要它：泛型剪辑按
            // transform 路径直接绑到层级上，不做人体重定向。所以这里只"有就挂上"
            // （万一以后有人把 rig 改成 Humanoid，仍能生效），不强制非空。
            animator.avatar = AssetDatabase
                .LoadAllAssetsAtPath(PlayerFbx)
                .OfType<Avatar>()
                .FirstOrDefault();

            Note(
                animator.avatar != null
                    ? "animator avatar: " + animator.avatar.name
                    : "animator avatar: 无（Generic 骨架的预期状态，剪辑按路径绑定）"
            );

            animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (animator.runtimeAnimatorController == null)
                throw new InvalidOperationException("Animator Controller 未生成: " + ControllerPath);

            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.updateMode = AnimatorUpdateMode.Normal;

            // Generic 骨架没有 Avatar 兜底，绑定路径错了也不会报任何错，
            // 角色只会定格在绑定姿势上平移，所以把这件事查出来写进日志。
            VerifyClipBindings(instance);

            // --- weapon sockets -------------------------------------------------
            var hand = FindDeep(instance.transform, HandSocket);
            if (hand == null)
                throw new InvalidOperationException("Socket not found in player FBX: " + HandSocket);

            var gun = AttachPrefab(gunPrefab, hand, "GEO_HeldGun");
            var pickaxe = AttachPrefab(pickaxePrefab, hand, "GEO_HeldPickaxe");
            pickaxe.SetActive(false);

            var visuals = instance.AddComponent<PlayerToolVisuals>();
            visuals.weaponPivot = hand;
            visuals.gunVisual = gun;
            visuals.pickaxeVisual = pickaxe;

            if (FindDeep(instance.transform, MuzzleSocket) == null)
                Note("  !! player socket missing: " + MuzzleSocket);
            if (FindDeep(instance.transform, MineHitSocket) == null)
                Note("  note: " + MineHitSocket + " lives on the pickaxe prefab, not the player");

            PrefabUtility.SaveAsPrefabAsset(instance, PlayerPrefabPath);
            UnityEngine.Object.DestroyImmediate(instance);

            Note("player prefab written: " + PlayerPrefabPath);
        }

        private static GameObject AttachPrefab(GameObject prefab, Transform socket, string name)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = name;
            go.transform.SetParent(socket, false);
            // Unit local transform: the socket already carries the mount orientation.
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go;
        }

        // ------------------------------------------------------------------ //
        // helpers
        // ------------------------------------------------------------------ //

        private static IEnumerable<Transform> FindTransforms(Transform root)
        {
            yield return root;
            foreach (Transform child in root)
                foreach (var t in FindTransforms(child))
                    yield return t;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name)
                return root;
            foreach (Transform child in root)
            {
                var hit = FindDeep(child, name);
                if (hit != null)
                    return hit;
            }
            return null;
        }

        /// <summary>
        /// Generic 骨架没有 Avatar 兜底：剪辑里的绑定路径一旦和 Prefab 层级对不上，
        /// Unity 不抛错也不警告，角色只是定格在绑定姿势上平移。这里把"曲线能不能
        /// 落地"提前查出来写进日志。
        ///
        /// 刻意不抛异常：个别常量曲线的路径差异不该阻断整条资产生成链，而真正的
        /// 大面积失配会以"N/M 条落地"的形式在日志里一眼看出来。
        /// </summary>
        private static void VerifyClipBindings(GameObject instance)
        {
            var total = 0;
            var resolved = 0;
            var missing = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var clip in CollectClipsById().Values)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    total++;
                    if (ResolveBindingPath(instance.transform, binding.path) != null)
                        resolved++;
                    else
                        missing.Add(binding.path);
                }
            }

            if (total == 0)
            {
                Note("  !! 剪辑里没有任何曲线，动画不会有任何效果。");
                return;
            }

            if (missing.Count == 0)
            {
                Note($"clip bindings: {resolved}/{total} 条曲线全部落在 Prefab 层级上");
                return;
            }

            Note($"  !! clip bindings: {resolved}/{total} 条落地，{missing.Count} 条路径在 Prefab 里找不到：");
            foreach (var path in missing.Take(8))
                Note("       " + (string.IsNullOrEmpty(path) ? "(根节点)" : path));
        }

        /// <summary>
        /// 按 "/" 逐段解析绑定路径；空路径指 Animator 所在节点自身。
        /// 有些导入方式会把模型根名也写进路径，所以整条走不通时再去掉首段试一次。
        /// </summary>
        private static Transform ResolveBindingPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path))
                return root;

            var segments = path.Split('/');
            var hit = Walk(root, segments, 0);
            if (hit != null)
                return hit;

            return segments.Length > 1 ? Walk(root, segments, 1) : null;
        }

        /// <summary>从 <paramref name="start"/> 段起逐级向下找。Transform.Find 只查直接子级，正合此用。</summary>
        private static Transform Walk(Transform from, string[] segments, int start)
        {
            var current = from;
            for (var i = start; i < segments.Length; i++)
            {
                current = current.Find(segments[i]);
                if (current == null)
                    return null;
            }
            return current;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            var parts = path.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
