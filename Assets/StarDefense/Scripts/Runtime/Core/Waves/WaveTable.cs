using System;
using System.Collections.Generic;
using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 威胁等级档位。对应策划案"威胁值随时间累积、并按时间解锁更高阶的敌人组成"。
    /// H5 规格里的 L_RATE 表就是这套东西：登陆 0.35 / L1 1.05 / L2 1.30 / L3 1.55 每秒。
    /// </summary>
    [Serializable]
    public class ThreatTier
    {
        [Tooltip("本档从第几秒开始生效（相对本局开始）。")]
        [Min(0f)] public float startTime = 60f;

        [Tooltip("本档的威胁累积速率（点/秒）。")]
        [Min(0f)] public float threatPerSecond = 1.05f;

        [Tooltip("本档每触发一波时的基础组成。留空表示此档不组波（例如登陆期）。\n" +
                 "组成是累加的：L2 的档位里要重复写出 L1 已有的组。")]
        public List<SpawnGroup> composition = new List<SpawnGroup>();
    }

    /// <summary>
    /// 一份合同下的完整波次配置。取代原先写死在 SpawnWave() 里的 8/12/5。
    /// </summary>
    [CreateAssetMenu(menuName = "Star Defense/Waves/Wave Table", fileName = "WaveTable")]
    public class WaveTable : ScriptableObject
    {
        [Header("Identity")]
        public string contract = "standard";

        [Header("Thresholds")]
        [Tooltip("威胁值达到这个数就触发一波。策划案为 100。")]
        [Min(1f)] public float waveThreshold = 100f;

        [Tooltip("场上同时存活敌人上限。策划案技术要点：场上敌人 ≤ 120。")]
        [Min(1)] public int maxAliveEnemies = 120;

        [Tooltip("同波内相邻两只敌人的出场间隔区间（秒）。规格为 0.25~0.8。")]
        public Vector2 spawnStagger = new Vector2(0.25f, 0.8f);

        [Tooltip("每波额外叠加的强度倍率：第 n 波的实际血量 = hpScale × (1 + (n-1) × 本值)。")]
        [Min(0f)] public float waveHpRamp;

        [Header("Tiers")]
        public List<ThreatTier> tiers = new List<ThreatTier>();

        /// <summary>取当前时刻生效的档位。时间倒着找，startTime 已过的最近一档即当前档。</summary>
        public ThreatTier TierAt(float matchTime)
        {
            ThreatTier best = null;
            for (int i = 0; i < tiers.Count; i++)
            {
                ThreatTier tier = tiers[i];
                if (tier == null || matchTime < tier.startTime)
                    continue;

                if (best == null || tier.startTime > best.startTime)
                    best = tier;
            }
            return best;
        }
    }
}
