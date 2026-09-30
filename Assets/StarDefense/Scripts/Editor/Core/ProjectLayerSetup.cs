using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// 确保工程里存在某个自定义层。
    ///
    /// 为什么需要代码来做这件事：<see cref="FirstPersonBodyCuller"/> 依赖一个专用层把
    /// "自己的身体"从第一人称相机里排除掉。层名是硬引用，但界面上的层配置存在
    /// <c>ProjectSettings/TagManager.asset</c> 里 —— 换机器、回滚工程、别人 clone 一份，
    /// 这个层就没了，而缺失的后果只是"穿模又回来了"，不会报任何错。
    ///
    /// 层槽位只有 8~31 共 24 个，Unity 不提供运行时新增的 API，所以只能在编辑器侧写 TagManager。
    /// 这里走 SerializedObject 而不是直接改 YAML 文件：工程打开着的时候，直接写文件会被
    /// Unity 的内存副本覆盖回去，而且不会有任何提示。
    /// </summary>
    internal static class ProjectLayerSetup
    {
        private const string TagManagerPath = "ProjectSettings/TagManager.asset";

        /// <summary>Unity 内置层占了 0~7，自定义层从 8 开始。</summary>
        private const int FirstUserLayerIndex = 8;

        /// <summary>
        /// 保证层存在并返回它的索引；没有空槽位时返回 -1（调用方需能接受这个结果）。
        /// 已经存在时是幂等的，不会挪动它原来的位置。
        /// </summary>
        internal static int EnsureLayer(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
                return -1;

            int existing = LayerMask.NameToLayer(layerName);
            if (existing >= 0)
                return existing;

            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(TagManagerPath);
            if (assets == null || assets.Length == 0 || assets[0] == null)
            {
                Debug.LogWarning(
                    $"[ProjectLayerSetup] 读不到 {TagManagerPath}，无法自动创建层 {layerName}。"
                        + "请手动在 Project Settings > Tags and Layers 里添加。");
                return -1;
            }

            var tagManager = new SerializedObject(assets[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");
            if (layers == null || !layers.isArray)
            {
                Debug.LogWarning(
                    $"[ProjectLayerSetup] {TagManagerPath} 里没有 layers 数组，无法自动创建层 {layerName}。");
                return -1;
            }

            for (int i = FirstUserLayerIndex; i < layers.arraySize; i++)
            {
                SerializedProperty slot = layers.GetArrayElementAtIndex(i);
                if (slot == null || !string.IsNullOrEmpty(slot.stringValue))
                    continue;

                slot.stringValue = layerName;
                tagManager.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();

                Debug.Log($"[ProjectLayerSetup] 已占用层槽位 {i} 作为 {layerName}。");
                return i;
            }

            Debug.LogWarning(
                $"[ProjectLayerSetup] 24 个自定义层槽位已用满，无法创建 {layerName}。"
                    + "请先清理不再使用的层，然后重跑全流程。");
            return -1;
        }
    }
}
