using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// Read-only audit of the bestiary assets. Changes nothing; writes
    /// <c>Logs/EnemyAssetsVerify.txt</c>.
    ///
    /// Why this exists separately from the builder's own assertions: the builder checks
    /// what it is about to write, this checks what is actually on disk — the saved prefab,
    /// the imported FBX settings, and the last hop, whether each <see cref="EnemyConfig"/>
    /// points at its prefab. That last one is where the chain silently degrades to spheres:
    /// every earlier step can be perfect and the game still spawns primitives, with no error
    /// anywhere. So the binding gets its own audit rather than being inferred.
    ///
    /// Menu: Star Defense / Verify Enemy Assets
    /// </summary>
    public static class EnemyAssetVerifier
    {
        private const string EnemyDir = "Assets/StarDefense/ScriptableObjects/Enemies";

        /// <summary>
        /// Gameplay id → the prefab it must end up bound to. Mirrors
        /// <see cref="StarDefenseAssetBinder"/>'s table on purpose: if the two ever disagree,
        /// one of them is wrong and this report is where it shows up.
        ///
        /// <c>thief</c> maps to null — <c>ENM_Thief_A</c> is deliberately unmodelled until
        /// thief gameplay exists, so a null binding is the expected state, not a problem.
        /// </summary>
        private static readonly Dictionary<string, string> ExpectedBinding = new Dictionary<string, string>
        {
            { "crawler", "PFB_Charger_A" },
            { "bomber", "PFB_Bomber_A" },
            { "burrow", "PFB_Burrower_A" },
            { "sniper", "PFB_Sniper_A" },
            { "jammer", "PFB_Jammer_A" },
            { "boss", "PFB_PlanetBeast_A" },
            { "thief", null },
        };

        [MenuItem("Star Defense/Verify Enemy Assets", false, 12)]
        public static void Verify()
        {
            var sb = new StringBuilder();
            var problems = new List<string>();

            sb.AppendLine("Star Defense - bestiary asset verification");
            sb.AppendLine("when: " + DateTime.Now.ToString("u"));
            sb.AppendLine();

            sb.AppendLine("== textures ==");
            CheckTexture(sb, problems, EnemyAssetBuilder.AtlasTexPath, "atlas (albedo)");
            CheckTexture(sb, problems, EnemyAssetBuilder.EmissionTexPath, "atlas (emission mask)");
            sb.AppendLine();

            sb.AppendLine("== shared material ==");
            CheckMaterial(sb, problems);
            sb.AppendLine();

            sb.AppendLine("== prefabs ==");
            int bones = 0;

            foreach (EnemyAssetBuilder.EnemySpec spec in EnemyAssetBuilder.Enemies)
            {
                CheckModelImport(sb, problems, spec);
                CheckPrefab(sb, problems, spec, ref bones);
            }
            sb.AppendLine();

            sb.AppendLine("== config binding (last hop to spawning) ==");
            CheckBindings(sb, problems);
            sb.AppendLine();

            sb.AppendLine("== summary ==");
            sb.AppendLine($"creatures: {EnemyAssetBuilder.Enemies.Length}");
            sb.AppendLine($"bones total: {bones}");
            sb.AppendLine($"problems: {problems.Count}");
            foreach (string p in problems)
                sb.AppendLine("  ! " + p);

            string outPath = WriteReport(sb);

            if (problems.Count == 0)
                Debug.Log($"[EnemyAssetVerifier] all {EnemyAssetBuilder.Enemies.Length} creatures OK. Report: {outPath}");
            else
                Debug.LogWarning(
                    $"[EnemyAssetVerifier] {problems.Count} problem(s) across "
                        + $"{EnemyAssetBuilder.Enemies.Length} creatures. Report: {outPath}"
                );
        }

        private static void CheckTexture(StringBuilder sb, List<string> problems, string path, string label)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null)
            {
                problems.Add("missing texture " + path);
                sb.AppendLine("  MISSING " + path);
                return;
            }

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            sb.AppendLine(
                $"  {tex.name} ({label})  {tex.width}x{tex.height}  sRGB={ti.sRGBTexture} "
                    + $"wrap={ti.wrapMode} maxSize={ti.maxTextureSize} "
                    + $"compression={ti.textureCompression} mipmaps={ti.mipmapEnabled}"
            );

            if (!ti.sRGBTexture)
                problems.Add(path + ": should import as sRGB (the masks were authored as sRGB)");
            if (ti.wrapMode != TextureWrapMode.Clamp)
                problems.Add(path + ": wrap mode should be Clamp so atlas tiles cannot bleed");
            if (ti.maxTextureSize < 2048)
                problems.Add(path + ": max size clipped below the authored 2048");
            if (!ti.mipmapEnabled)
                problems.Add(path + ": mipmaps are off; the bugs will alias at range");
        }

        private static void CheckMaterial(StringBuilder sb, List<string> problems)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(EnemyAssetBuilder.AtlasMatPath);
            if (mat == null)
            {
                problems.Add("missing material " + EnemyAssetBuilder.AtlasMatPath);
                sb.AppendLine("  MISSING " + EnemyAssetBuilder.AtlasMatPath);
                return;
            }

            Texture main = mat.GetTexture("_MainTex");
            Texture emission = mat.GetTexture("_EmissionMap");
            Color emissionColor = mat.GetColor("_EmissionColor");
            float gloss = mat.GetFloat("_Glossiness");

            sb.AppendLine($"  {mat.name}  shader={mat.shader.name}");
            sb.AppendLine($"    _MainTex       = {(main != null ? main.name : "null")}");
            sb.AppendLine($"    _EmissionMap   = {(emission != null ? emission.name : "null")}");
            sb.AppendLine($"    _EmissionColor = {emissionColor}   _Glossiness={gloss:F3}");
            sb.AppendLine($"    keyword _EMISSION = {mat.IsKeywordEnabled("_EMISSION")}");

            if (main == null)
                problems.Add("MAT_Enemy_Atlas has no _MainTex — every creature would be flat grey");
            if (emission == null)
                problems.Add("MAT_Enemy_Atlas has no _EmissionMap — glowing eyes and sacs would be dark");
            if (emissionColor.maxColorComponent <= 0.01f)
                problems.Add("MAT_Enemy_Atlas _EmissionColor is black — emission disabled despite the map");
            if (!mat.IsKeywordEnabled("_EMISSION"))
                problems.Add("MAT_Enemy_Atlas does not have _EMISSION enabled");

            float expectedGloss = 1f - EnemyAssetBuilder.AtlasRoughness;
            if (Mathf.Abs(gloss - expectedGloss) > 0.01f)
                problems.Add(
                    $"MAT_Enemy_Atlas _Glossiness is {gloss:F3}, expected {expectedGloss:F3} "
                        + $"(roughness {EnemyAssetBuilder.AtlasRoughness:F2} as authored in enm_lib.py)"
                );
        }

        private static void CheckModelImport(StringBuilder sb, List<string> problems, EnemyAssetBuilder.EnemySpec spec)
        {
            var imp = AssetImporter.GetAtPath(spec.FbxPath) as ModelImporter;
            if (imp == null)
            {
                problems.Add("missing model " + spec.FbxPath);
                sb.AppendLine($"  {spec.Id}: MISSING {spec.FbxPath}");
                return;
            }

            bool generic = imp.animationType == ModelImporterAnimationType.Generic;
            bool keepsHierarchy = !imp.optimizeGameObjects;

            sb.AppendLine(
                $"  {spec.Id}: animationType={imp.animationType} optimizeGameObjects={imp.optimizeGameObjects} "
                    + $"readable={imp.isReadable} weld={imp.weldVertices} colliders={imp.addCollider}"
            );

            if (!generic)
                problems.Add($"{spec.Id}: animationType should be Generic (project-wide policy, no Humanoid)");

            // The one setting whose failure is invisible: with this on, the sockets and the
            // unweighted DEF_Root vanish from the hierarchy and nothing renders differently.
            if (!keepsHierarchy)
                problems.Add(
                    $"{spec.Id}: optimizeGameObjects is ON — bones and Socket_* transforms are being "
                        + "collapsed into the skin. Missing sockets cannot be detected at runtime."
                );
        }

        private static void CheckPrefab(
            StringBuilder sb,
            List<string> problems,
            EnemyAssetBuilder.EnemySpec spec,
            ref int bones
        )
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(spec.PrefabPath) == null)
            {
                problems.Add("missing prefab " + spec.PrefabPath);
                sb.AppendLine($"  {spec.PrefabName}: MISSING");
                return;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(spec.PrefabPath);
            try
            {
                Transform[] all = EnemyAssetBuilder.Walk(root.transform).ToArray();
                var names = new HashSet<string>(all.Select(t => t.name));

                int boneCount = names.Count(n => n.StartsWith("DEF_", StringComparison.Ordinal));
                bones += boneCount;

                var renderers = root.GetComponentsInChildren<Renderer>(true);
                var materials = renderers
                    .SelectMany(r => r.sharedMaterials)
                    .Where(m => m != null)
                    .Select(m => m.name)
                    .Distinct()
                    .ToList();

                // Deliberately no triangle count: the models import with isReadable = false so
                // no CPU copy of the vertex data is kept, and Unity refuses triangles on those
                // meshes (with a console error). A readable copy just to print a number would
                // be the wrong trade. Mesh presence is covered by the bounds check below.
                var meshNames = renderers
                    .Select(r => EnemyAssetBuilder.TryGetMesh(r, out Mesh m) ? m.name : "<none>")
                    .ToList();

                var sockets = names.Where(n => n.StartsWith("Socket_", StringComparison.Ordinal)).OrderBy(n => n).ToList();

                sb.AppendLine($"  {spec.PrefabName}:");
                sb.AppendLine($"      bones={boneCount} renderers={renderers.Length} children={root.transform.childCount}");
                sb.AppendLine($"      mesh: {(meshNames.Count > 0 ? string.Join(", ", meshNames) : "NONE")}");
                sb.AppendLine($"      materials: {(materials.Count > 0 ? string.Join(", ", materials) : "NONE")}");
                sb.AppendLine($"      sockets: {(sockets.Count > 0 ? string.Join(", ", sockets) : "NONE")}");

                if (boneCount != EnemyAssetBuilder.ExpectedBoneCount)
                    problems.Add($"{spec.PrefabName}: {boneCount} DEF_ bones, expected {EnemyAssetBuilder.ExpectedBoneCount}");

                foreach (string bone in EnemyAssetBuilder.RequiredBones)
                {
                    if (!names.Contains(bone))
                        problems.Add($"{spec.PrefabName}: rig is missing {bone}");
                }

                foreach (string socket in spec.Sockets)
                {
                    if (!names.Contains(socket))
                        problems.Add($"{spec.PrefabName}: missing socket {socket}");
                }

                if (renderers.Length != 1)
                    problems.Add($"{spec.PrefabName}: {renderers.Length} renderers, contract says a single mesh");

                if (materials.Count != 1 || materials[0] != EnemyAssetBuilder.AtlasMatName)
                    problems.Add(
                        $"{spec.PrefabName}: materials should be exactly [{EnemyAssetBuilder.AtlasMatName}], "
                            + $"got [{string.Join(", ", materials)}]"
                    );

                if (EnemyAssetBuilder.TryComputeLocalBounds(root, out Bounds local))
                {
                    Vector3 expected = spec.ExpectedLocalSize;
                    sb.AppendLine(
                        $"      local bounds {local.size.x:F2} x {local.size.y:F2} x {local.size.z:F2} m "
                            + $"(ledger {expected.x:F2} x {expected.y:F2} x {expected.z:F2}), "
                            + $"lowest Z {local.min.z:F3} m"
                    );

                    float worst = Mathf.Max(
                        RelativeError(local.size.x, expected.x),
                        Mathf.Max(RelativeError(local.size.y, expected.y), RelativeError(local.size.z, expected.z))
                    );

                    if (worst > EnemyAssetBuilder.SizeTolerance)
                        problems.Add(
                            $"{spec.PrefabName}: size off the ledger by {worst * 100f:F0}% "
                                + $"({local.size.x:F2}x{local.size.y:F2}x{local.size.z:F2} vs "
                                + $"{expected.x:F2}x{expected.y:F2}x{expected.z:F2}) — check the FBX import scale"
                        );

                    // In the prefab root's local space (the Blender one) up is +Z, so an origin
                    // on the ground puts the lowest vertex at Z = 0.
                    if (Mathf.Abs(local.min.z) > 0.08f)
                        problems.Add(
                            $"{spec.PrefabName}: lowest local Z is {local.min.z:F3} m, expected ~0 — "
                                + "the origin is not on the ground plane, so spawns will hover or sink"
                        );
                }
                else
                {
                    problems.Add($"{spec.PrefabName}: no mesh, cannot size a collider");
                }

                BoxCollider box = root.GetComponent<BoxCollider>();
                if (box == null)
                {
                    // Without a collider the player's gun raycast hits nothing — shooting into
                    // a crowd does no damage and reports no error at all.
                    problems.Add($"{spec.PrefabName}: no BoxCollider — the gun raycast cannot hit it");
                }
                else if (box.isTrigger)
                {
                    problems.Add($"{spec.PrefabName}: BoxCollider is a trigger");
                }

                Rigidbody body = root.GetComponent<Rigidbody>();
                if (body == null)
                    problems.Add(
                        $"{spec.PrefabName}: no Rigidbody — a collider with no body is static to PhysX "
                            + "and gets re-inserted into the broadphase on every move"
                    );
                else if (!body.isKinematic)
                    problems.Add($"{spec.PrefabName}: Rigidbody should be kinematic (position comes from transform)");

                if (all.Any(t => GameObjectUtility.GetStaticEditorFlags(t.gameObject) != 0))
                    problems.Add(
                        $"{spec.PrefabName}: static batching flags present — enemies move, and a batched "
                            + "instance renders at its batched position"
                    );

                if (root.GetComponent<Animator>() != null)
                    sb.AppendLine("      animator: present, controller unassigned (expected until ANM_ clips exist)");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void CheckBindings(StringBuilder sb, List<string> problems)
        {
            string[] guids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { EnemyDir });
            int bound = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var config = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (config == null)
                    continue;

                var serialized = new SerializedObject(config);
                SerializedProperty idProperty = serialized.FindProperty("id");
                SerializedProperty prefabProperty = serialized.FindProperty("prefab");
                if (idProperty == null || prefabProperty == null)
                    continue;

                string id = idProperty.stringValue;
                var prefab = prefabProperty.objectReferenceValue as GameObject;

                if (!ExpectedBinding.TryGetValue(id, out string expectedName))
                {
                    problems.Add($"{config.name}: id '{id}' is not in the expected binding table");
                    sb.AppendLine($"  ?  {config.name} (id={id}): unknown id");
                    continue;
                }

                if (expectedName == null)
                {
                    sb.AppendLine(
                        $"  -  {config.name} (id={id}): no art asset expected yet "
                            + $"→ {(prefab == null ? "unbound, primitive fallback (correct)" : "BOUND to " + prefab.name + " — unexpected")}"
                    );

                    if (prefab != null)
                        problems.Add($"{config.name}: bound to {prefab.name} but no art asset is expected for '{id}'");

                    continue;
                }

                if (prefab == null)
                {
                    problems.Add(
                        $"{config.name} (id={id}): prefab is null — this creature spawns as a primitive. "
                            + $"Expected {expectedName}."
                    );
                    sb.AppendLine($"  ✗  {config.name} (id={id}): unbound, expected {expectedName}");
                    continue;
                }

                if (prefab.name != expectedName)
                {
                    problems.Add($"{config.name} (id={id}): bound to {prefab.name}, expected {expectedName}");
                    sb.AppendLine($"  ✗  {config.name} (id={id}): {prefab.name}, expected {expectedName}");
                    continue;
                }

                bound++;
                sb.AppendLine($"  ✓  {config.name} (id={id}) → {prefab.name}");
            }

            sb.AppendLine($"  bound: {bound}/{ExpectedBinding.Count - 1} (thief has no asset by design)");
        }

        private static float RelativeError(float actual, float expected)
        {
            return expected <= 0.0001f ? 0f : Mathf.Abs(actual - expected) / expected;
        }

        private static string WriteReport(StringBuilder sb)
        {
            string root = Path.GetDirectoryName(Application.dataPath);
            string logDir = Path.Combine(root ?? ".", "Logs");
            Directory.CreateDirectory(logDir);
            string outPath = Path.Combine(logDir, "EnemyAssetsVerify.txt");
            File.WriteAllText(outPath, sb.ToString());
            return outPath;
        }
    }
}
