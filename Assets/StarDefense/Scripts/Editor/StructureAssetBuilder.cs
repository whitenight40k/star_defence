using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// Turns the exported building / obstacle FBX into game-ready Unity assets.
    ///
    /// Scope: texture and model import settings, the shared structure material, and
    /// one prefab per asset with an explicit MeshCollider. It does not touch gameplay
    /// scripts, and it does not place anything in the scene.
    ///
    /// The whole kit shares ONE material (<c>MAT_Structure_Atlas</c>) because every
    /// part samples the same 2048² atlas, and the emissive tiles are lit through an
    /// emission mask rather than a second material. That keeps draw calls down and
    /// means a new building needs no new material — only new UVs.
    ///
    /// Run from the menu (Star Defense / Build Structure Assets) or headless:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt;
    ///             -executeMethod StarDefense.EditorTools.StructureAssetBuilder.BuildStructureAssets
    /// </summary>
    public static class StructureAssetBuilder
    {
        private const string ArtRoot = "Assets/StarDefense/Art";
        private const string MatDir = ArtRoot + "/Materials";
        private const string PrefabDir = ArtRoot + "/Prefabs";
        private const string BldDir = ArtRoot + "/Models/Buildings";
        private const string EnvDir = ArtRoot + "/Models/Environment";
        private const string TexDir = ArtRoot + "/Textures/Buildings";

        private const string AtlasTexPath = TexDir + "/TEX_Structure_Atlas_A.png";
        private const string EmissionTexPath = TexDir + "/TEX_Structure_Atlas_A_Emission.png";
        private const string AtlasMatName = "MAT_Structure_Atlas";
        private const string AtlasMatPath = MatDir + "/MAT_Structure_Atlas.mat";

        /// <summary>Albedo roughness authored in bld_lib.py.</summary>
        private const float AtlasRoughness = 0.74f;

        /// <summary>Matches the Emission Strength used in the Blender preview render.</summary>
        private const float EmissionBoost = 2.0f;

        internal static readonly string[] BuildingIds =
        {
            "BLD_Core_A",
            "BLD_MachineGunTurret_A",
            "BLD_CannonTurret_A",
            "BLD_TeslaTower_A",
            "BLD_EnergyWall_A",
            "BLD_EnergyWall_Node_A",
        };

        internal static readonly string[] EnvironmentIds =
        {
            "ENV_Rock_A",
            "ENV_Rock_B",
            "ENV_Crystal_A",
            "PRP_SupplyCrate_A",
            "PRP_Barricade_A",
        };

        /// <summary>
        /// Flat emissive materials for parts that are not atlas-mapped (small beacons
        /// and antenna tips). Everything else glows through the shared emission mask.
        /// </summary>
        private static readonly MaterialSpec[] GlowMaterials =
        {
            new MaterialSpec("MAT_Glow_Amber", new Color(0.950f, 0.480f, 0.060f), 0.42f,
                new Color(1.000f, 0.550f, 0.100f), 3.0f),
            new MaterialSpec("MAT_Tech_Teal", new Color(0.050f, 0.520f, 0.550f), 0.30f,
                new Color(0.100f, 0.850f, 0.900f), 2.5f),
            new MaterialSpec("MAT_Glow_White", new Color(0.900f, 0.920f, 0.950f), 0.40f,
                Color.white, 2.0f),
            new MaterialSpec("MAT_Alien_Green", new Color(0.080f, 0.440f, 0.160f), 0.45f,
                new Color(0.300f, 1.000f, 0.420f), 2.6f),
        };

        private readonly struct MaterialSpec
        {
            public readonly string Name;
            public readonly Color BaseColor;
            public readonly float Roughness;
            public readonly Color Emission;
            public readonly float EmissionStrength;

            public MaterialSpec(string name, Color baseColor, float roughness,
                Color emission, float emissionStrength)
            {
                Name = name;
                BaseColor = baseColor;
                Roughness = roughness;
                Emission = emission;
                EmissionStrength = emissionStrength;
            }
        }

        private static readonly List<string> Log = new List<string>();

        // ------------------------------------------------------------------ //
        [MenuItem("Star Defense/Build Structure Assets")]
        public static void BuildStructureAssets()
        {
            Log.Clear();
            try
            {
                EnsureFolder(MatDir);
                EnsureFolder(PrefabDir);
                EnsureFolder(TexDir);

                ConfigureTextureImporters();
                ConfigureModelImporters();
                RemapAll();
                BuildPrefabs();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                FlushLog("BUILD OK");
            }
            catch (Exception e)
            {
                FlushLog("BUILD FAILED");
                Debug.LogError("[StructureAssetBuilder] " + e);
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
                throw;
            }
        }

        private static void FlushLog(string headline)
        {
            Debug.Log("[StructureAssetBuilder] ===== " + headline + " =====");
            foreach (var line in Log)
                Debug.Log("[StructureAssetBuilder] " + line);
        }

        private static void Note(string line) => Log.Add(line);

        // ------------------------------------------------------------------ //
        // 1) textures
        // ------------------------------------------------------------------ //

        private static void ConfigureTextureImporters()
        {
            ConfigureTexture(AtlasTexPath, "structure atlas (albedo)");
            ConfigureTexture(EmissionTexPath, "structure atlas (emission mask)");
        }

        private static void ConfigureTexture(string path, string label)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null)
                throw new InvalidOperationException("No TextureImporter at " + path);

            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true; // authored as sRGB values by bld_tex.py
            ti.alphaSource = TextureImporterAlphaSource.None;
            ti.alphaIsTransparency = false;
            ti.mipmapEnabled = true;
            ti.filterMode = FilterMode.Bilinear;
            ti.wrapMode = TextureWrapMode.Clamp; // UVs never leave their tile
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.maxTextureSize = 2048;
            // Flat colour blocks are exactly what block compression is worst at, so
            // ask for the high-quality variant rather than the default DXT1.
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();

            Note($"texture configured: {path} ({label})");
        }

        // ------------------------------------------------------------------ //
        // 2) models
        // ------------------------------------------------------------------ //

        private static void ConfigureModelImporters()
        {
            foreach (var id in BuildingIds)
                ConfigureModel(BldDir + "/" + id + "_v1.fbx");
            foreach (var id in EnvironmentIds)
                ConfigureModel(EnvDir + "/" + id + "_v1.fbx");
        }

        private static void ConfigureModel(string path)
        {
            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            if (imp == null)
                throw new InvalidOperationException("No ModelImporter at " + path);

            imp.animationType = ModelImporterAnimationType.None;
            imp.importAnimation = false;
            imp.importBlendShapes = false;
            imp.importCameras = false;
            imp.importLights = false;
            imp.addCollider = false; // prefabs author their own MeshCollider
            imp.isReadable = true;   // MeshCollider and later procedural work
            imp.meshCompression = ModelImporterMeshCompression.Off;
            imp.weldVertices = false; // keep the authored hard edges on the low-poly silhouettes
            imp.importNormals = ModelImporterNormals.Import;
            imp.importTangents = ModelImporterTangents.None; // no normal maps in this kit
            // importMaterials (bool) was removed in 2022.x; materialImportMode is the
            // replacement and ImportStandard is equivalent to the old `true`.
            imp.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            imp.materialLocation = ModelImporterMaterialLocation.InPrefab;
            imp.materialName = ModelImporterMaterialName.BasedOnMaterialName;
            imp.materialSearch = ModelImporterMaterialSearch.Local;
            imp.SaveAndReimport();

            Note("model import configured: " + path);
        }

        // ------------------------------------------------------------------ //
        // 3) materials
        // ------------------------------------------------------------------ //

        private static void RemapAll()
        {
            var atlas = BuildAtlasMaterial();

            // MAT_Glow_Amber belongs to this builder; the rest may already have been
            // created by the player pass, in which case we leave them alone.
            foreach (var spec in GlowMaterials)
            {
                var owned = spec.Name == "MAT_Glow_Amber";
                EnsureMaterial(spec, overwrite: owned);
            }

            var map = new Dictionary<string, Material> { [AtlasMatName] = atlas };
            foreach (var spec in GlowMaterials)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/" + spec.Name + ".mat");
                if (mat != null)
                    map[spec.Name] = mat;
            }

            AssetDatabase.SaveAssets();

            foreach (var id in BuildingIds)
                RemapFbx(BldDir + "/" + id + "_v1.fbx", map);
            foreach (var id in EnvironmentIds)
                RemapFbx(EnvDir + "/" + id + "_v1.fbx", map);
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
            Note($"material written: {AtlasMatPath} (albedo + emission map shared by the whole kit)");
            return mat;
        }

        private static void EnsureMaterial(MaterialSpec spec, bool overwrite)
        {
            var path = MatDir + "/" + spec.Name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(mat, path);
                overwrite = true;
            }

            if (!overwrite)
            {
                Note("material already present, left as is: " + spec.Name);
                return;
            }

            mat.shader = Shader.Find("Standard");
            mat.color = spec.BaseColor;
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Glossiness", 1f - spec.Roughness);

            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            mat.SetColor("_EmissionColor", spec.Emission * spec.EmissionStrength);

            EditorUtility.SetDirty(mat);
            Note("material written: " + spec.Name);
        }

        private static void RemapFbx(string fbxPath, Dictionary<string, Material> map)
        {
            var imp = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (imp == null)
                throw new InvalidOperationException("No ModelImporter at " + fbxPath);

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

            var unmapped = new List<string>();
            foreach (var name in sourceNames)
            {
                if (!map.TryGetValue(name, out var target))
                {
                    unmapped.Add(name);
                    continue;
                }

                imp.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), name), target);
            }

            if (unmapped.Count > 0)
                throw new InvalidOperationException(
                    $"{fbxPath} uses materials this builder does not know: {string.Join(", ", unmapped)}"
                );

            imp.SaveAndReimport();
            Note($"remapped {sourceNames.Count} slot(s) on {System.IO.Path.GetFileName(fbxPath)}: "
                 + string.Join(", ", sourceNames));
        }

        // ------------------------------------------------------------------ //
        // 4) prefabs
        // ------------------------------------------------------------------ //

        private static void BuildPrefabs()
        {
            foreach (var id in BuildingIds)
                BuildPrefab(id, BldDir);
            foreach (var id in EnvironmentIds)
                BuildPrefab(id, EnvDir);
        }

        private static GameObject BuildPrefab(string id, string folder)
        {
            var fbx = folder + "/" + id + "_v1.fbx";
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            if (model == null)
                throw new InvalidOperationException("Missing model for prefab: " + fbx);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            instance.name = "PFB_" + ShortName(id);

            foreach (var c in instance.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.DestroyImmediate(c);

            var colliders = 0;
            foreach (var mf in instance.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null)
                    continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false; // static scenery: an exact hull is cheaper than a bad approximation
                colliders++;
            }

            // Turrets are split into a static base and a rotating head. Only the head
            // may move, so only the rest of the hierarchy gets static batching flags -
            // marking the head static would bake it in place and break rotation.
            var movable = instance
                .GetComponentsInChildren<MeshFilter>(true)
                .Any(mf => mf.name.Contains("_Head"));

            // Buildings are instantiated at runtime by StarDefenseGame.CreateBuilding and then
            // repositioned. Unity does not support moving a statically batched object: the batch
            // is built around the original transform, so the instance can render at a stale
            // position or drop out of the batch entirely. Only scene-placed environment props
            // may carry static flags.
            var runtimeSpawned = folder == BldDir;

            try
            {
                foreach (var t in Walk(instance.transform))
                {
                    var isMovable = movable && t.name.Contains("_Head");
                    var noStaticFlags = isMovable || runtimeSpawned;
                    GameObjectUtility.SetStaticEditorFlags(
                        t.gameObject,
                        noStaticFlags
                            ? 0
                            : StaticEditorFlags.BatchingStatic
                                | StaticEditorFlags.OccludeeStatic
                                | StaticEditorFlags.ContributeGI
                                | StaticEditorFlags.ReflectionProbeStatic
                    );
                }
            }
            catch (Exception e)
            {
                Note($"  note: could not set static flags on {id} ({e.GetType().Name})");
            }

            var socketNames = Walk(instance.transform)
                .Select(t => t.name)
                .Where(n => n.StartsWith("Socket_", StringComparison.Ordinal))
                .ToList();

            var prefabPath = PrefabDir + "/PFB_" + ShortName(id) + ".prefab";
            var saved = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            UnityEngine.Object.DestroyImmediate(instance);

            Note(
                $"prefab written: {prefabPath} ({colliders} MeshCollider(s)"
                + (socketNames.Count > 0 ? ", sockets: " + string.Join(", ", socketNames) : "")
                + (movable ? ", head is movable" : "")
                + ")"
            );
            return saved;
        }

        internal static string ShortName(string id)
        {
            foreach (var prefix in new[] { "BLD_", "ENV_", "PRP_" })
            {
                if (id.StartsWith(prefix, StringComparison.Ordinal))
                    return id.Substring(prefix.Length);
            }
            return id;
        }

        // ------------------------------------------------------------------ //
        private static IEnumerable<Transform> Walk(Transform root)
        {
            yield return root;
            foreach (Transform child in root)
                foreach (var t in Walk(child))
                    yield return t;
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
