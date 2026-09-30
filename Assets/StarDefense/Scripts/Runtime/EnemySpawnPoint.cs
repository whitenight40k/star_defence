using System.Collections.Generic;
using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 一个可配置的刷怪点：开局等一段时间，然后按固定间隔从这个位置按批放敌人。
    ///
    /// 与 <see cref="WaveDirector"/> 的分工：那个是"威胁值攒满就开波"的压力型节奏，
    /// 这个是"时间到就刷"的节拍型节奏。两者都只依赖 <see cref="IEnemySpawner"/>，
    /// 谁也不认识谁 —— 想让它们同时生效或只留一个，删组件即可，不用改代码。
    ///
    /// 刷怪出口特意走接口而不是直接抓 <see cref="StarDefenseGame"/>：
    /// 联网版本换一个走网络授权的实现时，本类一行都不用改。
    /// </summary>
    [DisallowMultipleComponent]
    public class EnemySpawnPoint : MonoBehaviour
    {
        [Header("敌人")]
        [Tooltip("这个点会刷哪些虫。每只都从里面随机挑一个。\n留空则本点不刷怪（Awake 会给一条警告，而不是默默地什么都不做）。")]
        public EnemyConfig[] enemyTypes = new EnemyConfig[0];

        [Header("节奏（秒）")]
        [Tooltip("开局多少秒之后开始刷。默认 60 —— 需求里的「游戏开始 1 分钟后」。")]
        [Min(0f)] public float startDelay = 60f;

        [Tooltip("之后每隔多少秒刷一批。")]
        [Min(1f)] public float interval = 90f;

        [Header("每批数量")]
        [Min(0)] public int firstBatchCount = 2;

        [Tooltip("每批比上一批多几只。")]
        [Min(0)] public int countGrowthPerBatch = 1;

        [Tooltip("单批数量上限。0 表示不封顶。\n多个刷怪点分别配置时，这个是按住「总压力」那只手。")]
        [Min(0)] public int maxBatchCount = 8;

        [Header("落点")]
        [Tooltip("出生点周围的平面散布半径（米）。\n高度不在这里管：刷怪出口会统一贴到地形上。")]
        [Min(0f)] public float spawnRadius = 4f;

        [Tooltip("同一批内部的错峰间隔（秒）。0 表示同帧全部出现。\n默认给一点，免得一批虫像一堵墙一样整体平移过来。")]
        [Min(0f)] public float stagger = 0.35f;

        [Header("开关")]
        [Tooltip("关掉后本点不再刷怪。只想让敌人从一个方向来时，把其余点的这个勾去掉即可。")]
        public bool active = true;

        [Tooltip("提示里显示的方向名（如「正北」）。场景里叫 Spawn_1 这类名字，直接念给玩家听没有意义。")]
        public string displayName = "未知方向";

        /// <summary>
        /// 一批敌人刚被排进待刷队列时触发，参数是本点与这一批的数量。
        /// 只有提示用途 —— 没有订阅者时什么都不会发生，刷怪不受影响。
        /// </summary>
        public event System.Action<EnemySpawnPoint, int> BatchQueued;

        [Header("Wiring")]
        [Tooltip("必须挂一个实现了 IEnemySpawner 的组件（目前是 StarDefenseGame）。由场景构建器回填。")]
        public MonoBehaviour spawnerSource;

        /// <summary>场上还有多少只存活敌人。只用于日志与调试，不参与节奏。</summary>
        public int AliveEnemyCount => spawner != null ? spawner.AliveEnemyCount : 0;

        /// <summary>已经开过多少批。从 1 开始；0 表示还没开始刷。</summary>
        public int BatchIndex => batchIndex;

        /// <summary>距离下一批还有多少秒。HUD 或调试用；未就绪时返回 0。</summary>
        public float SecondsUntilNextBatch =>
            IsReady && active ? Mathf.Max(0f, nextBatchAt - elapsed) : 0f;

        /// <summary>
        /// 本点是否能真的刷出东西。四件事缺一不可：开关打开、出口接上、虫种配了、同批上限没被配成 0。
        /// 判据写成属性而不是在每个 Update 里重算，是因为"为什么不刷"必须有唯一一处答案。
        /// </summary>
        public bool IsReady =>
            active
            && ResolveSpawner() != null
            && enemyTypes != null
            && enemyTypes.Length > 0
            && nextBatchSize() > 0;

        private struct PendingSpawn
        {
            public EnemyConfig config;
            public float atTime;
        }

        /// <summary>
        /// 待刷队列上限。一批最多十只量级，64 是"队列绝不会被填满"的余量；
        /// 真被填满说明 interval 被配得比一批的错峰时长还短，日志里会说出来。
        /// </summary>
        private const int PendingCapacity = 64;

        private IEnemySpawner spawner;
        private readonly List<PendingSpawn> pending = new List<PendingSpawn>();

        /// <summary>自己的计时，从组件启用那一刻算起。</summary>
        private float elapsed;

        private float nextBatchAt;
        private int batchIndex;

        private void Awake()
        {
            ResolveSpawner();
            nextBatchAt = startDelay;

            if (spawnerSource != null && spawner == null)
            {
                Debug.LogError(
                    $"[EnemySpawnPoint] {spawnerSource.GetType().Name} 未实现 IEnemySpawner，"
                        + "这个点不会刷怪。",
                    this
                );
            }
            else if (spawnerSource == null)
            {
                Debug.LogWarning("[EnemySpawnPoint] 未接线 spawnerSource，这个点不会刷怪。", this);
            }

            if (enemyTypes == null || enemyTypes.Length == 0)
                Debug.LogWarning("[EnemySpawnPoint] 没有配置 enemyTypes，这个点不会刷怪。", this);
        }

        /// <summary>
        /// 出口可以晚于 Awake 才被填（构建器回填、或运行时动态生成场景）。
        /// 每帧顺手重试一次比"在 Awake 里一次性判定"更稳，代价只是一次类型转换。
        /// </summary>
        private IEnemySpawner ResolveSpawner()
        {
            if (spawner == null && spawnerSource != null)
                spawner = spawnerSource as IEnemySpawner;

            return spawner;
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            elapsed += deltaTime;

            FlushPending();

            if (!IsReady)
                return;

            if (elapsed < nextBatchAt)
                return;

            QueueBatch();

            // 基准取"现在"而不是"上一次的计划时刻"：宁可丢掉卡顿期间欠下的批次，
            // 也不要在恢复流畅的瞬间把攒的几批一次性倒出来 —— 那种爆发看起来像 bug，
            // 而且正好发生在玩家最难受的时候（掉帧本来就够糟了）。
            nextBatchAt = elapsed + interval;
        }

        /// <summary>把一批敌人排进待刷队列，按 stagger 逐只错开。</summary>
        private void QueueBatch()
        {
            // 先取数量再自增：nextBatchSize() 算的是"第 batchIndex + 1 批"，
            // 也就是"即将开的这一批"。顺序反过来的话它会去算下一批，
            // 首批凭空多出一只，而且 IsReady 的预演与真实行为对不上。
            int count = nextBatchSize();
            if (count <= 0)
                return;

            batchIndex++;

            float now = elapsed;
            int queued = 0;

            for (int i = 0; i < count; i++)
            {
                if (pending.Count >= PendingCapacity)
                {
                    Debug.LogWarning(
                        $"[EnemySpawnPoint] 待刷队列已满（{PendingCapacity}），第 {batchIndex} 批被截断。"
                            + "把 interval 调大，或把 stagger × 单批数量 控制在 interval 以内。",
                        this
                    );
                    break;
                }

                EnemyConfig config = enemyTypes[Random.Range(0, enemyTypes.Length)];
                if (config == null)
                    continue;

                pending.Add(new PendingSpawn { config = config, atTime = now + stagger * i });
                queued++;
            }

            if (queued == 0)
                Debug.LogWarning($"[EnemySpawnPoint] 第 {batchIndex} 批没有产出任何敌人，检查 enemyTypes。", this);
            else
                BatchQueued?.Invoke(this, queued);
        }

        /// <summary>本批该刷几只。首次为 firstBatchCount，之后逐批递增，被 maxBatchCount 截断。</summary>
        private int nextBatchSize()
        {
            // 传 growthCap = 0 是 WaveComposer 的"增长量不封顶"哨兵，然后由我们按总量截断。
            // 两件事分开做是有意的：GroupCount 管的是"增长量封顶"，而刷怪点要的是"单批总量封顶"，
            // 把 maxBatchCount 当成 growthCap 传进去，在 maxBatchCount == firstBatchCount 时
            // 会退化成"不封顶"（哨兵值撞车），数量于是每批一直涨且不报错。
            int count = WaveComposer.GroupCount(firstBatchCount, batchIndex + 1, countGrowthPerBatch, 0);

            if (maxBatchCount > 0)
                count = Mathf.Min(count, maxBatchCount);

            return Mathf.Max(0, count);
        }

        /// <summary>把到时间的待刷项真正放出来。高度交给刷怪出口统一贴地。</summary>
        private void FlushPending()
        {
            if (pending.Count == 0)
                return;

            IEnemySpawner outlet = ResolveSpawner();
            if (outlet == null)
                return;

            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].atTime > elapsed)
                    continue;

                PendingSpawn item = pending[i];
                pending.RemoveAt(i);

                outlet.SpawnEnemy(item.config, NextSpawnPosition(), 1f);
            }
        }

        private Vector3 NextSpawnPosition()
        {
            // 只抖 XZ：刷怪出口会把落点贴到地形上，这里若连 y 一起抖，
            // 那个偏移量就成了一次没人负责修正的初速度误差。
            Vector2 jitter = Random.insideUnitCircle * spawnRadius;
            return transform.position + new Vector3(jitter.x, 0f, jitter.y);
        }
    }
}
