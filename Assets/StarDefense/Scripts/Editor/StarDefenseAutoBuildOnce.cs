using System.IO;
using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// 场景缺失时自动重建一份。
    ///
    /// 它自己**不直接建场景** —— 交给 <see cref="StarDefensePipeline"/>：
    /// 场景里要用真模型，前提是 Prefab 已经生成、并且已经绑到 BuildableConfig 上，
    /// 而"先绑资产后建场景"这个顺序只有管线知道。两个钩子都挂在 delayCall 上、
    /// 谁先跑不确定，若这里直接调 SceneBuilder，抢在绑定之前跑就会把整张地图
    /// 用图元建一遍（表现就是"场景里还是一堆方块和胶囊"），且不报任何错。
    /// </summary>
    [InitializeOnLoad]
    public static class StarDefenseAutoBuildOnce
    {
        private const string ScenePath = "Assets/StarDefense/Scenes/PlanetDefense_FirstVersion.unity";
        private const string MarkerPath = "Assets/StarDefense/Scenes/.first_version_generated";

        static StarDefenseAutoBuildOnce()
        {
            EditorApplication.delayCall += TryBuild;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// 进入播放模式会触发域重载，[InitializeOnLoad] 的静态构造会跟着再跑一遍。
        /// 播放期间不能写资产/建场景（EditorSceneManager.NewScene 在运行期被禁止），
        /// 所以这里什么都不做，等退出播放后补跑。
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

            // 播放模式下不碰资产与场景，退出播放后由 OnPlayModeStateChanged 补跑。
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            // 标记与场景必须同时存在才算"已生成"：只要场景不在，就重跑一次。
            if (File.Exists(MarkerPath) && File.Exists(ScenePath))
                return;

            // 场景不在 = 需要重建。先把管线的完成标记清掉：场景丢失时那个标记会变成
            // "自称已完成、产物却不存在"的矛盾状态，会让 RunPipeline 静默跳过整条链。
            if (!File.Exists(ScenePath))
                StarDefensePipeline.Invalidate();

            StarDefensePipeline.RunPipeline();

            // 只有场景真的落地了才写标记。管线中途失败时不留标记，下次重载可以再试。
            if (!File.Exists(ScenePath))
            {
                Debug.LogWarning("[StarDefenseAutoBuildOnce] 管线未产出场景，不写标记，下次脚本重载会再试。");
                return;
            }

            File.WriteAllText(MarkerPath, "Generated first playable Star Defense prototype.\n");
            AssetDatabase.ImportAsset(MarkerPath);
            Debug.Log("[StarDefenseAutoBuildOnce] 第一版原型已自动生成。后续如需重建，请删除 Assets/StarDefense/Scenes/.first_version_generated 后重新编译，或手动点击 Star Defense/★ 全流程。");
        }
    }
}
