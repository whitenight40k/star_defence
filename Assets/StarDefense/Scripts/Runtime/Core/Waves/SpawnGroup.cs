using System;
using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 一波里的一个刷怪组，对应策划案第 9 章 waves.csv 的一行
    /// （spawn_group / count / delay_sec / hp_scale / speed_scale）。
    ///
    /// 用 class 而不是 struct：Unity 通过默认构造函数创建新增元素，
    /// 字段初始化器才会生效，hpScale / speedScale 默认就是 1 而不是 0。
    /// 用 struct 的话，新加一组会得到 0 倍血量，是个很难查的坑。
    /// </summary>
    [Serializable]
    public class SpawnGroup
    {
        [Tooltip("本组刷哪种敌人。指向 EnemyConfig 资产。")]
        public EnemyConfig enemy;

        [Min(1)] public int count = 1;

        [Tooltip("相对本波开始的延迟秒数。同一波里各编队先后到场靠它错开。")]
        [Min(0f)] public float startDelay;

        [Tooltip("组内个体之间的额外间隔秒数。0 表示只按全局错峰抖动排开。")]
        [Min(0f)] public float interval;

        [Min(0f)] public float hpScale = 1f;

        [Min(0f)] public float speedScale = 1f;

        [Tooltip("波次号每 +1，本组数量上浮多少。对应规格里普通虫的 min(8, w) 增长项。")]
        [Min(0)] public int perWaveGrowth;

        [Tooltip("本组增长封顶。0 表示不封顶。")]
        [Min(0)] public int growthCap;
    }
}
