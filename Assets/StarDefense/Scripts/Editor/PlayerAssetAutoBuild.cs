using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// One-shot fallback for environments where batchmode cannot run.
    ///
    /// Editor-side batchmode is gated behind the "com.unity.editor.headless" entitlement
    /// on this Unity China build, and running without -nographics wedges the editor at
    /// "Application.AssetDatabase Initial Refresh Start". So instead: the first time the
    /// project is opened normally, this builds the player assets and writes an audit to
    /// Logs/PlayerAssetsAutoBuild.txt.
    ///
    /// It runs at most once. Delete Assets/StarDefense/Art/.player_assets_built to arm it
    /// again, or just use the Star Defense menu entries directly.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayerAssetAutoBuild
    {
        private const string MarkerPath = "Assets/StarDefense/Art/.player_assets_built";

        static PlayerAssetAutoBuild()
        {
            EditorApplication.delayCall += TryBuild;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// 进入播放模式会触发域重载，本钩子会跟着再跑一遍。播放期间做 FBX 重导入与剪辑切分
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
            report.AppendLine("Star Defense - player asset auto build");
            report.AppendLine("when: " + DateTime.Now.ToString("u"));
            report.AppendLine();

            var outcome = "OK";
            try
            {
                PlayerAssetBuilder.BuildPlayerAssets();
                PlayerAssetVerifier.Verify();
                report.AppendLine("PlayerAssetBuilder completed without throwing.");
            }
            catch (Exception e)
            {
                outcome = "FAILED";
                report.AppendLine("PlayerAssetBuilder threw:");
                report.AppendLine(e.ToString());
                Debug.LogError("[PlayerAssetAutoBuild] build failed: " + e);
            }

            report.AppendLine();
            report.AppendLine("outcome: " + outcome);
            report.AppendLine();
            report.AppendLine("Full builder log is in the Console / Editor.log.");
            report.AppendLine("Verification dump: Logs/PlayerAssetsVerify.txt");
            report.AppendLine("Re-arm: delete " + MarkerPath);

            try
            {
                var root = Path.GetDirectoryName(Application.dataPath);
                var logDir = Path.Combine(root ?? ".", "Logs");
                Directory.CreateDirectory(logDir);
                File.WriteAllText(Path.Combine(logDir, "PlayerAssetsAutoBuild.txt"), report.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PlayerAssetAutoBuild] could not write report: " + e.Message);
            }

            // 只在成功时写标记。失败也写的话，标记里那句 outcome=FAILED 会让这个钩子
            // 之后再也不会重试 —— 表现就是"改了代码却像没改"，只能靠手工删文件恢复。
            if (outcome == "OK")
            {
                try
                {
                    File.WriteAllText(MarkerPath, "outcome=OK at " + DateTime.Now.ToString("u") + "\n");
                    AssetDatabase.ImportAsset(MarkerPath);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[PlayerAssetAutoBuild] could not write marker: " + e.Message);
                }

                Debug.Log("[PlayerAssetAutoBuild] player assets built. See Logs/PlayerAssetsAutoBuild.txt");
            }
            else
            {
                Debug.LogWarning("[PlayerAssetAutoBuild] 本次未成功，未写标记；下次脚本重载会自动重试。");
            }
        }
    }
}
