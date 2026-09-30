using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// Turns the exported enemy / boss FBX into game-ready Unity assets.
    ///
    /// Same division of labour as <see cref="StructureAssetBuilder"/>: import settings,
    /// the shared enemy material, and one prefab per creature. It touches no gameplay
    /// script and places nothing in the scene — enemies are spawned at runtime, so there
    /// is no scene instance to place.
    ///
    /// Three things differ from the building and player kits, all of them enemy-specific:
    ///
    /// 1. **Its own atlas.** The bestiary uses <c>TEX_Enemy_Atlas_A</c> and has nothing to
    ///    do with <c>TEX_Structure_Atlas_A</c>. Sharing one would push creature colours into
    ///    the building atlas and force it from 4×4 to 5×5 — which would shift the UV region
    ///    of eleven already-delivered buildings.
    /// 2. **It has a rig.** All six share one 17-bone layout (<c>DEF_</c>), imported as
    ///    Generic, the same strategy as the player (this project never maps Humanoid).
    ///    <c>optimizeGameObjects</c> must stay off: it folds bones into the skin and swallows
    ///    the <c>Socket_*</c> transforms with them, and a missing socket is a silent failure —
    ///    the prefab still looks perfect. (The same reason the FBX exporter dropped this rig's
    ///    <c>DEF_Root</c> the first time until it was flagged as deforming.)
    /// 3. **It is moved by transform every frame.** So no static batching flags, and the
    ///    collider is a cheap box rather than a non-convex MeshCollider.
    ///
    /// Run from the menu (Star Defense / Build Enemy Assets) or let
    /// <see cref="StarDefensePipeline"/> drive it.
    /// </summary>
    public static class EnemyAssetBuilder
    {
        private const string ArtRoot = "Assets/StarDefense/Art";

        // Paths are internal so StarDefenseAssetVerifier audits the same locations this
        // builder writes to. A verifier with its own copy of the paths can pass while the
        // builder is pointed somewhere else entirely.
        internal const string MatDir = ArtRoot + "/Materials";
        internal const string PrefabDir = ArtRoot + "/Prefabs";
        internal const string ModelDir = ArtRoot + "/Models/Enemies";
        internal const string TexDir = ArtRoot + "/Textures/Enemies";

        internal const string AtlasTexPath = TexDir + "/TEX_Enemy_Atlas_A.png";
        internal const string EmissionTexPath = TexDir + "/TEX_Enemy_Atlas_A_Emission.png";
        internal const string AtlasMatName = "MAT_Enemy_Atlas";
        internal const string AtlasMatPath = MatDir + "/MAT_Enemy_Atlas.mat";

        /// <summary>Roughness authored in enm_lib.py (BSDF input, 0..1).</summary>
        internal const float AtlasRoughness = 0.62f;

        /// <summary>Emission Strength authored in enm_lib.py.</summary>
        internal const float EmissionBoost = 2.4f;

        /// <summary>Where the "this pass already ran" stamp goes. Read by the pipeline.</summary>
        internal const string MarkerPath = ArtRoot + "/.enemy_assets_built";

        private const string LogPath = "Logs/EnemyAssetsBuild.txt";

        /// <summary>Representative product. The pipeline uses it to tell "built" from "marker left over".</summary>
        internal const string ProbePath = PrefabDir + "/PFB_Charger_A.prefab";

        /// <summary>Every creature shares this bone layout. Count is asserted, not just presence.</summary>
        internal const int ExpectedBoneCount = 17;

        /// <summary>Bones a skinned mesh cannot survive without. <c>DEF_Root</c> carries no weight by design.</summary>
        internal static readonly string[] RequiredBones =
        {
            "DEF_Root",
            "DEF_Body",
            "DEF_Abdomen",
            "DEF_Head",
            "DEF_Jaw",
            "DEF_LegFront_L",
            "DEF_LegMid_L",
            "DEF_LegBack_L",
        };

        /// <summary>
        /// One entry per delivered creature.
        ///
        /// The dimensions are the ones in the art delivery ledger (width × height × length
        /// in metres) and exist purely as a cross-check: an FBX that comes in at the wrong
        /// unit scale still imports, still builds a prefab, and only shows up as "the bugs
        /// are knee-high" in play. Comparing against a number catches it at build time.
        ///
        /// Sockets are asserted too. They drive weak-point hits and per-creature effects,
        /// and a socket that got optimised away cannot be distinguished from one that was
        /// never authored.
        /// </summary>
        internal static readonly EnemySpec[] Enemies =
        {
            new EnemySpec("ENM_Charger_A", 1.11f, 0.70f, 1.47f, "Socket_Mouth", "Socket_WeakPoint"),
            new EnemySpec("ENM_Bomber_A", 1.18f, 0.83f, 1.35f, "Socket_Mouth", "Socket_WeakPoint", "Socket_Bomb"),
            new EnemySpec("ENM_Burrower_A", 1.53f, 0.46f, 1.67f, "Socket_Mouth", "Socket_WeakPoint"),
            new EnemySpec("ENM_Sniper_A", 1.16f, 1.01f, 2.09f, "Socket_Mouth", "Socket_WeakPoint", "Socket_Aim"),
            new EnemySpec("ENM_Jammer_A", 1.17f, 0.96f, 1.31f, "Socket_Mouth", "Socket_WeakPoint", "Socket_JamEmitter"),
            new EnemySpec("BOS_PlanetBeast_A", 3.58f, 2.53f, 4.89f, "Socket_Mouth", "Socket_WeakPoint", "Socket_Core"),
        };

        /// <summary>Delivered size tolerance, as a fraction. Loose enough to absorb bounds padding, tight enough to catch a unit error.</summary>
        internal const float SizeTolerance = 0.12f;

        /// <summary>
        /// How far the lowest vertex may sit off the root plane before it is worth complaining
        /// about (metres). Loose on purpose: the delivered creatures already sit 1.5–5 cm above
        /// it (the boss is the worst at 4.98 cm, per the art-side FBX readback), which is real
        /// geometry — leg claws modelled just clear of the plane — not an origin mistake. An
        /// origin that is genuinely in the middle of the body would be off by half the height,
        /// i.e. ~1.26 m for the boss, so the two cases are nowhere near each other.
        /// </summary>
        private const float FootTolerance = 0.08f;

        internal readonly struct EnemySpec
        {
            internal readonly string Id;
            internal readonly float Width;
            internal readonly float Height;
            internal readonly float Length;
            internal readonly string[] Sockets;

            internal EnemySpec(string id, float width, float height, float length, params string[] sockets)
            {
                Id = id;
                Width = width;
                Height = height;
                Length = length;
                Sockets = sockets;
            }

            internal string FileName => Id + "_v1.fbx";

            /// <summary>
            /// Prefab name keeps the art naming (prefix stripped), so <c>ENM_Charger_A</c>
            /// becomes <c>PFB_Charger_A</c>. The mapping from gameplay id to art name lives
            /// in <see cref="StarDefenseAssetBinder"/> — that seam is deliberate: renaming a
            /// creature for gameplay reasons must never rename a delivered asset.
            /// </summary>
            internal string PrefabName => "PFB_" + StripPrefix(Id);

            /// <summary>
            /// Size in the prefab root's local space. That space is the Blender one — the FBX
            /// root node carries the -90° X axis conversion — so X is width, Y is length and
            /// Z is height. (Same conversion the buildings and player rely on.)
            /// </summary>
            internal Vector3 ExpectedLocalSize => new Vector3(Width, Length, Height);

            internal string FbxPath => ModelDir + "/" + FileName;

            internal string PrefabPath => PrefabDir + "/" + PrefabName + ".prefab";
        }

        private static readonly List<string> Log = new List<string>();

        // ------------------------------------------------------------------ //
        [MenuItem("Star Defense/Build Enemy Assets", false, 11)]
        public static void BuildEnemyAssets()
        {
            Log.Clear();
            try
            {
                EnsureFolder(MatDir);
                EnsureFolder(PrefabDir);
                EnsureFolder(TexDir);

                ConfigureTextureImporters();
                ConfigureModelImporters();
                RemapMaterials();
                BuildPrefabs();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                WriteStamp();
                FlushLog("BUILD OK");
            }
            catch (Exception e)
            {
                FlushLog("BUILD FAILED");

                // No stamp on failure. A marker written by a failed pass is the exact thing
                // that makes "I fixed the code but nothing changed" happen: the pipeline
                // sees the marker, believes the assets exist, and skips the step forever.
                DeleteStamp();

                Debug.LogError("[EnemyAssetBuilder] " + e);
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
                throw;
            }
        }

        private static void FlushLog(string headline)
        {
            Debug.Log("[EnemyAssetBuilder] ===== " + headline + " =====");
            foreach (string line in Log)
                Debug.Log("[EnemyAssetBuilder] " + line);

            try
            {
                Directory.CreateDirectory("Logs");
                var text = new StringBuilder();
                text.AppendLine("Star Defense 敌人资产构建报告 —— " + headline);
                text.AppendLine("when: " + DateTime.Now.ToString("u"));
                text.AppendLine();
                foreach (string line in Log)
                    text.AppendLine(line);

                File.WriteAllText(LogPath, text.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EnemyAssetBuilder] 无法写日志 " + LogPath + "：" + e.Message);
            }
        }

        private static void Note(string line) => Log.Add(line);

        private static void WriteStamp()
        {
            try
            {
                File.WriteAllText(MarkerPath, "outcome=OK at " + DateTime.Now.ToString("u") + "\n");
                AssetDatabase.ImportAsset(MarkerPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EnemyAssetBuilder] 无法写入标记：" + e.Message);
            }
        }

        private static void DeleteStamp()
        {
            try
            {
                if (File.Exists(MarkerPath))
                    File.Delete(MarkerPath);
                if (File.Exists(MarkerPath + ".meta"))
                    File.Delete(MarkerPath + ".meta");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EnemyAssetBuilder] 无法删除标记：" + e.Message);
            }
        }

        // ------------------------------------------------------------------ //
        // 1) textures
        // ------------------------------------------------------------------ //

        private static void ConfigureTextureImporters()
        {
            ConfigureTexture(AtlasTexPath, "bestiary atlas (albedo)");
            ConfigureTexture(EmissionTexPath, "bestiary atlas (emission mask)");
        }

        private static void ConfigureTexture(string path, string label)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null)
                throw new InvalidOperationException("No TextureImporter at " + path);

            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true; // authored as sRGB values by enm_tex.py
            ti.alphaSource = TextureImporterAlphaSource.None;
            ti.alphaIsTransparency = false;
            ti.mipmapEnabled = true;
            ti.filterMode = FilterMode.Bilinear;
            ti.wrapMode = TextureWrapMode.Clamp; // UVs never leave their tile
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.maxTextureSize = 2048;
            // Flat colour blocks are exactly what block compression is worst at, so ask for
            // the high-quality variant rather than the default DXT1 — same call as the
            // structure atlas, for the same reason.
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();

            Note($"texture configured: {path} ({label})");
        }

        // ------------------------------------------------------------------ //
        // 2) models
        // ------------------------------------------------------------------ //

        private static void ConfigureModelImporters()
        {
            foreach (EnemySpec spec in Enemies)
                ConfigureModel(spec);
        }

        private static void ConfigureModel(EnemySpec spec)
        {
            ModelImporter imp = Importer(spec.FbxPath);

            // Generic, not None: the rig is real and the contract says every skeleton in this
            // project imports as Generic (no Humanoid anywhere). Note that Generic produces no
            // Avatar sub-asset — that is the expected state, not a defect, and clip bindings
            // resolve by transform path instead.
            imp.animationType = ModelImporterAnimationType.Generic;

            // No ANM_ clips exist for the bestiary yet. Leaving the animation pass on is
            // harmless (it produces zero clips) and means the first enemy clip dropped into
            // this FBX will be picked up with no code change here.
            imp.importAnimation = true;

            imp.importBlendShapes = false;
            imp.importCameras = false;
            imp.importLights = false;
            imp.addCollider = false;      // the prefab authors its own box collider
            imp.isReadable = false;       // the collider is baked from mesh.bounds, which reads fine unreadable
            imp.meshCompression = ModelImporterMeshCompression.Off;
            imp.weldVertices = false;     // keep the authored hard edges on the low-poly carapaces
            imp.importNormals = ModelImporterNormals.Import;
            imp.importTangents = ModelImporterTangents.None; // no normal maps in this kit
            imp.animationCompression = ModelImporterAnimationCompression.Off;
            imp.importAnimatedCustomProperties = false;
            imp.resampleCurves = true;

            // Must stay false. true collapses the transform hierarchy into the skin, which
            // deletes the sockets and every DEF_ bone that carries no weight — the model still
            // renders, so the failure is invisible until something tries to read a socket.
            imp.optimizeGameObjects = false;

            imp.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            imp.materialLocation = ModelImporterMaterialLocation.InPrefab;
            imp.materialName = ModelImporterMaterialName.BasedOnMaterialName;
            imp.materialSearch = ModelImporterMaterialSearch.Local;

            imp.SaveAndReimport();
            Note("model import configured: " + spec.FileName);
        }

        private static ModelImporter Importer(string path)
        {
            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            if (imp == null)
                throw new InvalidOperationException("No ModelImporter at " + path);
            return imp;
        }

        // ------------------------------------------------------------------ //
        // 3) material
        // ------------------------------------------------------------------ //

        private static void RemapMaterials()
        {
            Material atlas = BuildAtlasMaterial();
            AssetDatabase.SaveAssets();

            var map = new Dictionary<string, Material> { [AtlasMatName] = atlas };

            foreach (EnemySpec spec in Enemies)
            {
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(spec.FbxPath);
                if (model == null)
                    throw new InvalidOperationException("FBX did not import a GameObject: " + spec.FbxPath);

                var slots = model
                    .GetComponentsInChildren<Renderer>(true)
                    .SelectMany(r => r.sharedMaterials)
                    .Where(m => m != null)
                    .Select(m => Regex.Replace(m.name, @"\.\d+$", ""))
                    .Distinct()
                    .ToList();

                // The contract is one mesh, one material slot: glow is carried by the emission
                // page, not a second material. More slots than one means either the model
                // changed shape or the import drifted — both worth stopping for, because at
                // runtime the symptom is just "one part of the bug is magenta".
                if (slots.Count != 1)
                    throw new InvalidOperationException(
                        $"{spec.Id} expected a single material slot, got {slots.Count}: {string.Join(", ", slots)}"
                    );

                // One importer instance for the whole asset: AddRemap only lands if the same
                // wrapper is used for the edit and the SaveAndReimport that flushes it.
                ModelImporter imp = Importer(spec.FbxPath);

                foreach (string name in slots)
                {
                    if (!map.TryGetValue(name, out Material target))
                        throw new InvalidOperationException(
                            $"{spec.Id} uses a material this builder does not know: {name}"
                        );

                    imp.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), name), target);
                }

                imp.SaveAndReimport();
                Note($"remapped {spec.Id}: {string.Join(", ", slots)} -> {AtlasMatName}");
            }
        }

        private static Material BuildAtlasMaterial()
        {
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasTexPath);
            var emissive = AssetDatabase.LoadAssetAtPath<Texture2D>(EmissionTexPath);
            if (atlas == null)
                throw new InvalidOperationException("Atlas texture missing: " + AtlasTexPath);
            if (emissive == null)
                throw new InvalidOperationException("Emission texture missing: " + EmissionTexPath);

            var mat = AssetDatabase.LoadAssetAtPath<Material>(AtlasMatPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(mat, AtlasMatPath);
            }

            mat.shader = Shader.Find("Standard");
            mat.color = Color.white; // the atlas supplies all colour; _Color must not tint it
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Glossiness", 1f - AtlasRoughness);

            mat.SetTexture("_MainTex", atlas);
            mat.SetTextureScale("_MainTex", Vector2.one);
            mat.SetTextureOffset("_MainTex", Vector2.zero);

            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            mat.SetTexture("_EmissionMap", emissive);
            mat.SetColor("_EmissionColor", Color.white * EmissionBoost);

            EditorUtility.SetDirty(mat);
            Note($"material written: {AtlasMatPath} (albedo + emission map, shared by the whole bestiary)");
            return mat;
        }

        // ------------------------------------------------------------------ //
        // 4) prefabs
        // ------------------------------------------------------------------ //

        private static void BuildPrefabs()
        {
            foreach (EnemySpec spec in Enemies)
                BuildPrefab(spec);
        }

        private static void BuildPrefab(EnemySpec spec)
        {
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(spec.FbxPath);
            if (model == null)
                throw new InvalidOperationException("Missing model for prefab: " + spec.FbxPath);

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                instance.name = spec.PrefabName;

                var names = new HashSet<string>(Walk(instance.transform).Select(t => t.name));

                int bones = names.Count(n => n.StartsWith("DEF_", StringComparison.Ordinal));
                if (bones != ExpectedBoneCount)
                    throw new InvalidOperationException(
                        $"{spec.Id}: found {bones} DEF_ bones, expected {ExpectedBoneCount}. "
                            + "The rig was stripped on import — check optimizeGameObjects."
                    );

                var missing = RequiredBones.Where(b => !names.Contains(b)).ToList();
                if (missing.Count > 0)
                    throw new InvalidOperationException(
                        $"{spec.Id}: rig is missing {string.Join(", ", missing)}"
                    );

                var missingSockets = spec.Sockets.Where(s => !names.Contains(s)).ToList();
                if (missingSockets.Count > 0)
                    throw new InvalidOperationException(
                        $"{spec.Id}: missing sockets {string.Join(", ", missingSockets)}. "
                            + "Sockets are plain transforms; if optimizeGameObjects is on they are removed "
                            + "without any error and only fail much later."
                    );

                if (!TryComputeLocalBounds(instance, out Bounds local))
                    throw new InvalidOperationException($"{spec.Id} has no mesh to size a collider from");

                VerifySize(spec, local.size);
                VerifyFoot(spec, local.min.z);

                // Anything the art side exported in the way of colliders goes first: the rig
                // makes a per-mesh MeshCollider both wrong and expensive.
                foreach (Collider c in instance.GetComponentsInChildren<Collider>(true))
                    UnityEngine.Object.DestroyImmediate(c);

                // No static flags anywhere in the hierarchy. Enemies are spawned, moved every
                // frame and destroyed; a batched instance renders at the position it was batched
                // at, which looks like "some bugs are stuck at the spawn point".
                foreach (Transform t in Walk(instance.transform))
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, 0);

                // The player's gun finds enemies with a Physics.Raycast, so a collider is not
                // optional — without one, shooting simply does nothing and reports nothing.
                //
                // A box rather than the exact mesh hull: it is far cheaper to move, and enemies
                // move every frame. The centre and size come straight from the root-local bounds
                // computed above, so with the axis conversion already baked into the root this
                // lands correctly oriented without hard-coding which local axis is "up".
                BoxCollider box = instance.AddComponent<BoxCollider>();
                box.center = local.center;
                box.size = new Vector3(
                    Mathf.Max(local.size.x, 0.05f),
                    Mathf.Max(local.size.y, 0.05f),
                    Mathf.Max(local.size.z, 0.05f)
                );

                // A kinematic body rides along with the collider. A collider with no Rigidbody is
                // a *static* collider to PhysX, which has to be re-inserted into the broadphase
                // AABB tree on every move — pure waste with dozens of bugs on screen, and it
                // never shows up as anything but frame time.
                Rigidbody body = instance.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.useGravity = false;
                body.interpolation = RigidbodyInterpolation.None; // position comes from transform writes

                GameObject saved = PrefabUtility.SaveAsPrefabAsset(instance, spec.PrefabPath);
                if (saved == null)
                    throw new InvalidOperationException("Prefab save returned null: " + spec.PrefabPath);

                Note(
                    $"prefab written: {spec.PrefabPath} "
                        + $"(local size {local.size.x:F2} x {local.size.y:F2} x {local.size.z:F2} m, "
                        + $"feet at {local.min.z:F3} m, {bones} bones, "
                        + $"{spec.Sockets.Length} sockets, BoxCollider + kinematic Rigidbody)"
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Size cross-check against the art delivery ledger. A mismatch here is almost always
        /// a unit/scale problem in the FBX, which otherwise only surfaces as "the bugs look
        /// knee-high" once someone plays the build.
        /// </summary>
        private static void VerifySize(EnemySpec spec, Vector3 actual)
        {
            Vector3 expected = spec.ExpectedLocalSize;
            float worst = Mathf.Max(
                RelativeError(actual.x, expected.x),
                Mathf.Max(RelativeError(actual.y, expected.y), RelativeError(actual.z, expected.z))
            );

            Note($"  size check {spec.Id}: actual {actual.x:F2} x {actual.y:F2} x {actual.z:F2} m, "
                 + $"ledger {expected.x:F2} x {expected.y:F2} x {expected.z:F2} m (X width / Y length / Z height)");

            if (worst <= SizeTolerance)
                return;

            Debug.LogWarning(
                $"[EnemyAssetBuilder] {spec.Id} 尺寸与交付台账相差 {worst * 100f:F0}%（实际 "
                + $"{actual.x:F2}×{actual.y:F2}×{actual.z:F2} m，台账 {expected.x:F2}×{expected.y:F2}×{expected.z:F2} m）。"
                + "若相差是整倍数，先查 FBX 导入比例。"
            );
        }

        private static float RelativeError(float actual, float expected)
        {
            return expected <= 0.0001f ? 0f : Mathf.Abs(actual - expected) / expected;
        }

        /// <summary>
        /// Foot check. The contract puts the origin on the ground, and this is the one number
        /// that decides whether a creature hovers or sinks. In the prefab root's local space
        /// (the Blender one) up is +Z, so the lowest point should sit at Z = 0.
        /// </summary>
        private static void VerifyFoot(EnemySpec spec, float lowestZ)
        {
            Note($"  foot check {spec.Id}: lowest local Z = {lowestZ:F4} m");

            if (Mathf.Abs(lowestZ) <= FootTolerance)
                return;

            Debug.LogWarning(
                $"[EnemyAssetBuilder] {spec.Id} 最低点在本地 Z = {lowestZ:F3} m（期望 0）。"
                + "原点不在底面时，刷出来的敌人会整体悬空或陷进沙里 —— "
                + "StarDefenseGame.SpawnEnemy 是按原点贴地摆放的。"
            );
        }

        /// <summary>
        /// Bounds of everything the root renders, expressed in the root's own local space.
        ///
        /// The mesh bounds are axis-aligned in each renderer's space, so all eight corners are
        /// transformed and re-enclosed rather than transforming centre and size — that keeps it
        /// exact for the -90° X axis conversion the FBX root carries, instead of hard-coding
        /// which axis ended up where.
        /// </summary>
        internal static bool TryComputeLocalBounds(GameObject root, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!TryGetMesh(renderer, out Mesh mesh))
                    continue;

                Matrix4x4 relative = root.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                Bounds meshBounds = mesh.bounds;
                Vector3 c = meshBounds.center;
                Vector3 e = meshBounds.extents;

                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) == 0 ? c.x - e.x : c.x + e.x,
                        (i & 2) == 0 ? c.y - e.y : c.y + e.y,
                        (i & 4) == 0 ? c.z - e.z : c.z + e.z
                    );

                    Vector3 p = relative.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        bounds = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(p);
                    }
                }
            }

            return any;
        }

        /// <summary>Skinned or not, in one place — the verifier needs the same reading as the builder.</summary>
        internal static bool TryGetMesh(Renderer renderer, out Mesh mesh)
        {
            mesh = null;

            if (renderer is SkinnedMeshRenderer skinned)
                mesh = skinned.sharedMesh;
            else
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null)
                    mesh = filter.sharedMesh;
            }

            return mesh != null;
        }

        // ------------------------------------------------------------------ //
        internal static IEnumerable<Transform> Walk(Transform root)
        {
            yield return root;
            foreach (Transform child in root)
                foreach (Transform t in Walk(child))
                    yield return t;
        }

        internal static string StripPrefix(string id)
        {
            foreach (string prefix in new[] { "ENM_", "BOS_" })
            {
                if (id.StartsWith(prefix, StringComparison.Ordinal))
                    return id.Substring(prefix.Length);
            }
            return id;
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
