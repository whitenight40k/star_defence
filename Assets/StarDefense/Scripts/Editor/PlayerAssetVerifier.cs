using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// Read-only audit of the imported player/weapon assets. Dumps clip names, frame
    /// ranges, loop flags, material assignments, controller graph and prefab hierarchies
    /// so the result can be checked against the Blender-side contract without opening
    /// the editor UI.
    ///
    ///   Unity.exe -batchmode -nographics -quit -projectPath &lt;proj&gt;
    ///             -executeMethod StarDefense.EditorTools.PlayerAssetVerifier.Verify
    /// </summary>
    public static class PlayerAssetVerifier
    {
        private const string ArtRoot = "Assets/StarDefense/Art";
        private const string PlayerFbx = ArtRoot + "/Models/Characters/Player/CHR_Player_Astronaut_A_v1.fbx";
        private const string PickaxeFbx = ArtRoot + "/Models/Equipment/WPN_Pickaxe_A_v1.fbx";
        private const string GunFbx = ArtRoot + "/Models/Equipment/WPN_Gun_A_v1.fbx";
        private const string ControllerPath = ArtRoot + "/Animations/Characters/CTRL_Player_Astronaut_A.controller";
        private const string PlayerPrefabPath = ArtRoot + "/Prefabs/PFB_Player_Astronaut_A.prefab";
        private const string GunPrefabPath = ArtRoot + "/Prefabs/PFB_WPN_Gun_A.prefab";
        private const string PickaxePrefabPath = ArtRoot + "/Prefabs/PFB_WPN_Pickaxe_A.prefab";

        private static readonly StringBuilder Sb = new StringBuilder();

        private static void L(string s = "") => Sb.AppendLine(s);

        [MenuItem("Star Defense/Verify Player Assets")]
        public static void Verify()
        {
            Sb.Clear();

            L("===== PLAYER ASSETS VERIFICATION =====");
            L();

            VerifyModelSettings();
            VerifyClips();
            VerifyMaterials();
            VerifyController();
            VerifyPrefabs();

            L();
            L("===== END =====");

            var text = Sb.ToString();
            Debug.Log(text);

            // Also drop a machine-readable copy next to the project log.
            var outPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.dataPath) ?? ".",
                "Logs",
                "PlayerAssetsVerify.txt"
            );
            System.IO.File.WriteAllText(outPath, text);
        }

        // ------------------------------------------------------------------ //

        private static void VerifyModelSettings()
        {
            L("--- import settings ---");
            foreach (var path in new[] { PlayerFbx, GunFbx, PickaxeFbx })
            {
                var imp = AssetImporter.GetAtPath(path) as ModelImporter;
                if (imp == null)
                {
                    L($"  {path}: NO IMPORTER");
                    continue;
                }
                L(
                    $"  {System.IO.Path.GetFileName(path)}: rig={imp.animationType} "
                        + $"anim={imp.importAnimation} collider={imp.addCollider} "
                        + $"meshComp={imp.meshCompression} animComp={imp.animationCompression} "
                        + $"weld={imp.weldVertices} readable={imp.isReadable}"
                );
            }
            L();
        }

        private static void VerifyClips()
        {
            L("--- animation clips ---");
            var clips = AssetDatabase
                .LoadAllAssetsAtPath(PlayerFbx)
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal))
                .OrderBy(c => c.name)
                .ToList();

            L($"  count={clips.Count}");
            foreach (var c in clips)
            {
                L(
                    $"  {c.name,-26} length={c.length,7:F3}s fps={c.frameRate,5:F0} "
                        + $"loop={c.isLooping,-5} curves={AnimationUtility.GetCurveBindings(c).Length}"
                );
            }

            var avatars = AssetDatabase.LoadAllAssetsAtPath(PlayerFbx).OfType<Avatar>().ToList();
            L(
                $"  avatars={avatars.Count} -> {string.Join(", ", avatars.Select(a => a.name + "(" + a.isValid + ")"))}"
                    + (avatars.Count == 0 ? "   (Generic 骨架的正常结果：不生成 Avatar，动画按路径绑定)" : string.Empty)
            );
            L();

            L("--- clip loop/pose check (foot height stability not checked here) ---");
            var expected = new (string Id, bool Loop)[]
            {
                ("ANM_Player_Idle", true),
                ("ANM_Player_Walk", true),
                ("ANM_Player_Run", true),
                ("ANM_Player_JumpStart", false),
                ("ANM_Player_JumpLoop", true),
                ("ANM_Player_JumpLand", false),
                ("ANM_Player_Shoot", false),
                ("ANM_Player_Mine_Start", false),
                ("ANM_Player_Mine_Hit", false),
                ("ANM_Player_Mine_End", false),
            };
            foreach (var (id, loop) in expected)
            {
                // 按 id 解析而不是精确匹配名字：FBX 重新导入后，同一个动作可能同时以
                // 正式名（ANM_Player_Idle）和 take 名（CHR_Player_Astronaut_A|ANM_Player_Idle）
                // 两种形式存在，直接比名字会误报一排 MISSING。口径与 PlayerAssetBuilder 一致。
                //
                // 这里刻意分两步写、用 `== null` 而不是 `??`：AnimationClip 是 UnityEngine.Object，
                // `??` 走的是 C# 的引用判空，绕过 Unity 重载的 `==`。一个"已被销毁但引用还在"的
                // 剪辑在 `??` 眼里是非空的，于是核验器会拿着一具尸体继续往下比对。
                // （UNT0007 抓的就是这个；本项目已经吃过一次"引用断了却不报错"的亏。）
                var clip = clips.FirstOrDefault(c => c.name == id);
                if (clip == null)
                    clip = clips.FirstOrDefault(c => PlayerAssetBuilder.ExtractClipId(c.name) == id);

                if (clip == null)
                {
                    L($"  MISSING  {id}");
                    continue;
                }
                var ok = clip.isLooping == loop;
                L($"  {(ok ? "ok   " : "BAD  ")} {id,-26} expected loop={loop} actual={clip.isLooping}");
            }
            L();
        }

        private static void VerifyMaterials()
        {
            L("--- material slot assignments ---");
            foreach (var path in new[] { PlayerFbx, GunFbx, PickaxeFbx })
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model == null)
                {
                    L($"  {path}: NOT IMPORTED");
                    continue;
                }

                var renderers = model.GetComponentsInChildren<Renderer>(true);
                var slots = renderers
                    .SelectMany(r => r.sharedMaterials)
                    .Where(m => m != null)
                    .Select(m => m.name)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();

                L($"  {System.IO.Path.GetFileName(path)}: {renderers.Length} renderer(s), {slots.Count} material(s)");
                foreach (var s in slots)
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(ArtRoot + "/Materials/" + s + ".mat");
                    var external = mat != null ? "shared asset" : "EMBEDDED/UNMAPPED";
                    string info = "";
                    if (mat != null)
                    {
                        var emissive = mat.IsKeywordEnabled("_EMISSION")
                            ? " emissive=" + mat.GetColor("_EmissionColor")
                            : "";
                        info =
                            $" color={mat.color} gloss={mat.GetFloat("_Glossiness"):F2}{emissive}";
                    }
                    L($"     {s,-22} [{external}]{info}");
                }

                var comp = renderers.OfType<SkinnedMeshRenderer>().FirstOrDefault();
                if (comp != null && comp.sharedMesh != null)
                {
                    var mesh = comp.sharedMesh;
                    L(
                        $"     skinned mesh '{mesh.name}' verts={mesh.vertexCount} "
                            + $"subMeshes={mesh.subMeshCount} bones={comp.bones.Length} "
                            + $"rootBone={(comp.rootBone != null ? comp.rootBone.name : "null")}"
                    );
                }
            }
            L();
        }

        private static void VerifyController()
        {
            L("--- animator controller ---");
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (ctrl == null)
            {
                L("  MISSING " + ControllerPath);
                L();
                return;
            }

            L($"  path={ControllerPath}");
            L($"  parameters: {string.Join(", ", ctrl.parameters.Select(p => $"{p.name}:{p.type}"))}");
            ReportUnwiredParameters(ctrl);

            var sm = ctrl.layers[0].stateMachine;
            L($"  layers={ctrl.layers.Length} defaultState={(sm.defaultState != null ? sm.defaultState.name : "null")}");
            L($"  states={sm.states.Length}");
            foreach (var s in sm.states)
                L($"     {s.state.name,-26} motion={(s.state.motion != null ? s.state.motion.name : "null")}");

            L($"  anyStateTransitions={sm.anyStateTransitions.Length}");
            foreach (var t in sm.anyStateTransitions)
                L($"     -> {t.destinationState.name,-26} {Describe(t)}");

            foreach (var s in sm.states)
            {
                foreach (var t in s.state.transitions)
                    L($"     {s.state.name,-26} -> {t.destinationState.name,-26} {Describe(t)}");
            }
            L();
        }

        private static string Describe(AnimatorStateTransition t)
        {
            var conds = t.conditions.Length == 0
                ? "no-cond"
                : string.Join(" & ", t.conditions.Select(c => $"{c.parameter} {c.mode} {c.threshold}"));
            return $"[exit={t.hasExitTime} exitTime={t.exitTime:F2} dur={t.duration:F2}] {conds}";
        }

        /// <summary>
        /// 控制器声明的每个参数，都必须能在运行时找到写入方。
        ///
        /// 这是本项目吃过一次大亏之后加上的检查。当时的形状是：参数 <c>Grounded</c> 建好了、
        /// 连线也画了，但**没有任何脚本写它**，于是它永远停在 Unity 的初值 <c>false</c> 上。
        /// 挂 AnyState 的跳跃条件 "IfNot Grounded" 因此恒真，角色一进场景就无限重播跳跃循环；
        /// 而控制器图在编辑器里怎么看都是对的，没有任何一处会报错。
        ///
        /// 判据分两层：
        ///   1. 参数名必须出现在 AnimatorParameters 里 —— 那是运行时唯一的名字来源；
        ///   2. 该名字对应的 hash 字段必须被至少一个运行时脚本引用
        ///      （脚本用 <c>AnimatorParameters.Airborne</c> 这样的字段，不是字符串字面量）。
        /// </summary>
        private static void ReportUnwiredParameters(AnimatorController ctrl)
        {
            const string tablePath = "Assets/StarDefense/Scripts/Runtime/Core/AnimatorParameters.cs";
            const string runtimeDir = "Assets/StarDefense/Scripts/Runtime";

            if (!File.Exists(tablePath) || !Directory.Exists(runtimeDir))
            {
                L("  (跳过参数接线检查：读不到运行时源码目录)");
                return;
            }

            // 表里的命名规则：const string XxxName = "..." 配 static readonly int Xxx。
            var table = File.ReadAllText(tablePath);
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(table, @"const\s+string\s+(\w+)Name\s*="))
                fields.Add(m.Groups[1].Value);

            var writers = new StringBuilder();
            foreach (var file in Directory.GetFiles(runtimeDir, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFullPath(file) == Path.GetFullPath(tablePath))
                    continue;
                writers.Append(File.ReadAllText(file));
            }

            var writerText = writers.ToString();
            var unwired = new List<string>();

            foreach (var p in ctrl.parameters)
            {
                if (!fields.Contains(p.name))
                {
                    unwired.Add(p.name);
                    L($"  !! 参数 {p.name}：AnimatorParameters 里没有它的名字，运行时无从引用它");
                    continue;
                }

                if (writerText.Contains("AnimatorParameters." + p.name))
                    continue;

                unwired.Add(p.name);
                L($"  !! 参数 {p.name}：没有任何运行时脚本引用它 —— 它会永远停在默认初值上，");
                L("     用到它的转移条件因此恒真或恒假（本项目出过：恒真的跳跃条件 = 无限跳跃）");
            }

            if (unwired.Count == 0)
                L($"  参数接线：{ctrl.parameters.Length}/{ctrl.parameters.Length} 个都有运行时写入方");
        }

        private static void VerifyPrefabs()
        {
            L("--- prefabs ---");
            foreach (var path in new[] { GunPrefabPath, PickaxePrefabPath, PlayerPrefabPath })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    L($"  MISSING {path}");
                    continue;
                }

                L($"  {System.IO.Path.GetFileName(path)}");
                DumpHierarchy(prefab.transform, 2);

                var colliders = prefab.GetComponentsInChildren<Collider>(true);
                L($"     colliders={colliders.Length} (must be 0 on held weapons)");

                var animator = prefab.GetComponent<Animator>();
                if (animator != null)
                {
                    L(
                        $"     Animator avatar={(animator.avatar != null ? animator.avatar.name : "null")} "
                            + $"controller={(animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "null")} "
                            + $"rootMotion={animator.applyRootMotion} culling={animator.cullingMode}"
                    );
                }

                var visuals = prefab.GetComponent<PlayerToolVisuals>();
                if (visuals != null)
                {
                    L(
                        $"     PlayerToolVisuals pivot={(visuals.weaponPivot != null ? visuals.weaponPivot.name : "null")} "
                            + $"gun={(visuals.gunVisual != null ? visuals.gunVisual.name : "null")} "
                            + $"pickaxe={(visuals.pickaxeVisual != null ? visuals.pickaxeVisual.name : "null")}"
                    );
                }

                var skinned = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                L($"     skinnedMeshRenderers={skinned.Length}");
            }
            L();
        }

        private static void DumpHierarchy(Transform t, int depth)
        {
            if (depth > 6)
                return;
            var pad = new string(' ', depth * 2);
            var extra = "";
            var skinned = t.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null && skinned.sharedMesh != null)
                extra = $" [skin {skinned.sharedMesh.vertexCount}v]";
            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterials.Length > 0 && mr.sharedMaterials[0] != null)
                extra = $" [mat {mr.sharedMaterials[0].name}]";

            L($"{pad}{t.name}{extra}");

            foreach (Transform child in t)
                DumpHierarchy(child, depth + 1);
        }
    }
}
