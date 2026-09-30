using System.Collections.Generic;
using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 对局总控。当前仍持有配置、场景引用、阶段状态机、电网、建造与查询等多件事，
    /// 正在逐块外移（威胁与开波已交给 WaveDirector）。新功能不要再往这里加。
    /// </summary>
    public class StarDefenseGame : MonoBehaviour, IEnemySpawner
    {
        [Header("Data")]
        public GameBalanceConfig balance;
        public BuildableConfig[] buildables = new BuildableConfig[0];
        public EnemyConfig[] enemies = new EnemyConfig[0];
        public EnemyConfig bossConfig;

        [Header("Scene")]
        public Transform core;
        public Transform player;
        public Transform enemyRoot;
        public Transform buildingRoot;
        public Transform resourceRoot;
        public Transform[] spawnPoints = new Transform[0];

        [Header("Runtime")]
        public ResourceWallet resources;
        public float matchTime;
        public GamePhase phase = GamePhase.Landing;

        [Header("Wiring (可选，留空即按旧行为运行)")]
        [Tooltip("威胁值资产。与 WaveDirector 共持同一个资产，双方都不需要认识对方。")]
        public FloatVariable threatMeter;

        [Tooltip("挂上 WaveDirector 后，威胁累积与开波全部交给它，本类不再自己刷波。")]
        public WaveDirector waveDirector;

        [Tooltip("场上已有 EnemySpawnPoint 接管节奏时打开：威胁值照常累积（HUD 上看得到压力），"
            + "但不再由它触发自动开波 —— 两套节奏并存会让压力凭空翻倍，"
            + "而「刷怪节奏可配置」这个前提也就没了。\n\n"
            + "**故意用否定式命名**：老场景反序列化后落到 false，行为与引入本字段之前完全一致。"
            + "写成肯定式（autoWaves）的话，老场景会静默变成「永不自动开波」。")]
        public bool suppressAutoWaves;

        [Header("Debug")]
        [Tooltip("未接 threatMeter 时的威胁值存储。接了资产后这里不再参与运算。")]
        [SerializeField] private float threatFallback;

        /// <summary>
        /// 威胁值。接了 FloatVariable 就是全场唯一来源（HUD 读、WaveDirector 读、采矿建筑写），
        /// 没接就退回本类内部字段，保持旧行为。
        /// </summary>
        public float threat
        {
            get => threatMeter != null ? threatMeter.Value : threatFallback;
            set
            {
                if (threatMeter != null)
                    threatMeter.Value = value;
                else
                    threatFallback = value;
            }
        }

        /// <summary>开波阈值。交给 WaveDirector 时以它的 WaveTable 为准，避免两边阈值不一致互相打架。</summary>
        private float EffectiveWaveThreshold =>
            waveDirector != null && waveDirector.table != null ? waveDirector.table.waveThreshold : balance.waveThreshold;

        /// <summary>爆炸对玩家的伤害系数。策划案 21.3 要求"轻度误伤"，不允许一次误伤直接致死。</summary>
        private const float PlayerBlastRatio = 0.6f;

        private Health playerHealthCache;

        private readonly List<EnemyUnit> activeEnemies = new List<EnemyUnit>();
        private readonly List<Buildable> activeBuildings = new List<Buildable>();
        private Health coreHealth;
        private bool bossSpawned;
        private float passiveWaveTimer;
        private string hint = "";
        private float hintTimer;

        public GameBalanceConfig Balance => balance;
        public IReadOnlyList<EnemyUnit> ActiveEnemies => activeEnemies;
        public IReadOnlyList<Buildable> ActiveBuildings => activeBuildings;
        public string HintText => hintTimer > 0f ? hint : "";

        /// <summary>基地核心的血量组件。护盾发生器需要给它挂护盾，统一从这里取，避免各处重复 GetComponent。</summary>
        public Health CoreHealth => coreHealth;
        public float CoreHealth01 => coreHealth == null ? 0f : coreHealth.Normalized;
        public int PowerSupply { get; private set; }
        public int PowerDemand { get; private set; }

        /// <summary>当前挂着护盾的建筑数量，供 HUD 展示护盾发生器是否在起作用。</summary>
        public int ShieldedBuildingCount { get; private set; }
        public int RemainingSeconds => Mathf.Max(0, Mathf.CeilToInt(balance.evacuationStart + balance.evacuationDuration - matchTime));

        /// <summary>基地建筑容量点数上限（策划案 6.1 / 13 章固定为 20 点）。</summary>
        public int BuildingCapacity => balance != null ? balance.buildingCapacity : 0;

        /// <summary>
        /// 当前已占用的容量点数。建造中的建筑同样占点，
        /// 否则玩家可以连续放下满屏"未完工"建筑来绕过上限。
        /// </summary>
        public int UsedCapacity
        {
            get
            {
                int used = 0;
                for (int i = 0; i < activeBuildings.Count; i++)
                {
                    Buildable item = activeBuildings[i];
                    if (item == null || item.config == null || item.Health == null || item.Health.IsDead)
                        continue;
                    used += item.config.capacityCost;
                }
                return used;
            }
        }

        public int FreeCapacity => Mathf.Max(0, BuildingCapacity - UsedCapacity);

        public bool HasCapacityFor(BuildableConfig config)
        {
            return config != null && UsedCapacity + config.capacityCost <= BuildingCapacity;
        }

        private void Awake()
        {
            if (balance == null)
            {
                Debug.LogError("[StarDefenseGame] 缺少 GameBalanceConfig。", this);
                enabled = false;
                return;
            }

            resources = balance.startingResources;
            if (core != null)
            {
                coreHealth = core.GetComponent<Health>();
                if (coreHealth != null)
                {
                    coreHealth.Configure(1800f);
                    coreHealth.Died += _ => phase = GamePhase.Defeat;
                }
            }

            if (buildingRoot != null)
            {
                foreach (Buildable buildable in buildingRoot.GetComponentsInChildren<Buildable>())
                {
                    if (!activeBuildings.Contains(buildable))
                        activeBuildings.Add(buildable);
                }
            }

            SubscribeSpawnPoints();
        }

        /// <summary>
        /// 订阅刷怪点的开批事件，好让玩家知道"敌人从哪个方向来了"。
        ///
        /// 没订阅也不会出错 —— 它只是个提示，刷怪本身照常进行。
        /// 这里按 <see cref="spawnPoints"/> 去找组件而不是全场景搜索：那正是构建器
        /// 已经把刷怪点列进来的那个数组，用它就不需要任何 Find。
        /// </summary>
        private void SubscribeSpawnPoints()
        {
            if (spawnPoints == null)
                return;

            for (int i = 0; i < spawnPoints.Length; i++)
            {
                if (spawnPoints[i] == null)
                    continue;

                EnemySpawnPoint point = spawnPoints[i].GetComponent<EnemySpawnPoint>();
                if (point != null)
                    point.BatchQueued += OnSpawnPointBatchQueued;
            }
        }

        private void OnSpawnPointBatchQueued(EnemySpawnPoint point, int count)
        {
            if (point == null)
                return;

            ShowHint($"{point.displayName}：{count} 只敌人进入战场");
        }

        private void Update()
        {
            if (phase == GamePhase.Victory || phase == GamePhase.Defeat)
                return;

            matchTime += Time.deltaTime;
            hintTimer = Mathf.Max(0f, hintTimer - Time.deltaTime);
            UpdatePhase();
            UpdateThreat();
            UpdatePower();
        }

        private void UpdatePhase()
        {
            if (matchTime >= balance.evacuationStart + balance.evacuationDuration)
            {
                phase = coreHealth != null && !coreHealth.IsDead ? GamePhase.Victory : GamePhase.Defeat;
                return;
            }

            if (matchTime >= balance.evacuationStart)
                phase = GamePhase.Evacuation;
            else if (matchTime >= balance.bossStart)
                phase = GamePhase.BossFight;
            else if (matchTime >= balance.bossWarningStart)
                phase = GamePhase.BossWarning;
            else if (matchTime >= balance.defenseStart)
                phase = GamePhase.Defense;
            else if (matchTime >= balance.expansionStart)
                phase = GamePhase.Expansion;
            else
                phase = GamePhase.Landing;

            if (phase == GamePhase.BossFight && !bossSpawned)
            {
                bossSpawned = true;
                SpawnBoss();
            }
        }

        private void UpdateThreat()
        {
            // 交给 WaveDirector 时完全不碰威胁累积。两边都涨会让开波节奏直接翻倍。
            if (waveDirector != null)
                return;

            threat += balance.passiveThreatPerSecond * Time.deltaTime;

            // 由刷怪点接管节奏时，威胁值继续累积（它是"压力"的读数，HUD 在读），
            // 但不再由它开波 —— 否则定时刷怪点与自动波会叠加，节奏变成两套的和。
            if (suppressAutoWaves)
                return;

            passiveWaveTimer -= Time.deltaTime;
            if (passiveWaveTimer <= 0f && phase >= GamePhase.Defense)
            {
                passiveWaveTimer = phase == GamePhase.BossFight ? 22f : 35f;
                AddThreat(35f);
            }

            if (threat >= EffectiveWaveThreshold)
            {
                threat -= EffectiveWaveThreshold;
                SpawnWave();
            }
        }

        private void UpdatePower()
        {
            int supply = 100;
            int demand = 0;
            int shielded = 0;
            foreach (Buildable item in activeBuildings)
            {
                if (item == null || item.config == null || item.Health == null || item.Health.IsDead)
                    continue;

                if (item.Health.HasShield)
                    shielded++;

                if (item.constructed && !item.IsJammed)
                {
                    supply += item.config.powerSupply;
                    demand += item.config.powerCost;
                }
            }

            PowerSupply = supply;
            PowerDemand = demand;
            ShieldedBuildingCount = shielded;
            bool enough = supply >= demand;
            foreach (Buildable item in activeBuildings)
            {
                if (item != null)
                    item.SetPowered(enough || item.config.powerSupply > 0);
            }
        }

        public void AddResource(ResourceType type, int amount)
        {
            resources.Add(type, amount);
        }

        public void AddThreat(float amount)
        {
            threat = Mathf.Min(EffectiveWaveThreshold + 20f, threat + Mathf.Max(0f, amount));
        }

        public bool TryPlaceBuildable(BuildableConfig config, Vector3 position)
        {
            return TryPlaceBuildable(config, position, Quaternion.identity);
        }

        /// <summary>
        /// 摆放建筑。<paramref name="rotation"/> 由建造虚影给出（只含水平朝向），
        /// 在这里原样落到实例上 —— 虚影与实物必须用同一个朝向，否则预览就变成了误导。
        /// 能量墙这类长条建筑尤其明显：朝错了整排墙的方向都是歪的。
        /// </summary>
        public bool TryPlaceBuildable(BuildableConfig config, Vector3 position, Quaternion rotation)
        {
            if (config == null)
                return false;

            // 容量先于资源判定：容量是结构性上限，玩家更需要先知道"没地方放了"，而不是"没钱了"
            if (!HasCapacityFor(config))
            {
                ShowHint($"建筑容量不足：需要 {config.capacityCost} 点，剩余 {FreeCapacity}/{BuildingCapacity}");
                return false;
            }

            if (!resources.CanAfford(config.cost))
            {
                ShowHint("资源不足");
                return false;
            }

            resources.Spend(config.cost);
            AddThreat(balance.buildThreat);
            CreateBuilding(config, position, false, rotation);
            ShowHint($"开始建造：{config.displayName}");
            return true;
        }

        /// <summary>炮塔可动部件的名字特征。美术契约里转塔网格以 <c>_Head</c> 结尾，原点落在偏航轴上。</summary>
        private const string TurretHeadKeyword = "_Head";

        /// <summary>炮塔偏航轴的插槽名。仅在找不到 <c>_Head</c> 网格时作为退路。</summary>
        private const string TurretYawSocketName = "Socket_TurretYaw";

        /// <summary>炮口候选名。机枪塔是左右各一个，取左。</summary>
        private static readonly string[] MuzzleSocketNames = { "Socket_Muzzle", "Socket_Muzzle_L" };

        public Buildable CreateBuilding(BuildableConfig config, Vector3 position, bool completed)
        {
            return CreateBuilding(config, position, completed, Quaternion.identity);
        }

        /// <summary>
        /// 实例化一座建筑。
        ///
        /// 两条来源路径共用**同一套摆放契约** —— 原点在底面中心、按米 authored：
        ///   · 有美术 Prefab 就用资产；
        ///   · 没有就用 <see cref="ProcPrototypes"/> 拼一个可辨识的占位件。
        ///
        /// 原先那条"图元要抬起半个高度"的分支因此消失。它当年是靠"图元原点在几何中心"
        /// 这个事实活着的，一旦有人在程序化路径上复用它，建筑就会整座浮在半空 —— 而且不报错。
        /// 让两条路径从形状上就无法区分，比在注释里提醒下一个人要可靠。
        ///
        /// **但"位置契约相同"不等于"旋转契约相同"**：美术 Prefab 的根节点自带 -90° X 的
        /// Blender Z-up → Y-up 轴向校正，而程序化产物的根是 identity。所以姿态不能直接
        /// <c>SetPositionAndRotation(pos, yaw)</c> —— 那会把前者整个抹掉，几何仍按 Z-up 立着，
        /// 建筑于是**横躺**，且"原点在底面中心"跟着失效（落点也偏，像两个 bug 其实一个）。
        /// 这个差异收敛在 <see cref="BuildingPose.Apply"/> 一处，本方法不自己拼旋转。
        /// </summary>
        public Buildable CreateBuilding(BuildableConfig config, Vector3 position, bool completed, Quaternion rotation)
        {
            bool useArt = config.prefab != null;

            GameObject go = useArt
                ? Instantiate(config.prefab, buildingRoot)
                : ProcPrototypes.CreateBuilding(config, buildingRoot);

            // 占位件在 ProcPrototypes 里被主动摘掉了碰撞体 —— 那是给**建造虚影**让路
            // （虚影要能被自己的建造射线穿过）。但**实物**必须留下碰撞体，否则：
            //   · 虫子靠前方探测判断"撞到墙了"，没有碰撞体就是直接穿过去；
            //   · 玩家的枪与镐靠 Physics.Raycast 命中，没有碰撞体是打不中且不报错。
            // 虚影走的是另一条路径（不经过这里），所以这个补丁不会把虚影也弄成挡路的。
            //
            // 碰撞体形态随后由 Buildable 按建造进度切换：**没建完的走触发器**（不挡人、
            // 不被虫子当成障碍，但镐子仍能命中它继续施工），建成后才变回实体。见
            // Buildable.ApplyConstructionCollision。
            if (!useArt)
                EnsurePlaceholderCollider(go, config);

            // 基准朝向必须在任何赋值**之前**读：它就是要被保留下来的那份资产自带校正。
            Quaternion baseRot = BuildingPose.CaptureBase(go);
            BuildingPose.Apply(go, position, rotation, baseRot);
            go.name = config.displayName;

            Health health = go.GetComponent<Health>();
            if (health == null)
                health = go.AddComponent<Health>();

            Buildable buildable = go.GetComponent<Buildable>();
            if (buildable == null)
                buildable = go.AddComponent<Buildable>();

            buildable.Initialize(config, completed);
            activeBuildings.Add(buildable);

            if (config.isWeapon)
            {
                TurretDefense turret = go.GetComponent<TurretDefense>();
                if (turret == null)
                    turret = go.AddComponent<TurretDefense>();

                turret.game = this;

                if (useArt)
                {
                    // 优先旋转 _Head 网格本身，而不是 Socket_TurretYaw。
                    // 契约只保证 _Head 的原点落在偏航轴上，没有保证 Socket_TurretYaw 是它的父级；
                    // 两者若是兄弟节点，转插槽不会带动转塔，看上去就是"炮塔不转"。
                    if (turret.yawPivot == null)
                        turret.yawPivot = ResolveTurretYaw(go.transform);

                    if (turret.muzzle == null)
                        turret.muzzle = FindChildByName(go.transform, MuzzleSocketNames);
                }
                else
                {
                    GameObject muzzle = new GameObject("Muzzle");
                    muzzle.transform.SetParent(go.transform, false);
                    muzzle.transform.localPosition = Vector3.up * 0.75f + Vector3.forward * 0.7f;
                    turret.muzzle = muzzle.transform;
                }
            }
            else if (config.role == BuildableRole.ShieldEmitter)
            {
                if (go.GetComponent<ShieldEmitter>() == null)
                    go.AddComponent<ShieldEmitter>().game = this;
            }
            else if (config.role == BuildableRole.RepairAura)
            {
                if (go.GetComponent<RepairAura>() == null)
                    go.AddComponent<RepairAura>().game = this;
            }

            health.Died += _ => activeBuildings.Remove(buildable);
            return buildable;
        }

        /// <summary>
        /// 给程序化占位件补一个碰撞体。美术资产自带 MeshCollider，只有占位件需要这一步。
        ///
        /// 补出来的是**实体**碰撞体，但"是否挡路"由 <see cref="Buildable"/> 按建造进度决定 ——
        /// 施工中的建筑会被切成触发器（见 <c>Buildable.ApplyConstructionCollision</c>）。
        ///
        /// 尺度换算必须与 <see cref="ProcPrototypes.CreateBuilding"/> 逐字一致：
        /// 那边把"宽 × 高 × 深"压成"方形底面 × 高"（width = max(size.x, size.z)），
        /// 碰撞体按原样取 size.x/size.z 的话，底面 1×4 的占位件会得到一个 1×4 的盒子，
        /// 而它看起来是 4×4 —— 虫子于是能穿过视觉上明明挡着的部分。
        /// </summary>
        private static void EnsurePlaceholderCollider(GameObject go, BuildableConfig config)
        {
            if (go == null || go.GetComponent<Collider>() != null)
                return;

            Vector3 size = config != null ? config.size : new Vector3(2f, 2f, 2f);
            float width = Mathf.Max(0.4f, Mathf.Max(size.x, size.z));
            float height = Mathf.Max(0.4f, size.y);

            BoxCollider box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(width, height, width);

            // 占位件与美术资产共用"原点在底面中心"的契约，所以盒心要抬到半高。
            box.center = new Vector3(0f, height * 0.5f, 0f);
        }

        /// <summary>
        /// 给程序化占位敌人补一个碰撞体。尺寸对齐 <see cref="ProcPrototypes.CreateEnemyPlaceholder"/>
        /// 的腹部椭球 —— 那是模型上最宽的一节，也是子弹该打中的地方。
        ///
        /// 换算必须逐项照搬：<c>config.radius</c> 在配置里的语义是"碰撞/接触半径"、偏小，
        /// 那边乘了 1.70 才接近躯干实际宽度；这里按原值取会得到一个比模型小一圈的碰撞体，
        /// 表现是"看得见虫子、子弹从它身上穿过去"。
        /// </summary>
        private static void EnsurePlaceholderEnemyCollider(GameObject go, EnemyConfig config)
        {
            if (go == null || go.GetComponent<Collider>() != null)
                return;

            float radius = config != null ? Mathf.Max(0.3f, config.radius) : 0.8f;
            float body = config != null && config.isBoss ? 2.6f : radius * 1.7f;

            BoxCollider box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(body, body * 0.62f, body * 1.25f);
            box.center = new Vector3(0f, body * 0.42f, 0f);
        }

        // 图元时代的 CreatePrimitiveBody 已删除：它产出的是一根孤零零的圆柱/方块，
        // 和周围的美术资产摆在一起像一块没做完的占位物。取而代之的是
        // ProcPrototypes.CreateBuilding —— 同样的"美术缺席也能跑"回退语义，
        // 但产出的是有辨识度的组合体，且与美术资产共用同一套摆放契约。

        /// <summary>按候选名在子树里找插槽。找不到返回 null。</summary>
        private static Transform FindChildByName(Transform root, string[] candidates)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                Transform found = FindDescendant(root, candidates[i]);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>
        /// 解析炮塔的偏航轴：先找 <c>_Head</c> 网格（原点在偏航轴上，转它一定能带动转塔），
        /// 再退到 <c>Socket_TurretYaw</c> 插槽。都找不到返回 null，由 TurretDefense 退化成转自身。
        /// </summary>
        private static Transform ResolveTurretYaw(Transform root)
        {
            Transform head = FindDescendantContaining(root, TurretHeadKeyword);
            if (head != null)
                return head;

            return FindDescendant(root, TurretYawSocketName);
        }

        private static Transform FindDescendantContaining(Transform root, string fragment)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name.Contains(fragment))
                    return all[i];
            }
            return null;
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name == name)
                    return all[i];
            }
            return null;
        }

        /// <summary>
        /// 拆解建筑：返还部分建造成本并释放容量点。
        /// 容量是硬上限，如果没有主动拆除的出口，玩家一旦占满就再也无法调整防线布局，
        /// 这和策划案 13 章"根据战场情况安排建筑数量"的设计意图正好相反。
        /// </summary>
        public bool Demolish(Buildable target)
        {
            if (target == null || target.config == null)
                return false;
            if (!activeBuildings.Contains(target))
                return false;

            ResourceCost refund = ScaleCost(target.config.cost, balance.demolishRefundRatio);
            resources.Add(ResourceType.Metal, refund.metal);
            resources.Add(ResourceType.Energy, refund.energy);
            resources.Add(ResourceType.Crystal, refund.crystal);

            // 先移出列表再销毁：Destroy 是延迟执行，死亡回调不会走在销毁路径上
            activeBuildings.Remove(target);
            ShowHint($"拆解 {target.config.displayName}：容量 -{target.config.capacityCost}，返还 金属{refund.metal} 能源{refund.energy} 晶体{refund.crystal}");

            if (target.gameObject != null)
                Destroy(target.gameObject);
            return true;
        }

        private static ResourceCost ScaleCost(ResourceCost cost, float ratio)
        {
            return new ResourceCost(
                Mathf.FloorToInt(cost.metal * ratio),
                Mathf.FloorToInt(cost.energy * ratio),
                Mathf.FloorToInt(cost.crystal * ratio));
        }

        public EnemyUnit GetNearestEnemy(Vector3 origin, float range)
        {
            EnemyUnit best = null;
            float bestDistance = range;
            for (int i = activeEnemies.Count - 1; i >= 0; i--)
            {
                EnemyUnit enemy = activeEnemies[i];
                if (enemy == null || !enemy.IsAlive)
                {
                    activeEnemies.RemoveAt(i);
                    continue;
                }

                float distance = StarDefenseMath.HorizontalDistance(origin, enemy.transform.position);
                if (distance <= bestDistance)
                {
                    best = enemy;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>
        /// 敌人的目标选择。顺序是**基地优先，其次玩家**（需求原文）。
        ///
        /// 这里**故意不返回"附近最近的建筑"**。旧版会挑 10 m 内最近的建筑，
        /// 结果是虫子被路边随便一座墙吸引、绕着基地打转，"冲向基地"这件事根本不发生。
        /// 沿路遇到的建筑改由 <see cref="EnemyUnit"/> 的前方碰撞探测负责 ——
        /// 撞上就停下来拆掉它。于是"直奔基地"与"拆掉挡路的东西"是同一件事的两面，
        /// 不需要在目标列表里排优先级（也就不会有"两者都在附近时选谁"这种争不清楚的规则）。
        /// </summary>
        public Transform GetEnemyTarget(Vector3 enemyPosition, EnemyConfig config = null)
        {
            // 挖掘虫无视防线，直奔核心；它能钻出来就说明防线已经被绕过了
            if (config != null && config.behavior == EnemyBehaviorType.Burrower)
                return core;

            // 狙击虫按策划案优先点名玩家，逼采集和建造的人找掩体，而不是站着挨打。
            // 这是行为类型固有的差异，不是"优先级更高"——它只在玩家进入射程时才成立。
            if (config != null && config.behavior == EnemyBehaviorType.Sniper
                && IsPlayerInRange(enemyPosition, config.attackRange))
                return player;

            if (IsCoreTargetable())
                return core;

            // 基地已经没了：转去追玩家
            if (IsPlayerTargetable())
                return player;

            // 基地和玩家都不在了（正常流程里走不到：基地毁即判负，双方都不可打时场上已无威胁）。
            // 给一个不空转的兜底，而不是返回 null 让一批虫子原地发呆。
            Buildable fallback = NearestAliveBuilding(enemyPosition);
            return fallback != null ? fallback.transform : null;
        }

        /// <summary>基地是否还是有效目标。</summary>
        private bool IsCoreTargetable()
        {
            if (core == null)
                return false;

            // coreHealth 在 Awake 里取；取不到说明基地上没有 Health，那也是不能打的
            return coreHealth != null && !coreHealth.IsDead;
        }

        /// <summary>玩家是否还是有效目标（未倒地）。</summary>
        private bool IsPlayerTargetable()
        {
            Health target = ResolvePlayerHealth();
            return target != null && !target.IsDead;
        }

        /// <summary>
        /// 最近的一座**已建成且存活**的建筑。只用于基地与玩家都不存在时的兜底，
        /// 不参与正常的优先级（正常优先级是基地 → 玩家）。
        /// </summary>
        private Buildable NearestAliveBuilding(Vector3 from)
        {
            Buildable nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (Buildable building in activeBuildings)
            {
                if (building == null || building.Health == null || building.Health.IsDead || !building.constructed)
                    continue;

                float distance = StarDefenseMath.HorizontalDistance(from, building.transform.position);
                if (distance < nearestDistance)
                {
                    nearest = building;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        /// <summary>玩家是否在给定位置的有效范围内且尚未倒下。倒地中的玩家不再被追打。</summary>
        private bool IsPlayerInRange(Vector3 from, float range)
        {
            Health target = ResolvePlayerHealth();
            if (target == null || target.IsDead)
                return false;

            return StarDefenseMath.HorizontalDistance(from, player.position) <= range;
        }

        /// <summary>
        /// 懒加载缓存玩家 Health。狙击虫每帧都要判定玩家是否进入射程，
        /// 直接 GetComponent 会变成每帧每只虫一次场景查找。
        /// </summary>
        private Health ResolvePlayerHealth()
        {
            if (player == null)
                return null;

            if (playerHealthCache == null)
                playerHealthCache = player.GetComponent<Health>();

            return playerHealthCache;
        }

        public void EnemyAttack(EnemyUnit enemy, Transform target, float damage)
        {
            if (target == null)
                return;

            Health targetHealth = target.GetComponent<Health>();
            if (targetHealth == null)
                targetHealth = target.GetComponentInParent<Health>();
            if (targetHealth != null)
                targetHealth.TakeDamage(damage);
        }

        public void NotifyEnemyKilled(EnemyUnit enemy)
        {
            activeEnemies.Remove(enemy);
            AddResource(ResourceType.Metal, 4);
            if (Random.value < 0.2f)
                AddResource(ResourceType.Crystal, 1);
        }

        public void ShowHint(string text)
        {
            hint = text;
            hintTimer = 2.2f;
        }

        private void SpawnWave()
        {
            if (enemies == null || enemies.Length == 0 || spawnPoints == null || spawnPoints.Length == 0)
                return;

            int count = phase >= GamePhase.BossFight ? 12 : phase >= GamePhase.Defense ? 8 : 5;
            for (int i = 0; i < count; i++)
            {
                EnemyConfig config = enemies[Random.Range(0, enemies.Length)];

                // 只在 XZ 上抖 —— 原来用的 insideUnitSphere 会连 y 一起抖（±4 m），
                // 而 EnemyUnit 只推 XZ、不碰 y，那点初速度误差会永久留在敌人身上：
                // 表现就是一部分虫子整场悬在半空、另一部分陷进地里。
                Vector2 jitter = Random.insideUnitCircle * 4f;
                Vector3 spawn = spawnPoints[Random.Range(0, spawnPoints.Length)].position
                                + new Vector3(jitter.x, 0f, jitter.y);
                SpawnEnemy(config, spawn);
            }
            ShowHint($"虫群来袭：{count} 只");
        }

        private void SpawnBoss()
        {
            if (bossConfig == null || spawnPoints == null || spawnPoints.Length == 0)
                return;
            SpawnEnemy(bossConfig, spawnPoints[0].position + Vector3.right * 4f);
            ShowHint("Boss 已出现：行星巨兽");
        }

        /// <summary>场上存活敌人数量。IEnemySpawner 的实现，供 WaveDirector 判性能预算。</summary>
        public int AliveEnemyCount => activeEnemies.Count;

        /// <summary>
        /// IEnemySpawner 的实现。有美术 Prefab 就用资产，没有就用 <see cref="ProcPrototypes"/>
        /// 拼一个程序化占位件，保证某只虫的模型还没做出来时不阻塞玩法调试。
        ///
        /// 两条路径共用同一条契约 —— **原点在底面中心** —— 所以这里不再有坐标系换算，
        /// 落点一律就是地面高度。这个值仍要交给 <see cref="EnemyUnit"/> 逐帧复用：
        /// 地面有起伏后推进时只推 XZ，y 每帧重算，抬升量是那次重算的一部分。
        /// </summary>
        /// <param name="hpScale">血量倍率，供波次导演做逐波递增；直接刷怪时传 1。</param>
        public EnemyUnit SpawnEnemy(EnemyConfig config, Vector3 position, float hpScale = 1f)
        {
            if (config == null)
                return null;

            const float groundOffset = 0f;

            GameObject go = config.prefab != null
                ? Instantiate(config.prefab, enemyRoot)
                : ProcPrototypes.CreateEnemyPlaceholder(config, enemyRoot);

            // 与建筑那条路径同一个理由：占位件在 ProcPrototypes 里被统一摘掉了碰撞体
            // （那是给建造虚影让路的），但敌人没有碰撞体 = 玩家的枪打不中（Physics.Raycast）、
            // 也挡不住玩家的 CharacterController。美术资产自带 BoxCollider，只有占位件需要补。
            if (config.prefab == null)
                EnsurePlaceholderEnemyCollider(go, config);

            go.transform.position = PlanetTerrain.SnapToGround(position, groundOffset);

            Health health = go.GetComponent<Health>();
            if (health == null)
                health = go.AddComponent<Health>();

            EnemyUnit unit = go.GetComponent<EnemyUnit>();
            if (unit == null)
                unit = go.AddComponent<EnemyUnit>();

            unit.groundOffset = groundOffset;
            unit.Initialize(config, this, hpScale);
            activeEnemies.Add(unit);
            return unit;
        }

        /// <summary>
        /// 范围伤害，作用于范围内敌人，可选是否波及建筑。
        /// 自爆虫走这条路径并伤及建筑与同族，所以爆炸会波及它自己的集群；
        /// 炮塔溅射传 damageBuildings = false，避免自家火力拆自家墙。
        /// 用快照遍历，避免伤害致死时回调修改 activeEnemies 导致遍历失效。
        /// </summary>
        public void Explode(Vector3 center, float radius, float damage, Color color, bool damageBuildings = true)
        {
            EnemyUnit[] enemySnapshot = activeEnemies.ToArray();
            for (int i = 0; i < enemySnapshot.Length; i++)
            {
                EnemyUnit enemy = enemySnapshot[i];
                if (enemy == null || !enemy.IsAlive)
                    continue;

                if (StarDefenseMath.HorizontalDistance(center, enemy.transform.position) <= radius)
                    enemy.ApplyDamage(damage);
            }

            if (damageBuildings)
            {
                Buildable[] buildingSnapshot = activeBuildings.ToArray();
                for (int i = 0; i < buildingSnapshot.Length; i++)
                {
                    Buildable building = buildingSnapshot[i];
                    if (building == null || building.Health == null || building.Health.IsDead)
                        continue;

                    if (StarDefenseMath.HorizontalDistance(center, building.transform.position) <= radius)
                        building.Health.TakeDamage(damage);
                }
            }

            // 爆炸同样波及玩家（策划案 21.3 的"轻度误伤"），伤害打折，不会被一发带走
            Health playerTarget = ResolvePlayerHealth();
            if (playerTarget != null && !playerTarget.IsDead
                && StarDefenseMath.HorizontalDistance(center, player.position) <= radius)
                playerTarget.TakeDamage(damage * PlayerBlastRatio);

            SpawnBlast(center, radius, color);
        }

        /// <summary>干扰虫调用：瘫痪半径内所有已建成建筑，并给出可见反馈。</summary>
        public void JamBuildings(Vector3 center, float radius, float duration, Color color)
        {
            int jammed = 0;
            foreach (Buildable building in activeBuildings)
            {
                if (building == null || !building.constructed || building.Health == null || building.Health.IsDead)
                    continue;

                if (StarDefenseMath.HorizontalDistance(center, building.transform.position) > radius)
                    continue;

                building.Jam(duration);
                jammed++;
            }

            if (jammed > 0)
                ShowHint($"干扰虫瘫痪了 {jammed} 座建筑！");
        }

        private void SpawnBlast(Vector3 center, float radius, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Blast";

            Collider blastCollider = go.GetComponent<Collider>();
            if (blastCollider != null)
            {
                blastCollider.enabled = false;
                Destroy(blastCollider);
            }

            go.transform.position = center + Vector3.up * 0.6f;
            go.transform.localScale = Vector3.one * (radius * 1.6f);

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                Material material = new Material(Shader.Find("Standard"));
                material.color = Color.Lerp(color, Color.white, 0.55f);
                renderer.sharedMaterial = material;
            }

            Destroy(go, 0.25f);
        }

        public void SpawnTracer(Vector3 start, Vector3 end, Color color)
        {
            GameObject go = new GameObject("Tracer");
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPosition(0, start);
            line.SetPosition(1, end);
            line.startWidth = 0.04f;
            line.endWidth = 0.01f;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.startColor = color;
            line.endColor = new Color(color.r, color.g, color.b, 0f);
            Destroy(go, 0.08f);
        }
    }
}
