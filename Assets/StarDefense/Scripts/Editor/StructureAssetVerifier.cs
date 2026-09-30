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
    /// Read-only audit of the building / obstacle assets. Changes nothing; writes
    /// Logs/StructureAssetsVerify.txt.
    ///
    /// Menu: Star Defense / Verify Structure Assets
    /// </summary>
    public static class StructureAssetVerifier
    {
        private const string ArtRoot = "Assets/StarDefense/Art";
        private const string MatDir = ArtRoot + "/Materials";
        private const string PrefabDir = ArtRoot + "/Prefabs";
        private const string BldDir = ArtRoot + "/Models/Buildings";
        private const string EnvDir = ArtRoot + "/Models/Environment";
        private const string TexDir = ArtRoot + "/Textures/Buildings";

        private const string AtlasTexPath = TexDir + "/TEX_Structure_Atlas_A.png";
        private const string EmissionTexPath = TexDir + "/TEX_Structure_Atlas_A_Emission.png";
        private const string AtlasMatPath = MatDir + "/MAT_Structure_Atlas.mat";

        [MenuItem("Star Defense/Verify Structure Assets")]
        public static void Verify()
        {
            var sb = new StringBuilder();
            var problems = new List<string>();

            sb.AppendLine("Star Defense - structure asset verification");
            sb.AppendLine("when: " + DateTime.Now.ToString("u"));
            sb.AppendLine();

            sb.AppendLine("== textures ==");
            CheckTexture(sb, problems, AtlasTexPath);
            CheckTexture(sb, problems, EmissionTexPath);
            sb.AppendLine();

            sb.AppendLine("== shared material ==");
            var atlas = AssetDatabase.LoadAssetAtPath<Material>(AtlasMatPath);
            if (atlas == null)
            {
                problems.Add("missing material " + AtlasMatPath);
                sb.AppendLine("  MISSING " + AtlasMatPath);
            }
            else
            {
                var main = atlas.GetTexture("_MainTex") as Texture2D;
                var emiMap = atlas.GetTexture("_EmissionMap") as Texture2D;
                var emiCol = atlas.GetColor("_EmissionColor");
                var gloss = atlas.GetFloat("_Glossiness");

                sb.AppendLine($"  {atlas.name}  shader={atlas.shader.name}");
                sb.AppendLine($"    _MainTex      = {(main != null ? main.name : "null")}");
                sb.AppendLine($"    _EmissionMap  = {(emiMap != null ? emiMap.name : "null")}");
                sb.AppendLine($"    _EmissionColor= {emiCol}  _Glossiness={gloss:F3}");
                sb.AppendLine($"    keyword _EMISSION = {atlas.IsKeywordEnabled("_EMISSION")}");

                if (main == null)
                    problems.Add("MAT_Structure_Atlas has no _MainTex");
                if (emiMap == null)
                    problems.Add("MAT_Structure_Atlas has no _EmissionMap (no part would glow)");
                if (emiCol.maxColorComponent <= 0.01f)
                    problems.Add("MAT_Structure_Atlas _EmissionColor is black - emission disabled");
                if (!atlas.IsKeywordEnabled("_EMISSION"))
                    problems.Add("MAT_Structure_Atlas does not have the _EMISSION keyword");
            }
            sb.AppendLine();

            var all = StructureAssetBuilder.BuildingIds
                .Select(id => (Id: id, Dir: BldDir))
                .Concat(StructureAssetBuilder.EnvironmentIds.Select(id => (Id: id, Dir: EnvDir)))
                .ToList();

            var totalTris = 0;
            sb.AppendLine("== models ==");
            foreach (var (id, dir) in all)
            {
                var fbx = dir + "/" + id + "_v1.fbx";
                var imp = AssetImporter.GetAtPath(fbx) as ModelImporter;
                if (imp == null)
                {
                    problems.Add("missing or unimported FBX: " + fbx);
                    sb.AppendLine("  MISSING " + fbx);
                    continue;
                }

                var meshes = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Mesh>().ToList();
                var tris = meshes.Sum(m => m.triangles.Length / 3);
                totalTris += tris;

                sb.AppendLine(
                    $"  {id}  meshes={meshes.Count} tris={tris} "
                    + $"anim={imp.animationType} readable={imp.isReadable} "
                    + $"addCollider={imp.addCollider} weld={imp.weldVertices} "
                    + $"compression={imp.meshCompression}"
                );
                foreach (var m in meshes)
                    sb.AppendLine($"      {m.name}: {m.triangles.Length / 3} tris, "
                                  + $"{m.subMeshCount} submesh, uv={m.uv.Length > 0}");

                if (imp.animationType != ModelImporterAnimationType.None)
                    problems.Add(id + ": animationType should be None for a static structure");
                if (imp.addCollider)
                    problems.Add(id + ": importer colliders are on; the prefab authors its own");
                if (!imp.isReadable)
                    problems.Add(id + ": mesh not readable - MeshCollider setup may fail");
                if (imp.meshCompression != ModelImporterMeshCompression.Off)
                    problems.Add(id + ": mesh compression should be Off for low-poly silhouettes");
                if (meshes.Any(m => m.uv.Length == 0))
                    problems.Add(id + ": a mesh has no UVs - it cannot sample the atlas");
                if (meshes.Count > 2)
                    problems.Add(id + ": unexpected mesh count " + meshes.Count);
            }
            sb.AppendLine();

            sb.AppendLine("== prefabs ==");
            foreach (var (id, _) in all)
            {
                var prefabPath = PrefabDir + "/PFB_" + StructureAssetBuilder.ShortName(id) + ".prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    problems.Add("missing prefab " + prefabPath);
                    sb.AppendLine("  MISSING " + prefabPath);
                    continue;
                }

                var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
                var colliders = prefab.GetComponentsInChildren<MeshCollider>(true);
                var sockets = prefab.GetComponentsInChildren<Transform>(true)
                    .Select(t => t.name)
                    .Where(n => n.StartsWith("Socket_", StringComparison.Ordinal))
                    .ToList();
                var materials = prefab.GetComponentsInChildren<Renderer>(true)
                    .SelectMany(r => r.sharedMaterials)
                    .Where(m => m != null)
                    .Select(m => m.name)
                    .Distinct()
                    .ToList();

                sb.AppendLine(
                    $"  {prefab.name}  meshFilters={filters.Length} "
                    + $"meshColliders={colliders.Length} children={prefab.transform.childCount}"
                );
                sb.AppendLine("      materials: " + (materials.Count > 0 ? string.Join(", ", materials) : "NONE"));
                if (filters.Length > 1)
                    sb.AppendLine("      movable parts: "
                                  + string.Join(", ", filters.Where(f => f.name.Contains("_Head")).Select(f => f.name)));
                if (sockets.Count > 0)
                    sb.AppendLine("      sockets: " + string.Join(", ", sockets));

                if (colliders.Length != filters.Length)
                    problems.Add(
                        $"{prefab.name}: {filters.Length} mesh(es) but {colliders.Length} MeshCollider(s)"
                    );
                if (materials.Count == 0)
                    problems.Add(prefab.name + ": no materials assigned");
                foreach (var m in materials)
                {
                    if (m != "MAT_Structure_Atlas" && !m.StartsWith("MAT_Glow_") && !m.StartsWith("MAT_Tech_")
                        && !m.StartsWith("MAT_Alien_"))
                        problems.Add($"{prefab.name}: unexpected material {m}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("== summary ==");
            sb.AppendLine($"assets: {all.Count}");
            sb.AppendLine($"total triangles: {totalTris}");
            sb.AppendLine($"problems: {problems.Count}");
            foreach (var p in problems)
                sb.AppendLine("  ! " + p);

            var root = Path.GetDirectoryName(Application.dataPath);
            var logDir = Path.Combine(root ?? ".", "Logs");
            Directory.CreateDirectory(logDir);
            var outPath = Path.Combine(logDir, "StructureAssetsVerify.txt");
            File.WriteAllText(outPath, sb.ToString());

            if (problems.Count == 0)
                Debug.Log($"[StructureAssetVerifier] all {all.Count} assets OK. Report: {outPath}");
            else
                Debug.LogWarning(
                    $"[StructureAssetVerifier] {problems.Count} problem(s) across {all.Count} assets. "
                    + "Report: " + outPath
                );
        }

        private static void CheckTexture(StringBuilder sb, List<string> problems, string path)
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
                $"  {tex.name}  {tex.width}x{tex.height}  sRGB={ti.sRGBTexture} "
                + $"wrap={ti.wrapMode} maxSize={ti.maxTextureSize} "
                + $"compression={ti.textureCompression} mipmaps={ti.mipmapEnabled}"
            );

            if (!ti.sRGBTexture)
                problems.Add(path + ": should import as sRGB (values were authored as sRGB)");
            if (ti.wrapMode != TextureWrapMode.Clamp)
                problems.Add(path + ": wrap mode should be Clamp so tiles cannot bleed");
            if (ti.maxTextureSize < 2048)
                problems.Add(path + ": max size clipped below the authored 2048");
            if (!ti.mipmapEnabled)
                problems.Add(path + ": mipmaps are off; distant buildings will alias");
        }
    }
}
