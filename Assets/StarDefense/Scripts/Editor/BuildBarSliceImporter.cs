using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// 建造栏 UI 贴图的导入设置（九宫格 border / 无压缩 / Clamp / 限尺寸）。
    ///
    /// 为什么必须脚本化：10 张切图的 border 和 15 张图标的 maxSize 都在 Inspector 里，
    /// 手点一遍必然漏；而 border 填错的失败形态是"看起来正常，只是四角被拉扁"，
    /// 不报错、不上当铺，等美术发现时已经铺了一整屏。
    ///
    /// 沿用本工程既有约定（见 StructureAssetAutoBuild）：
    ///   菜单项 "Star Defense/Import Build Bar UI Textures" + [InitializeOnLoad] 标记文件，
    ///   正常开工程时自动跑一次。
    /// <b>失败不写标记</b> —— 失败也写的话，改好设置后这个钩子就永远不会再试。
    ///
    /// 跑完设好 border 后，若仍走 uGUI，Image 直接 type = Sliced 即可；
    /// 若走当前的 IMGUI，用 BuildBarUI.SlicedBox(tex, border) —— 不要再手填一次 border 数值。
    /// </summary>
    [InitializeOnLoad]
    public static class BuildBarSliceImporter
    {
        private const string MarkerPath = "Assets/StarDefense/Art/.build_bar_ui_imported";
        private const string LogPath = "Logs/BuildBarUIImport.txt";

        private const string SliceRoot = "Assets/StarDefense/Art/Textures/UI/BuildBar";
        private const string IconRoot = "Assets/StarDefense/Art/Textures/UI/Buildings";

        /// <summary>容器/槽位/遮罩 的组织切图 border（64×64）。</summary>
        private const int BorderBox = 16;

        /// <summary>标签/进度条的组织切图 border（24×16）。</summary>
        private const int BorderPill = 6;

        /// <summary>
        /// uGUI 的 9 宫格把 sprite 的 border 按 (referencePixelsPerUnit / pixelsPerUnit) 缩放。
        /// 保持 Unity 默认 100，border 就按像素 1:1 铺；改成 1 会让 16px 描角涨成 1600px。
        /// </summary>
        private const float PixelsPerUnit = 100f;

        static BuildBarSliceImporter()
        {
            EditorApplication.delayCall += TryImport;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// 进出播放模式都会触发域重载，本钩子会跟着再跑。播放期间重导入贴图会打断场景，
        /// 所以推迟到退出播放后再跑。
        /// </summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += TryImport;
        }

        [MenuItem("Star Defense/Import Build Bar UI Textures", false, 40)]
        public static void ImportFromMenu()
        {
            string report = Apply();
            Debug.Log("[BuildBarSliceImporter]\n" + report);
        }

        /// <summary>
        /// 供管线在重建场景**之前**调用：保证切图的导入设置已经生效。
        ///
        /// 为什么这件事不能只交给 [InitializeOnLoad] 的自动钩子：两者都挂在启动的
        /// delayCall 上，谁先跑是不确定的。而顺序错了的后果是隐性的 ——
        /// 场景构建器会把"按默认设置导入的贴图"引用进场景，等切图设置随后补上，
        /// 引用本身没断，但 maxTextureSize / 压缩方式已经不是场景构建时那一份了。
        /// 由管线显式调一次，顺序就确定了。
        /// </summary>
        /// <returns>一句写进管线日志的摘要。</returns>
        internal static string EnsureImported()
        {
            if (File.Exists(MarkerPath))
                return "跳过（" + MarkerPath + " 已存在）";

            string report = Apply();
            WriteReport(report);

            int failed = ParseFailed(report);
            if (failed != 0 || report.IndexOf("MISSING", StringComparison.Ordinal) >= 0)
                return $"有失败项（failed={failed}），未写标记；详见 {LogPath}";

            WriteMarker();

            int slices = CountLines(report, SliceRoot);
            int icons = CountLines(report, IconRoot);
            return $"切图 {slices} 张 / 图标 {icons} 张";
        }

        /// <summary>数某个目录下有几行 OK。</summary>
        private static int CountLines(string report, string folder)
        {
            int start = report.IndexOf(folder, StringComparison.Ordinal);
            if (start < 0)
                return 0;

            int end = report.IndexOf("\n\n", start, StringComparison.Ordinal);
            string block = end < 0 ? report.Substring(start) : report.Substring(start, end - start);

            int count = 0;
            foreach (string line in block.Split('\n'))
            {
                if (line.StartsWith("OK", StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        private static void WriteMarker()
        {
            try
            {
                File.WriteAllText(MarkerPath, "failed=0 at " + DateTime.Now.ToString("u") + "\n");
                AssetDatabase.ImportAsset(MarkerPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BuildBarSliceImporter] could not write marker: " + e.Message);
            }
        }

        private static void TryImport()
        {
            if (Application.isBatchMode)
                return;

            if (EditorApplication.isCompiling)
            {
                EditorApplication.delayCall += TryImport;
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            if (File.Exists(MarkerPath))
                return;

            string report = Apply();
            int failed = ParseFailed(report);

            WriteReport(report);

            if (failed == 0 && report.IndexOf("MISSING", StringComparison.Ordinal) < 0)
            {
                WriteMarker();
                Debug.Log("[BuildBarSliceImporter] build bar UI textures imported. See " + LogPath);
            }
            else
            {
                Debug.LogWarning("[BuildBarSliceImporter] 本次有失败项，未写标记；"
                                 + "下次脚本重载会自动重试。详见 " + LogPath);
            }
        }

        private static string Apply()
        {
            var log = new StringBuilder();
            log.AppendLine("Star Defense - build bar UI texture import");
            log.AppendLine("when: " + DateTime.Now.ToString("u"));
            log.AppendLine();

            int failed = 0;
            failed += ImportFolder(log, SliceRoot, true);
            failed += ImportFolder(log, IconRoot, false);

            log.AppendLine();
            log.AppendLine("failed: " + failed);
            log.AppendLine("Re-arm: delete " + MarkerPath + " and reopen the project.");
            return log.ToString();
        }

        private static int ImportFolder(StringBuilder log, string folder, bool isSlice)
        {
            log.AppendLine("── " + folder + (isSlice ? "   [九宫格切图]" : "   [建筑图标]"));
            if (!AssetDatabase.IsValidFolder(folder))
            {
                log.AppendLine("MISSING  " + folder);
                return 1;
            }

            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
            if (guids.Length == 0)
            {
                log.AppendLine("EMPTY    " + folder);
                return 1;
            }

            int failed = 0;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    failed++;
                    log.AppendLine("SKIP     " + path + "  (not a TextureImporter)");
                    continue;
                }

                try
                {
                    if (isSlice)
                        ConfigureSlice(importer, path);
                    else
                        ConfigureIcon(importer);

                    importer.SaveAndReimport();
                    log.AppendLine("OK       " + path
                                   + (isSlice ? BorderNote(path) : ""));
                }
                catch (Exception e)
                {
                    failed++;
                    log.AppendLine("FAILED   " + path + "  " + e.Message);
                }
            }

            log.AppendLine();
            return failed;
        }

        private static void ConfigureSlice(TextureImporter importer, string path)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            // Vector4 的顺序是 (left, bottom, right, top) —— 不是 left/top/right/bottom。
            importer.spriteBorder = BorderFor(path);
            importer.spritePixelsPerUnit = PixelsPerUnit;

            importer.filterMode = FilterMode.Bilinear;
            importer.mipmapEnabled = false;      // 切图基本 1:1 绘制，mipmap 白占显存
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 128;       // 原图 64×64 / 24×16，留一档余量
            // 平色 + 1px 硬描边是最怕压缩的内容，压完描边会起斑。
            // 单张 64×64 未压缩才 16 KB，不值当省。
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.crunchedCompression = false;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.isReadable = false;
        }

        private static void ConfigureIcon(TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = PixelsPerUnit;

            importer.filterMode = FilterMode.Bilinear;
            // 母版是 958×958，实际显示在 72 / 128 —— 有 mipmap 缩小才不闪。
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.npotScale = TextureImporterNPOTScale.None;
            // 导入即限到 256：母版留着是为改图，运行期不需要 958 的分辨率。
            // 15 张 958 不压是 ~55 MB 显存，限到 256 只剩零头。
            importer.maxTextureSize = 256;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.crunchedCompression = false;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.isReadable = false;
        }

        /// <summary>
        /// 按文件名给 border。切图是程序化生成的，名字即规范，别在别处再维护一张表。
        /// </summary>
        private static Vector4 BorderFor(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("UI_BLD_Slice_")
                && (name.Contains("Panel") || name.Contains("Slot") || name.Contains("Mask")))
            {
                return new Vector4(BorderBox, BorderBox, BorderBox, BorderBox);
            }

            if (name == "UI_BLD_Slice_Tag" || name == "UI_BLD_Slice_Bar")
                return new Vector4(BorderPill, BorderPill, BorderPill, BorderPill);

            // UI_BLD_White_4：纯色块，拉满即可，不需要九宫格。
            return Vector4.zero;
        }

        private static string BorderNote(string path)
        {
            Vector4 b = BorderFor(path);
            if (b == Vector4.zero)
                return "   border 0（纯色块）";
            return "   border " + (int)b.x;
        }

        private static int ParseFailed(string report)
        {
            const string key = "failed: ";
            int i = report.LastIndexOf(key, StringComparison.Ordinal);
            if (i < 0)
                return -1;

            i += key.Length;
            int j = i;
            while (j < report.Length && char.IsDigit(report[j]))
                j++;

            int value;
            return int.TryParse(report.Substring(i, j - i), out value) ? value : -1;
        }

        private static void WriteReport(string report)
        {
            try
            {
                string root = Path.GetDirectoryName(Application.dataPath);
                string dir = Path.Combine(root ?? ".", "Logs");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(root ?? ".", LogPath), report);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BuildBarSliceImporter] could not write report: " + e.Message);
            }
        }
    }
}
