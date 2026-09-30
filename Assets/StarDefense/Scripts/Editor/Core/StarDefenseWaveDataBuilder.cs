using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// 按策划案规格生成波次数据资产。
    ///
    /// 数值来自策划案的可执行规格：威胁值随时间按档位累积、满 100 触发一波、
    /// 波次组成随威胁档位解锁并按波次号递增（普通虫 min(8,w)、自爆虫 min(3,w/2) 等）。
    ///
    /// 生成后仍是普通资产，策划可以直接在 Inspector 里改，改完不需要动代码。
    /// 重新执行本命令会覆盖同名资产——手工调过的数值请先另存。
    /// </summary>
    public static class StarDefenseWaveDataBuilder
    {
        private const string SoRoot = "Assets/StarDefense/ScriptableObjects";
        private const string EnemyDir = SoRoot + "/Enemies";
        private const string VariableDir = SoRoot + "/Variables";
        private const string WaveDir = SoRoot + "/Waves";

        [MenuItem("Star Defense/Create Wave Data Assets", false, 30)]
        public static void CreateWaveData()
        {
            if (!EditorUtility.DisplayDialog(
                    "生成波次数据资产",
                    "将生成/覆盖以下资产：\n\n" +
                    "  ScriptableObjects/Variables/Var_Threat.asset\n" +
                    "  ScriptableObjects/Waves/WaveTable_Standard.asset\n\n" +
                    "手工调过的数值会被覆盖，请先另存。继续？",
                    "生成",
                    "取消"))
                return;

            EnsureFolder(VariableDir);
            EnsureFolder(WaveDir);

            var threat = CreateOrLoad<FloatVariable>(VariableDir + "/Var_Threat.asset");

            var table = CreateOrLoad<WaveTable>(WaveDir + "/WaveTable_Standard.asset");
            ConfigureTable(table);

            EditorUtility.SetDirty(threat);
            EditorUtility.SetDirty(table);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[StarDefenseWaveDataBuilder] 已生成 Var_Threat 与 WaveTable_Standard（{table.tiers.Count} 个威胁档位，" +
                      $"共 {CountGroups(table)} 个刷怪组）。把 Var_Threat 同时指到 StarDefenseGame.threatMeter 与 WaveDirector.threatMeter 即可。");
        }

        private static void ConfigureTable(WaveTable table)
        {
            table.contract = "standard";
            table.waveThreshold = 100f;
            table.maxAliveEnemies = 120;
            table.spawnStagger = new Vector2(0.25f, 0.8f);
            table.waveHpRamp = 0f;

            table.tiers = new List<ThreatTier>
            {
                // 登陆期：只积累威胁，不组波。给玩家一段安静的建设窗口。
                new ThreatTier
                {
                    startTime = 0f,
                    threatPerSecond = 0.35f,
                    composition = new List<SpawnGroup>()
                },

                // L1：普通虫登场，验证防线是否成立。
                new ThreatTier
                {
                    startTime = 60f,
                    threatPerSecond = 1.05f,
                    composition = new List<SpawnGroup>
                    {
                        Group("Crawler", 4, perWaveGrowth: 1, growthCap: 8)
                    }
                },

                // L2：加入自爆虫，逼玩家别把塔堆在一起。
                new ThreatTier
                {
                    startTime = 180f,
                    threatPerSecond = 1.30f,
                    composition = new List<SpawnGroup>
                    {
                        Group("Crawler", 4, perWaveGrowth: 1, growthCap: 8),
                        Group("Bomber", 1, perWaveGrowth: 1, growthCap: 3)
                    }
                },

                // L3：加入挖掘虫与狙击虫——一个绕防线、一个点玩家。
                new ThreatTier
                {
                    startTime = 360f,
                    threatPerSecond = 1.55f,
                    composition = new List<SpawnGroup>
                    {
                        Group("Crawler", 6, perWaveGrowth: 1, growthCap: 8),
                        Group("Bomber", 1, perWaveGrowth: 1, growthCap: 3),
                        Group("Burrower", 1, perWaveGrowth: 1, growthCap: 2),
                        Group("SniperBug", 1, perWaveGrowth: 0, growthCap: 0)
                    }
                },

                // L4：Boss 阶段。干扰虫瘫痪防线、搬运虫偷资源，压力从"打不过"转向"顾不过来"。
                new ThreatTier
                {
                    startTime = 600f,
                    threatPerSecond = 1.60f,
                    composition = new List<SpawnGroup>
                    {
                        Group("Crawler", 6, perWaveGrowth: 1, growthCap: 8),
                        Group("Bomber", 3, perWaveGrowth: 1, growthCap: 3),
                        Group("Burrower", 1, perWaveGrowth: 1, growthCap: 2),
                        Group("SniperBug", 1, perWaveGrowth: 0, growthCap: 0),
                        Group("Jammer", 1, perWaveGrowth: 0, growthCap: 0),
                        Group("Thief", 1, perWaveGrowth: 0, growthCap: 0)
                    }
                }
            };
        }

        private static SpawnGroup Group(string enemyAssetName, int count, int perWaveGrowth, int growthCap)
        {
            var enemy = AssetDatabase.LoadAssetAtPath<EnemyConfig>(EnemyDir + "/" + enemyAssetName + ".asset");
            if (enemy == null)
                Debug.LogWarning($"[StarDefenseWaveDataBuilder] 找不到敌人配置 {enemyAssetName}.asset，该组会留空。");

            return new SpawnGroup
            {
                enemy = enemy,
                count = count,
                startDelay = 0f,
                interval = 0f,
                hpScale = 1f,
                speedScale = 1f,
                perWaveGrowth = perWaveGrowth,
                growthCap = growthCap
            };
        }

        private static int CountGroups(WaveTable table)
        {
            int total = 0;
            for (int i = 0; i < table.tiers.Count; i++)
            {
                if (table.tiers[i] != null && table.tiers[i].composition != null)
                    total += table.tiers[i].composition.Count;
            }
            return total;
        }

        /// <summary>存在就加载（保留 GUID，场景里的引用不会断），不存在就新建。</summary>
        private static T CreateOrLoad<T>(string path) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
                return existing;

            var created = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = Path.GetFileName(path);

            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);

            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
