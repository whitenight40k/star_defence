using System.Collections.Generic;
using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 刷怪出口。WaveDirector 只依赖这个接口，不认识具体实现。
    /// 后面的联网版本换一个走网络授权的实现即可，本类不用改。
    /// </summary>
    public interface IEnemySpawner
    {
        EnemyUnit SpawnEnemy(EnemyConfig config, Vector3 position, float hpScale);

        /// <summary>场上存活敌人数量。上限判定用它，不需要认识具体是哪个实现类。</summary>
        int AliveEnemyCount { get; }
    }

    /// <summary>
    /// 波次导演：威胁值累积 → 触发开波 → 按配置组队 → 错峰刷出。
    ///
    /// 从 StarDefenseGame 里独立出来的第一块。它只管"什么时候放什么进来"，
    /// 不碰伤害、不碰建筑、不碰胜负判定。跨系统通信用 FloatVariable（威胁值）
    /// 和 IEnemySpawner（刷怪出口），不持有 StarDefenseGame 引用。
    ///
    /// 未挂到场景里时，StarDefenseGame 会走它原有的硬编码刷怪路径，行为不变。
    /// </summary>
    [DisallowMultipleComponent]
    public class WaveDirector : MonoBehaviour
    {
        [Header("Data")]
        public WaveTable table;

        [Tooltip("威胁值。与 StarDefenseGame 共持同一个资产，双方都不需要认识对方。")]
        public FloatVariable threatMeter;

        [Header("Wiring")]
        [Tooltip("必须挂一个实现了 IEnemySpawner 的组件（目前是 StarDefenseGame）。")]
        public MonoBehaviour spawnerSource;

        public Transform[] spawnPoints = new Transform[0];

        [Header("Spawn")]
        [Tooltip("刷怪点周围的随机散布半径（米）。")]
        [Min(0f)] public float spawnRadius = 4f;

        [Tooltip("待刷队列上限。防止长时间不刷导致的队列堆积。")]
        [Min(16)] public int pendingCapacity = 160;

        /// <summary>一波开始时触发。参数是本波序号（从 1 开始）。</summary>
        public event System.Action<int> WaveStarted;

        private struct PendingSpawn
        {
            public EnemyConfig config;
            public float atTime;
            public float hpScale;
        }

        private IEnemySpawner spawner;
        private readonly List<PendingSpawn> pending = new List<PendingSpawn>();

        private float localThreat;
        private float matchTime;
        private int waveNumber;

        public int WaveNumber => waveNumber;
        public int PendingCount => pending.Count;
        public float Threat => threatMeter != null ? threatMeter.Value : localThreat;
        public float Threshold => table != null ? table.waveThreshold : 100f;
        public bool IsReady => table != null && spawner != null && spawnPoints != null && spawnPoints.Length > 0;

        private void Awake()
        {
            spawner = spawnerSource as IEnemySpawner;

            if (spawnerSource != null && spawner == null)
                Debug.LogError($"[WaveDirector] {spawnerSource.GetType().Name} 未实现 IEnemySpawner，不会刷怪。", this);

            if (table == null)
                Debug.LogWarning("[WaveDirector] 未指定 WaveTable，本组件不生效。", this);

            if (spawnPoints == null || spawnPoints.Length == 0)
                Debug.LogWarning("[WaveDirector] 未指定刷怪点，本组件不生效。", this);
        }

        /// <summary>外部系统（采矿、建造、爆炸）加威胁值走这里。</summary>
        public void AddThreat(float amount)
        {
            if (amount <= 0f)
                return;

            if (threatMeter != null)
                threatMeter.ApplyChange(amount);
            else
                localThreat += amount;
        }

        private void Update()
        {
            if (!IsReady)
                return;

            matchTime += Time.deltaTime;
            FlushPending();
            AccumulateThreat();
        }

        /// <summary>按当前档位的速率累积威胁，满阈值就开波并扣掉一个阈值。</summary>
        private void AccumulateThreat()
        {
            ThreatTier tier = table.TierAt(matchTime);
            if (tier == null || tier.composition.Count == 0)
                return;

            AddThreat(tier.threatPerSecond * Time.deltaTime);

            // 用 while 而不是 if：帧率极低时一帧可能跨过多个阈值，if 会吞掉其中几波。
            int guard = 0;
            while (Threat >= table.waveThreshold && guard < 8)
            {
                guard++;
                AddThreat(-table.waveThreshold);
                TriggerWave(tier);
            }
        }

        /// <summary>按档位组成开一波，并把个体排进待刷队列。</summary>
        private void TriggerWave(ThreatTier tier)
        {
            waveNumber++;

            float now = Time.time;
            int queued = 0;

            for (int g = 0; g < tier.composition.Count; g++)
            {
                SpawnGroup group = tier.composition[g];
                if (group == null || group.enemy == null)
                    continue;

                int count = WaveComposer.GroupCount(group.count, waveNumber, group.perWaveGrowth, group.growthCap);
                float hpScale = group.hpScale * WaveComposer.WaveHpRamp(table.waveHpRamp, waveNumber);

                for (int i = 0; i < count; i++)
                {
                    if (pending.Count >= pendingCapacity)
                        break;

                    float jitter = Random.Range(table.spawnStagger.x, table.spawnStagger.y);
                    pending.Add(new PendingSpawn
                    {
                        config = group.enemy,
                        atTime = now + group.startDelay + jitter * (i + 1) + group.interval * i,
                        hpScale = hpScale
                    });
                    queued++;
                }
            }

            pending.Sort((a, b) => a.atTime.CompareTo(b.atTime));
            WaveStarted?.Invoke(waveNumber);

            if (queued == 0)
                Debug.LogWarning($"[WaveDirector] 第 {waveNumber} 波的配置未产出任何敌人，检查 WaveTable 的 composition。", this);
        }

        /// <summary>把到时间的待刷项真正实例化出来。</summary>
        private void FlushPending()
        {
            if (pending.Count == 0)
                return;

            float now = Time.time;

            // 场上存活上限是性能预算，不是玩法限制：满了就只是推迟，不丢弃。
            int budget = Mathf.Max(0, table.maxAliveEnemies - spawner.AliveEnemyCount);

            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].atTime > now)
                    continue;
                if (budget <= 0)
                    break;

                PendingSpawn item = pending[i];
                pending.RemoveAt(i);

                Vector3 position = NextSpawnPosition();
                spawner.SpawnEnemy(item.config, position, item.hpScale);
                budget--;
            }
        }

        private Vector3 NextSpawnPosition()
        {
            Transform point = spawnPoints[Random.Range(0, spawnPoints.Length)];
            if (point == null)
                return transform.position;

            Vector3 position = point.position + Random.insideUnitSphere * spawnRadius;
            position.y = point.position.y;
            return position;
        }
    }

    /// <summary>
    /// 波次组成的纯计算，不依赖 Unity 对象，方便单独核对数值。
    /// 规则取自策划案规格：基础数量随波次号增长，但封顶（原型里的 min(8, w) 就是这一条）。
    /// </summary>
    public static class WaveComposer
    {
        /// <summary>
        /// 本波该组实际数量 = 基础值 + min(增长上限, (波次号-1) × 每波增量)。
        /// growthCap 传 0 表示不封顶。
        /// </summary>
        public static int GroupCount(int baseCount, int waveNumber, int perWaveGrowth, int growthCap)
        {
            int growth = Mathf.Max(0, (waveNumber - 1) * Mathf.Max(0, perWaveGrowth));
            if (growthCap > 0)
                growth = Mathf.Min(growth, growthCap);

            return Mathf.Max(0, baseCount + growth);
        }

        /// <summary>第 n 波的血量倍率。n=1 时为 1，之后按 waveHpRamp 线性叠加。</summary>
        public static float WaveHpRamp(float rampPerWave, int waveNumber)
        {
            if (rampPerWave <= 0f || waveNumber <= 1)
                return 1f;

            return 1f + (waveNumber - 1) * rampPerWave;
        }
    }
}
