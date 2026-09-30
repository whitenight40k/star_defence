using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// 把 Art/Prefabs 下由 StructureAssetBuilder 产出的 Prefab 绑到对应的配置资产上。
    ///
    /// 这是"美术进玩法"的最后一跳。Builder 只负责生成 Prefab，不负责知道哪个 Prefab 是哪座塔；
    /// 那份映射关系集中在本文件，改映射只改这一处。
    ///
    /// 绑定是幂等的：已经绑对的不动，只填空的和错的。
    /// </summary>
    public static class StarDefenseAssetBinder
    {
        private const string PrefabDir = "Assets/StarDefense/Art/Prefabs";
        private const string BuildableDir = "Assets/StarDefense/ScriptableObjects/Buildables";
        private const string EnemyDir = "Assets/StarDefense/ScriptableObjects/Enemies";
        private const string LogPath = "Logs/StarDefenseAssetBind.txt";

        /// <summary>
        /// 配置 id → 美术资产基名（Prefab 去掉 PFB_ 前缀与 _A/_B 变体后缀的部分）。
        /// 还没有美术资产的条目先留着，等模型做出来就能自动绑上，不用再改代码。
        /// </summary>
        private static readonly Dictionary<string, string> BuildableArt = new Dictionary<string, string>
        {
            { "turret", "MachineGunTurret" },
            { "cannon", "CannonTurret" },
            { "tesla", "TeslaTower" },
            { "wall", "EnergyWall" },
            { "gen", "PowerGenerator" },
            { "radar", "Radar" },
            { "repair", "RepairStation" },
            { "shield", "ShieldGenerator" },
        };

        /// <summary>
        /// 玩法 id → 美术资产基名。左边是 <c>EnemyConfig.id</c>，右边是交付的模型名。
        ///
        /// 两边故意不统一：美术按造型命名（冲锋 / 挖掘 / 狙击），玩法按行为命名。
        /// 让某一侧为了对齐另一侧改名，会把已经交付的资产、台账、渲染图一起作废，
        /// 所以差异收敛在这张表里，而不是去改资产名。
        ///
        /// · <c>crawler</c>（普通虫）用 <c>ENM_Charger_A</c>（冲锋虫）—— 同一只，基础近战单位。
        /// · <c>thief</c>（搬运虫）**尚无模型**：台账里写明 <c>ENM_Thief_A</c> 要等搬运玩法
        ///   落地后才做。留空不是遗漏，运行时它退回图元，行为仍可调。
        /// </summary>
        private static readonly Dictionary<string, string> EnemyArt = new Dictionary<string, string>
        {
            { "crawler", "Charger" },
            { "bomber", "Bomber" },
            { "burrow", "Burrower" },
            { "sniper", "Sniper" },
            { "jammer", "Jammer" },
            { "thief", "Thief" },
            { "boss", "PlanetBeast" },
        };

        internal class BindStats
        {
            public int Bound;
            public int Missing;
            public int Unchanged;
            public readonly List<string> Lines = new List<string>();
        }

        [MenuItem("Star Defense/Bind Art Prefabs", false, 20)]
        public static void BindFromMenu()
        {
            var stats = Bind();
            Report(stats);
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// 执行绑定。internal 是为了让 <see cref="StarDefensePipeline"/> 按顺序驱动 ——
        /// 它必须在资产生成之后、场景重建之前跑完这一步，否则场景构建器拿不到 Prefab。
        /// </summary>
        internal static BindStats Bind()
        {
            var stats = new BindStats();

            BindFolder(BuildableDir, BuildableArt, stats);
            BindFolder(EnemyDir, EnemyArt, stats);

            AssetDatabase.SaveAssets();
            return stats;
        }

        private static void BindFolder(string folder, Dictionary<string, string> artMap, BindStats stats)
        {
            string[] guids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { folder });

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
                string baseName;
                if (!artMap.TryGetValue(id, out baseName))
                {
                    stats.Lines.Add($"  ?  {config.name}: id '{id}' 不在映射表里，跳过");
                    continue;
                }

                GameObject prefab = FindPrefab(baseName);
                if (prefab == null)
                {
                    stats.Missing++;
                    stats.Lines.Add($"  -  {config.name}: 美术资产 PFB_{baseName}_A 尚未产出，保持留空（运行时走图元回退）");
                    continue;
                }

                if (prefabProperty.objectReferenceValue == prefab)
                {
                    stats.Unchanged++;
                    stats.Lines.Add($"  =  {config.name} → {prefab.name}（已是最新）");
                    continue;
                }

                prefabProperty.objectReferenceValue = prefab;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(config);

                stats.Bound++;
                stats.Lines.Add($"  +  {config.name} → {prefab.name}");
            }
        }

        /// <summary>按 _A / _B / 无后缀的顺序找 Prefab。</summary>
        private static GameObject FindPrefab(string baseName)
        {
            string[] candidates = { "PFB_" + baseName + "_A", "PFB_" + baseName + "_B", "PFB_" + baseName };

            for (int i = 0; i < candidates.Length; i++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDir + "/" + candidates[i] + ".prefab");
                if (prefab != null)
                    return prefab;
            }
            return null;
        }

        [MenuItem("Star Defense/Fix Runtime Prefab Static Flags", false, 21)]
        public static void FixRuntimePrefabStaticFlags()
        {
            var stats = Bind();
            var lines = new List<string> { "清除运行时实例化 Prefab 的静态合批标记", "" };
            int touched = 0;

            var visited = new HashSet<GameObject>();

            foreach (string guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { BuildableDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var config = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (config == null)
                    continue;

                var serialized = new SerializedObject(config);
                SerializedProperty prefabProperty = serialized.FindProperty("prefab");
                if (prefabProperty == null)
                    continue;

                var prefab = prefabProperty.objectReferenceValue as GameObject;
                if (prefab == null || !visited.Add(prefab))
                    continue;

                int cleared = ClearStaticFlags(prefab);
                touched += cleared;
                lines.Add($"  {(cleared > 0 ? "✓" : "=")}  {prefab.name}: 清除 {cleared} 个静态标记");
            }

            lines.Insert(1, $"共清除 {touched} 处。建筑由运行时实例化并摆位，带静态合批标记会导致渲染位置错乱。");
            lines.Add("");
            lines.AddRange(stats.Lines);

            WriteLog(lines);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[StarDefenseAssetBinder] 静态合批标记清理完成，共 {touched} 处。详见 {LogPath}");
        }

        private static int ClearStaticFlags(GameObject prefab)
        {
            string path = AssetDatabase.GetAssetPath(prefab);
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            int cleared = 0;

            try
            {
                Transform[] all = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (GameObjectUtility.GetStaticEditorFlags(all[i].gameObject) == 0)
                        continue;

                    GameObjectUtility.SetStaticEditorFlags(all[i].gameObject, 0);
                    cleared++;
                }

                if (cleared > 0)
                    PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return cleared;
        }

        private static void Report(BindStats stats)
        {
            var lines = new List<string>
            {
                $"新绑定 {stats.Bound} 项 / 已最新 {stats.Unchanged} 项 / 缺美术资产 {stats.Missing} 项",
                ""
            };
            lines.AddRange(stats.Lines);

            WriteLog(lines);
            Debug.Log($"[StarDefenseAssetBinder] 绑定完成：新绑 {stats.Bound}，已最新 {stats.Unchanged}，缺资产 {stats.Missing}。详见 {LogPath}");
        }

        private static void WriteLog(List<string> lines)
        {
            try
            {
                var text = new StringBuilder();
                text.AppendLine("Star Defense 美术资产绑定报告");
                text.AppendLine();
                foreach (string line in lines)
                    text.AppendLine(line);

                Directory.CreateDirectory("Logs");
                File.WriteAllText(LogPath, text.ToString());
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[StarDefenseAssetBinder] 无法写入日志 {LogPath}: {e.Message}");
            }
        }
    }
}
