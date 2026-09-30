using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 敌人通用推进与近战。
    /// 这里只保留"所有虫子都这么做"的部分；差异化的行为交给 EnemyBehavior 子类，
    /// 子类通过 TakeOverMovement / TakeOverCombat 局部接管，避免把 switch 堆进本类。
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class EnemyUnit : MonoBehaviour
    {
        public EnemyConfig config;
        public StarDefenseGame game;

        /// <summary>
        /// 贴地抬升（米）：原点在底面的资产给 0，原点在几何中心的图元给半径/半高。
        /// 由 <c>StarDefenseGame.SpawnEnemy</c> 在刷怪时写入 —— 只有刷怪那条路径知道
        /// 这次用的是资产还是图元。
        /// </summary>
        public float groundOffset;

        private Health health;
        private EnemyBehavior behavior;
        private Transform currentTarget;
        private float attackTimer;
        private float slowTimer;
        private float slowMultiplier = 1f;

        // ---- 前方阻挡探测 ----
        //
        // 虫子是直接推 transform 前进的（见 MoveToward），不做物理碰撞响应 ——
        // 运动学刚体本来就不会被撞开。所以"被建筑挡住"必须自己探、自己停，
        // 否则一群虫子会直接从墙里穿过去，而且不会有任何报错。
        //
        // 探测球的体型参数只在 Initialize 里量一次，之后每帧只读位置：
        // 虫子每帧都在动，若每帧重读 bounds，长条形的 Charge 转身时
        // AABB 会忽大忽小，探测球跟着缩胀，表现为"有时候能撞到墙、有时候穿过去"。

        /// <summary>探测命中缓存。静态复用，避免每帧每只虫都分配一个数组。</summary>
        private static readonly RaycastHit[] ProbeHits = new RaycastHit[16];

        /// <summary>
        /// Unity 内置的 Ignore Raycast 层索引。它永远是 2，与 <see cref="BuildPreview"/>
        /// 里查不到层名时的兜底值一致。
        /// </summary>
        private const int IgnoreRaycastLayer = 2;

        /// <summary>
        /// 探测用的层掩码：排除 Ignore Raycast。
        ///
        /// 建造虚影被放在这一层，而且它的碰撞体在生成时就会被销毁 —— 两道保险都留着，
        /// 因为虚影是跟着玩家鼠标走的。少了任何一道，玩家把虚影举在虫子前面时，
        /// 虫子就会停在一个"还不存在的建筑"前面。
        /// </summary>
        private static readonly int ProbeLayerMask = ~(1 << IgnoreRaycastLayer);

        private Collider bodyCollider;
        private float probeRadius = 0.3f;
        private float probeCenterY = 0.35f;

        public Health Health => health;
        public bool IsAlive => health != null && !health.IsDead;
        public Transform CurrentTarget => currentTarget;

        /// <summary>挖掘虫等行为需要绕过防线直奔的最终目标。</summary>
        public Transform CoreTransform => game != null ? game.core : null;

        public float SlowMultiplier => slowMultiplier;

        private void Awake()
        {
            health = GetComponent<Health>();
            health.Died += OnDied;

            // 这里不用 [RequireComponent(typeof(Collider))] 去满足 UNT0039：
            // Collider 是抽象类，Unity 在缺组件时会试着 AddComponent 一个抽象类型并失败，
            // 结果是"为了防止静默 null，先制造一个更响的报错"。它的缺席是可处理的
            // （见 ConfigureProbe 的默认体型分支），所以按可选依赖对待。
            bodyCollider = GetComponent<Collider>();
            if (bodyCollider == null)
                bodyCollider = GetComponentInChildren<Collider>();
        }

        private void OnDestroy()
        {
            if (health != null)
                health.Died -= OnDied;
        }

        public void Initialize(EnemyConfig enemyConfig, StarDefenseGame owner, float healthScale = 1f)
        {
            config = enemyConfig;
            game = owner;
            gameObject.name = enemyConfig.displayName;

            if (health == null)
                health = GetComponent<Health>();

            if (health != null)
                health.Configure(enemyConfig.maxHealth * Mathf.Max(0.01f, healthScale));

            // 换成美术 Prefab 后不再覆盖缩放。资产是按米 authored 的，
            // config.radius 只是图元时代的球形替代半径，覆盖它会把模型压成小球。
            if (enemyConfig.prefab == null)
                transform.localScale = Vector3.one * enemyConfig.radius;

            // 必须在缩放定下来之后量：探测球是按实际体型算的，量早了会拿到旧尺寸。
            ConfigureProbe();

            AttachBehavior(enemyConfig.behavior);
        }

        /// <summary>
        /// 量一次前方探测球的体型参数。之后每帧只读位置，不再重读 bounds。
        ///
        /// 假设敌人的根不带轴向校正（当前 Enemy 批是 Y-up，root 是 identity）。
        /// 若哪天这批资产换成带 -90° X 的 Z-up 版本，这里的"高度"会变成"长度"，
        /// 探测球会横过来 —— 那时请连同 <c>EnemyAssetBuilder</c> 的骨骼断言一起复核。
        /// </summary>
        private void ConfigureProbe()
        {
            // 体型量不出来时用 1 米见方的默认值：程序化占位敌人没有碰撞体，
            // 而探测本身不需要自己有碰撞体（SphereCast 用的是我们自己的球），
            // 所以它照样能被建筑挡住，只是判定盒子取了默认尺寸。
            Bounds bounds = bodyCollider != null
                ? bodyCollider.bounds
                : new Bounds(transform.position + Vector3.up * 0.5f, Vector3.one);

            Vector3 extents = bounds.extents;

            // 水平方向尽量取宽：取小了虫子会半截身子插进墙里才停下来。
            float horizontal = Mathf.Min(Mathf.Abs(extents.x), Mathf.Abs(extents.z));

            // 但垂直方向不能超出身体。球心加半径一旦越过脚底，球就扎进地里，
            // 于是每帧都命中地形 —— 地形不挡路、会被过滤掉所以不会误停，
            // 但它会把 ProbeHits 的槽位占满，真正的墙反而排不进数组里。
            probeRadius = Mathf.Max(0.12f, Mathf.Min(horizontal * 0.9f, extents.y * 0.95f));
            probeCenterY = Mathf.Max(bounds.center.y - transform.position.y, probeRadius + 0.05f);
        }

        private void AttachBehavior(EnemyBehaviorType type)
        {
            switch (type)
            {
                case EnemyBehaviorType.Bomber:
                    behavior = gameObject.AddComponent<BomberBehavior>();
                    break;

                case EnemyBehaviorType.Burrower:
                    behavior = gameObject.AddComponent<BurrowerBehavior>();
                    break;

                case EnemyBehaviorType.Sniper:
                    behavior = gameObject.AddComponent<SniperBehavior>();
                    break;

                case EnemyBehaviorType.Jammer:
                    behavior = gameObject.AddComponent<JammerBehavior>();
                    break;
            }

            if (behavior != null)
                behavior.Initialize(this);
        }

        private void Update()
        {
            if (config == null || game == null || !IsAlive)
                return;

            float deltaTime = Time.deltaTime;
            UpdateSlow(deltaTime);

            // 先刷新目标再驱动行为：行为组件都要读 CurrentTarget，
            // 若放在 Tick 之后更新，行为会整整慢一帧才拿到目标。
            currentTarget = game.GetEnemyTarget(transform.position, config);
            if (currentTarget == null)
                return;

            if (behavior != null)
                behavior.Tick(deltaTime);

            bool movementTaken = behavior != null && behavior.TakeOverMovement;
            bool combatTaken = behavior != null && behavior.TakeOverCombat;

            // 遇阻就打：把挡在去路上的建筑换成当前目标，这一帧就停下来拆它。
            //
            // 必须在算距离**之前**替换。放到后面只是白算一次探测 ——
            // 虫子仍会朝基地推进，然后从墙里穿过去（它是直接推 transform 的，没有物理阻挡）。
            if (!movementTaken)
            {
                Transform blocker = ProbeBlocker(currentTarget, deltaTime);
                if (blocker != null)
                    currentTarget = blocker;
            }

            float distance = StarDefenseMath.HorizontalDistance(transform.position, currentTarget.position);

            if (!movementTaken && distance > config.attackRange)
            {
                MoveToward(currentTarget.position, deltaTime);
                return;
            }

            if (!combatTaken && distance <= config.attackRange)
                TryAttack(currentTarget, deltaTime);
        }

        /// <summary>
        /// 探测去路上有没有建筑挡着。返回 null 表示畅通。
        ///
        /// 探测距离就是"这一帧会走多远"：它只回答"这一步会撞到谁"。
        /// 探得更远会把侧前方的建筑也吸进来，虫子于是拐过去拆它 ——
        /// 那正是旧版"被路边建筑吸引、绕着基地打转"的行为，绕一圈又回来了。
        /// </summary>
        private Transform ProbeBlocker(Transform goal, float deltaTime)
        {
            if (config == null || goal == null)
                return null;

            Vector3 flat = goal.position - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude <= 0.0001f)
                return null;

            // 球心抬到身体中部：贴着脚底探的话，沙丘起伏会让每一次探测都命中地形。
            Vector3 origin = transform.position + Vector3.up * probeCenterY;

            // 给一个下限：deltaTime 为 0 时距离为 0，SphereCast 不会报告"起点已经贴着墙"，
            // 于是第一帧的判断会漏掉。
            float step = Mathf.Max(0.02f, config.moveSpeed * slowMultiplier * deltaTime);

            int count = Physics.SphereCastNonAlloc(
                origin,
                probeRadius,
                flat.normalized,
                ProbeHits,
                step,
                ProbeLayerMask,
                QueryTriggerInteraction.Ignore
            );

            Transform best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = ProbeHits[i];
                if (hit.collider == null)
                    continue;

                Transform blocker = ResolveBlocker(hit.collider);
                if (blocker == null)
                    continue;

                // 取最近的那个：一只虫可能同时贴着墙和炮塔，先拆挡得最死的。
                if (hit.distance < bestDistance)
                {
                    best = blocker;
                    bestDistance = hit.distance;
                }
            }

            return best;
        }

        /// <summary>
        /// 命中物是不是"能挡路的实体"。
        ///
        /// 只认建筑和基地 —— 地形、岩石、资源点、其他虫子、玩家都不挡。
        /// 别把这里放宽成"任何碰撞体都能挡"：那会让虫子被一块石头卡住，
        /// 而卡住它的东西既不是它的目标、它也不会去拆，表现是一群虫子对着一块岩石原地抽搐。
        /// </summary>
        private Transform ResolveBlocker(Collider collider)
        {
            if (collider == null)
                return null;

            Transform hit = collider.transform;

            // 自己。SphereCast 的起点常常落在自己的碰撞体内部，会以 distance 0 命中自己。
            if (hit.IsChildOf(transform))
                return null;

            // 同族。虫子之间不互相挡 —— 它们本来就没有物理碰撞响应，一直是重叠着走的。
            if (collider.GetComponentInParent<EnemyUnit>() != null)
                return null;

            // 玩家。玩家挡不住虫子：玩家是移动的，被一只虫"卡住"会变成一种莫名其妙的位移限制。
            if (game != null && game.player != null && hit.IsChildOf(game.player))
                return null;

            // 基地要单独认：它是一个带 Health 的普通对象，身上没有 Buildable。
            if (game != null && game.core != null && hit.IsChildOf(game.core))
            {
                Health coreHealth = game.CoreHealth;
                return coreHealth != null && !coreHealth.IsDead ? game.core : null;
            }

            Buildable building = collider.GetComponentInParent<Buildable>();
            if (building != null && building.Health != null && !building.Health.IsDead)
                return building.transform;

            return null;
        }

        private void UpdateSlow(float deltaTime)
        {
            if (slowTimer <= 0f)
                return;

            slowTimer -= deltaTime;
            if (slowTimer <= 0f)
                slowMultiplier = 1f;
        }

        /// <summary>朝目标推进。行为组件也复用这条路径，保证减速效果一致生效。</summary>
        public void MoveToward(Vector3 destination, float deltaTime, float speedScale = 1f)
        {
            Vector3 direction = destination - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.01f)
                return;

            FaceTowards(destination);
            float speed = config.moveSpeed * slowMultiplier * Mathf.Max(0.05f, speedScale);
            transform.position += direction.normalized * (speed * deltaTime);

            // 这里只推了 XZ（上面把方向压平了），所以每帧都要重新贴一次地面。
            // 不做这一步，沙丘上的虫子会沿出生高度一路平飞 —— 不会报错，只是看着"飘"。
            transform.position = PlanetTerrain.SnapToGround(transform.position, groundOffset);
        }

        public void FaceTowards(Vector3 worldPosition)
        {
            Vector3 direction = worldPosition - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        public bool TryAttack(Transform target, float deltaTime)
        {
            if (target == null)
                return false;

            attackTimer -= deltaTime;
            if (attackTimer > 0f)
                return false;

            attackTimer = Mathf.Max(0.1f, config.attackInterval);
            game.EnemyAttack(this, target, config.damage);
            return true;
        }

        public void ApplyDamage(float amount)
        {
            if (health != null)
                health.TakeDamage(amount);
        }

        public void ApplySlow(float multiplier, float seconds)
        {
            slowMultiplier = Mathf.Min(slowMultiplier, Mathf.Clamp(multiplier, 0.1f, 1f));
            slowTimer = Mathf.Max(slowTimer, seconds);
        }

        /// <summary>
        /// 让敌人以"被击杀"的方式退场，用于自爆虫这类自行了结的单位。
        /// 走 Health 死亡流程，保证战利品结算与列表清理一致，不额外开一条销毁路径。
        /// </summary>
        public void KillSelf()
        {
            if (health != null)
                health.TakeDamage(health.MaxHealth + 1f);
        }

        private void OnDied(Health deadHealth)
        {
            if (game != null)
                game.NotifyEnemyKilled(this);
            Destroy(gameObject, 0.05f);
        }
    }
}
