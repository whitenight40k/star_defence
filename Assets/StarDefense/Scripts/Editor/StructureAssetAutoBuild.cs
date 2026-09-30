using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// One-shot fallback for environments where batchmode cannot run (see
    /// PlayerAssetAutoBuild for the two reproduced blockers on this Unity China build).
    ///
    /// The first time the project is opened normally, this builds the building /
    /// obstacle assets and writes Logs/StructureAssetsAutoBuild.txt.
    ///
    /// It runs at most once. Delete Assets/StarDefense/Art/.structure_assets_built to
    /// arm it again, or use the Star Defense menu entries directly.
    /// </summary>
    [InitializeOnLoad]
    public static class StructureAssetAutoBuild
    {
        private const string MarkerPath = "Assets/StarDefense/Art/.structure_assets_built";

        static StructureAssetAutoBuild()
        {
            EditorApplication.delayCall += TryBuild;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// 进入播放模式会触发域重载，本钩子会跟着再跑一遍。播放期间做 FBX 重导入
        /// 会打断正在运行的场景，所以推迟到退出播放后再跑。
        /// </summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += TryBuild;
        }

        private static void TryBuild()
        {
            if (Application.isBatchMode)
                return;

            if (EditorApplication.isCompiling)
            {
                EditorApplication.delayCall += TryBuild;
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            if (File.Exists(MarkerPath))
                return;

            var report = new StringBuilder();
            report.AppendLine("Star Defense - structure asset auto build");
            report.AppendLine("when: " + DateTime.Now.ToString("u"));
            report.AppendLine();

            var outcome = "OK";
            try
            {
                StructureAssetBuilder.BuildStructureAssets();
                StructureAssetVerifier.Verify();
                report.AppendLine("StructureAssetBuilder completed without throwing.");
            }
            catch (Exception e)
            {
                outcome = "FAILED";
                report.AppendLine("StructureAssetBuilder threw:");
                report.AppendLine(e.ToString());
                Debug.LogError("[StructureAssetAutoBuild] build failed: " + e);
            }

            report.AppendLine();
            report.AppendLine("outcome: " + outcome);
            report.AppendLine();
            report.AppendLine("Full builder log is in the Console / Editor.log.");
            report.AppendLine("Verification dump: Logs/StructureAssetsVerify.txt");
            report.AppendLine("Re-arm: delete " + MarkerPath);

            try
            {
                var root = Path.GetDirectoryName(Application.dataPath);
                var logDir = Path.Combine(root ?? ".", "Logs");
                Directory.CreateDirectory(logDir);
                File.WriteAllText(Path.Combine(logDir, "StructureAssetsAutoBuild.txt"), report.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[StructureAssetAutoBuild] could not write report: " + e.Message);
            }

            // 只在成功时写标记 —— 失败也写的话，修好代码后这个钩子就永远不会再试。
            if (outcome == "OK")
            {
                try
                {
                    File.WriteAllText(MarkerPath, "outcome=OK at " + DateTime.Now.ToString("u") + "\n");
                    AssetDatabase.ImportAsset(MarkerPath);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[StructureAssetAutoBuild] could not write marker: " + e.Message);
                }

                Debug.Log("[StructureAssetAutoBuild] structure assets built. "
                          + "See Logs/StructureAssetsAutoBuild.txt");
            }
            else
            {
                Debug.LogWarning("[StructureAssetAutoBuild] 本次未成功，未写标记；下次脚本重载会自动重试。");
            }
        }
    }
}
