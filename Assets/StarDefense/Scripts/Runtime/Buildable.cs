using UnityEngine;

namespace StarDefense
{
    [RequireComponent(typeof(Health))]
    public class Buildable : MonoBehaviour
    {
        /// <summary>被干扰时染上的颜色，和"停机黄""建造中灰"区分开。</summary>
        private static readonly Color JammedColor = new Color(0.72f, 0.3f, 1f, 1f);

        /// <summary>被护盾覆盖时染上的颜色，让玩家看得出护盾发生器在起作用。</summary>
        private static readonly Color ShieldedColor = new Color(0.35f, 0.85f, 1f, 1f);

        /// <summary>Built-in 标准着色器的底色属性。用 PropertyToID 缓存，避免每帧做字符串哈希。</summary>
        private static readonly int BaseColorId = Shader.PropertyToID("_Color");

        /// <summary>URP/Lit 的底色属性。本工程当前是 Built-in，一起写上是为了将来换管线时不用回头找。</summary>
        private static readonly int UrpBaseColorId = Shader.PropertyToID("_BaseColor");

        public BuildableConfig config;
        public bool constructed;
        public bool powered = true;

        private Health health;
        private float progress;
        private float jamTimer;
        private Renderer[] renderers;
        private MaterialPropertyBlock propertyBlock;

        /// <summary>本实例层级里的全部碰撞体。美术件挂在子网格上，程序化占位件挂在根上，两处都要收。</summary>
        private Collider[] colliders;

        /// <summary>上面每个碰撞体**本来的** isTrigger 值，用于建成后精确还原。</summary>
        private bool[] colliderTriggers;

        /// <summary>
        /// 染色是否必须走 <see cref="MaterialPropertyBlock"/>。
        ///
        /// 判据是"这个实例的材质是否被别的对象共用"，不是"它有没有美术资产"：
        /// 美术图集材质（<c>MAT_Structure_Atlas</c>）和程序化占位件的调色板材质
        /// （<c>ProcPrototypes</c> 按颜色缓存的那几份）都是共享的，所以现在恒为 true。
        ///
        /// 保留这个字段而不是直接把代码写死，是因为判据本身值得留名 ——
        /// 将来若有人给某座建筑一份独占材质，这里换判据即可，不必回头翻刷新逻辑。
        /// </summary>
        private bool tintViaPropertyBlock = true;

        public Health Health => health;
        public float BuildProgress01 => config == null || config.buildSeconds <= 0f ? 1f : Mathf.Clamp01(progress / config.buildSeconds);

        /// <summary>被干扰虫瘫痪中，期间不工作、不供电。</summary>
        public bool IsJammed => jamTimer > 0f;

        public bool IsOperational => constructed && powered && !IsJammed && health != null && !health.IsDead;

        private void Awake()
        {
            health = GetComponent<Health>();
            renderers = GetComponentsInChildren<Renderer>();
            CacheColliders();

            // 订阅血量变化：护盾被击破和冷却回满都会走 Changed，
            // 这两个时刻正是需要刷新"是否染护盾色"的时刻，不必每帧轮询。
            if (health != null)
                health.Changed += OnHealthChanged;
        }

        private void OnDestroy()
        {
            if (health != null)
                health.Changed -= OnHealthChanged;
        }

        private void OnHealthChanged(Health changed, float current, float max) => RefreshVisuals();

        private void Update()
        {
            if (jamTimer <= 0f)
                return;

            jamTimer -= Time.deltaTime;
            if (jamTimer <= 0f)
                RefreshVisuals();
        }

        public void Initialize(BuildableConfig buildableConfig, bool startConstructed)
        {
            if (health == null)
                health = GetComponent<Health>();
            if (renderers == null || renderers.Length == 0)
                renderers = GetComponentsInChildren<Renderer>();
            CacheColliders();

            config = buildableConfig;
            gameObject.name = buildableConfig.displayName;

            // 这里**不再覆盖 transform.localScale**。
            //
            // 两条来源都已经按米 authored：美术资产是按真实尺寸导出的，
            // 程序化占位件是按 config.size 生成出来的。再乘一次 size 会让占位件被平方
            // （2×2×2 的发电机变成 4×4×4）、把 1.7 m 的机枪塔压成立方体比例，
            // 而且两个方向都不会报错。
            // config.size 现在的唯一用途就是"告诉程序化占位件该多大"，仅此一处。

            if (health != null)
                health.Configure(buildableConfig.maxHealth);

            constructed = startConstructed;
            progress = startConstructed ? buildableConfig.buildSeconds : 0f;
            ApplyConstructionCollision();
            RefreshVisuals();
        }

        public void AddBuildProgress(float seconds)
        {
            if (constructed || config == null)
                return;

            progress += Mathf.Max(0f, seconds);
            if (progress >= config.buildSeconds)
                constructed = true;

            ApplyConstructionCollision();
            RefreshVisuals();
        }

        /// <summary>
        /// 记下每个碰撞体的原始 isTrigger。**只采一次。**
        ///
        /// 重复采会把"施工中被改成触发器"的那个状态记成原始值，于是建成后还原成触发器 ——
        /// 建筑从此永远不挡路，而且全程不报错（表现为虫子穿墙、玩家穿墙，像碰撞体丢了）。
        /// </summary>
        private void CacheColliders()
        {
            if (colliders != null)
                return;

            colliders = GetComponentsInChildren<Collider>(true);
            colliderTriggers = new bool[colliders.Length];
            for (int i = 0; i < colliders.Length; i++)
                colliderTriggers[i] = colliders[i] != null && colliders[i].isTrigger;
        }

        /// <summary>
        /// 按建造进度切换碰撞体形态 —— **没建完的建筑不挡路**。
        ///
        /// 施工中把碰撞体改成**触发器**：玩家的 CharacterController 与敌人的前方探测都不理会触发器，
        /// 于是"进度不满的建筑"既不挡人、也不会被虫子当成障碍（虫子直接穿过去，
        /// 不会停下来拆一个还没成型的地基）。
        ///
        /// 而镐子 / 修理 / 拆除走的是 <c>Physics.Raycast</c>，工程设置里
        /// <c>m_QueriesHitTriggers = 1</c>（ProjectSettings/DynamicsManager.asset），**照样能命中**。
        /// 这一点是硬前提：施工进度的**唯一**来源就是"射线打中这座建筑"
        /// （见 <see cref="PlayerToolController"/> 的 <c>TryGather</c>），
        /// 把碰撞体整个移除会让建筑永远停在 0% —— 所以这里改的是 isTrigger，不是 enabled。
        ///
        /// 建成后按 <see cref="colliderTriggers"/> 的原值还原，而不是一律设回 false：
        /// 资产自带的触发器不该被这里顺手改掉。
        /// </summary>
        private void ApplyConstructionCollision()
        {
            if (colliders == null)
                return;

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider target = colliders[i];
                if (target == null)
                    continue;

                target.isTrigger = constructed || colliderTriggers[i];
            }
        }

        public void SetPowered(bool isPowered)
        {
            if (powered == isPowered)
                return;

            powered = isPowered;
            RefreshVisuals();
        }

        /// <summary>被干扰虫瘫痪指定秒数。重复干扰取较长者，不会互相缩短。</summary>
        public void Jam(float seconds)
        {
            if (seconds <= 0f || !constructed)
                return;

            jamTimer = Mathf.Max(jamTimer, seconds);
            RefreshVisuals();
        }

        private void RefreshVisuals()
        {
            if (renderers == null || config == null)
                return;

            Color color = config.color;
            if (!constructed)
                color = Color.Lerp(Color.black, color, 0.35f + 0.65f * BuildProgress01);
            else if (IsJammed)
                color = Color.Lerp(color, JammedColor, 0.6f);
            else if (!powered)
                color = Color.Lerp(color, Color.yellow, 0.45f);
            else if (health != null && health.HasShield)
                color = Color.Lerp(color, ShieldedColor, 0.5f);
            else
            {
                // 正常运转：白色叠加 = 不染。
                // 图集里已经画好了各部件的配色，用 config.color 去乘会把整座塔压成
                // 一个色相（机枪塔会被染成灰蓝），美术做的分色就白费了。
                // config.color 只用来表达异常状态（建造中 / 断电 / 被干扰 / 有护盾）。
                color = Color.white;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer target = renderers[i];
                if (target == null)
                    continue;

                if (tintViaPropertyBlock)
                {
                    // 材质是共享的（美术图集 / 占位件的调色板各只有一份），写 sharedMaterial.color
                    // 会同时改掉场上所有建筑，而且是把颜色直接写进材质资产 —— Play 一次脏一次。
                    // MaterialPropertyBlock 是逐渲染器的覆盖层，既不碰资产也不串染。
                    if (propertyBlock == null)
                        propertyBlock = new MaterialPropertyBlock();

                    target.GetPropertyBlock(propertyBlock);
                    propertyBlock.SetColor(BaseColorId, color);
                    propertyBlock.SetColor(UrpBaseColorId, color);
                    target.SetPropertyBlock(propertyBlock);
                    continue;
                }

                if (target.sharedMaterial != null)
                    target.sharedMaterial.color = color;
            }
        }
    }
}
