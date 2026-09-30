using UnityEngine;

namespace StarDefense
{
    public class PlayerToolController : MonoBehaviour
    {
        public StarDefenseGame game;
        public Camera playerCamera;
        public PlayerTool currentTool = PlayerTool.Gun;
        public int selectedBuildIndex = -1;
        public Transform weaponPivot;

        /// <summary>
        /// 建造虚影。留空时建造仍可用，只是没有预览（位置由本类自己打一条射线求出来）。
        /// </summary>
        public BuildPreview preview;

        /// <summary>
        /// 创造模式。开启后镐子的左键从"采集"变成"放置当前选中的建筑"。
        ///
        /// 刻意做成**独立于 <see cref="PlayerTool"/> 的第二层状态**，而不是给枚举加一个
        /// <c>Build</c> 值：工具决定"手上拿的是什么"（枪 / 镐），模式决定"拿着镐子在干什么"
        /// （采集 / 建造）。合成一个枚举的话，"切到枪再切回镐"就必然丢失玩家原本的模式，
        /// 而武器外观（<c>PlayerToolVisuals</c>）也只认识工具，不认识模式。
        /// </summary>
        public bool buildMode;

        /// <summary>
        /// 轮询的数字键个数（1–9），不是"建筑数量"。与 <see cref="BuildableConfig.MaxHotkey"/>
        /// 绑成同一个来源 —— 这两个数漂移的后果是"配置里能填、按不出来"，
        /// 而那种故障不报错，只能靠人肉发现。
        /// </summary>
        private const int HotkeyCount = BuildableConfig.MaxHotkey;

        private float fireTimer;
        private float swingTimer;
        private Vector3 weaponBaseLocalPosition;
        private Quaternion weaponBaseLocalRotation;

        private Animator animator;
        private bool drivesAttack;
        private bool drivesSpecial;
        private bool drivesHoldingGun;

        /// <summary>创造模式是否开启。UI 与虚影都读这个，不要各自去猜。</summary>
        public bool BuildMode => buildMode && currentTool == PlayerTool.Pickaxe;

        private void Awake()
        {
            if (playerCamera == null)
                playerCamera = GetComponentInChildren<Camera>();
            if (preview == null)
                preview = GetComponent<BuildPreview>();
            if (weaponPivot != null)
            {
                weaponBaseLocalPosition = weaponPivot.localPosition;
                weaponBaseLocalRotation = weaponPivot.localRotation;
            }
        }

        /// <summary>
        /// 开火与挥镐的动画由 Animator 的两个触发器驱动（见 <see cref="AnimatorParameters"/>）。
        /// 图元回退的玩家没有 Animator，老控制器里也可能没有这两个参数 —— 先探一次能力，
        /// 避免对着不存在的参数 SetTrigger 刷警告。探测放在 Start 而不是 Awake：
        /// Animator 的控制器引用要到组件初始化完成后才一定可用。
        /// 能力探测只做一次：Animator.parameters 每次访问都会拷一份数组出来。
        /// </summary>
        private void Start()
        {
            animator = GetComponentInChildren<Animator>();
            drivesAttack = AnimatorParameters.Has(
                animator,
                AnimatorParameters.Attack,
                AnimatorControllerParameterType.Trigger
            );
            drivesSpecial = AnimatorParameters.Has(
                animator,
                AnimatorParameters.Special,
                AnimatorControllerParameterType.Trigger
            );
            drivesHoldingGun = AnimatorParameters.Has(
                animator,
                AnimatorParameters.HoldingGun,
                AnimatorControllerParameterType.Float
            );

            // 这里必须显式写一次。HoldingGun 是三个地面状态混合树的**权重**，
            // 没人写它就是 0；而玩家的初始工具是枪（PlayerToolVisuals 也按 Gun 起步）。
            // 少了这一句的表现是：进场景后枪一直垂在身侧，切一次武器才抬起来 ——
            // 而"切一下就好了"正是最不容易被当成 bug 的形态。
            ApplyHoldingGun();
        }

        /// <summary>
        /// 把"手上是不是枪"告诉 Animator。
        ///
        /// 它不切换状态，只是三个地面状态那条 1D 混合树的权重（0 = 垂手 / 1 = 举枪），
        /// 所以切枪时枪是**平滑抬起**的，不会有一次瞬移；也正因为权重没有条件语义，
        /// 它没被写时的退化是"枪垂着"，而不是某条转移恒真。
        /// </summary>
        private void ApplyHoldingGun()
        {
            if (!drivesHoldingGun)
                return;

            animator.SetFloat(
                AnimatorParameters.HoldingGun,
                currentTool == PlayerTool.Gun ? 1f : 0f
            );
        }

        private void Update()
        {
            if (game == null || playerCamera == null)
                return;

            fireTimer = Mathf.Max(0f, fireTimer - Time.deltaTime);
            HandleToolAndModeInput();
            AnimateTool();

            if (Input.GetMouseButton(0))
                UsePrimary();

            // 右键：创造模式下一律理解为"退出创造模式"，否则只是取消建筑选择。
            // 不这么做的话，放下一座之后建筑还选着，玩家会以为自己仍在建造状态。
            if (Input.GetMouseButtonDown(1))
            {
                if (buildMode)
                    SetBuildMode(false);
                else
                    selectedBuildIndex = -1;
            }

            if (Input.GetKeyDown(KeyCode.E))
                TryRepair();

            if (Input.GetKeyDown(KeyCode.R))
                TryDemolish();
        }

        /// <summary>
        /// 工具切换（Q）、模式切换（X）、建筑选择（1-8 / 滚轮）。
        ///
        /// 这里的三条规则合起来就是本作的操作契约：
        ///   · **只有手持镐子才能进入创造模式**，拿枪时按 X 会被拒绝并说明原因；
        ///   · **只有按 X 才开启创造模式** —— 数字键不会"顺手"帮玩家切过去，
        ///     否则玩家想采个矿却按错了数字键，手里的镐子会突然开始往地上放东西；
        ///   · **切到枪械自动退出创造模式**，避免"手上是枪、状态是建造"这种自相矛盾。
        /// </summary>
        private void HandleToolAndModeInput()
        {
            if (Input.GetKeyDown(KeyCode.Q))
            {
                currentTool = currentTool == PlayerTool.Gun ? PlayerTool.Pickaxe : PlayerTool.Gun;

                if (currentTool == PlayerTool.Gun)
                {
                    selectedBuildIndex = -1;
                    if (buildMode)
                        SetBuildMode(false);
                    game.ShowHint("手持枪械：射击模式");
                }
                else
                {
                    game.ShowHint("手持镐子：按 X 切换 采集 / 创造");
                }

                // 写完提示再改姿态：枪的抬起 / 落下是切武器的结果，不是按钮的反馈。
                ApplyHoldingGun();
            }

            if (Input.GetKeyDown(KeyCode.X))
                ToggleBuildMode();

            HandleBuildSelection();
        }

        private void HandleBuildSelection()
        {
            if (game.buildables == null || game.buildables.Length == 0)
                return;

            if (currentTool != PlayerTool.Pickaxe || !buildMode)
            {
                // 非创造模式下按数字键不算错，只是"还没准备好"。用一条提示代替静默无效，
                // 否则玩家会以为是按键坏了。
                if (AnyHotkeyPressed())
                    game.ShowHint(
                        currentTool != PlayerTool.Pickaxe
                            ? "先按 Q 切换到镐子，再按 X 进入创造模式"
                            : "按 X 进入创造模式后才能选择建筑");

                return;
            }

            // 数字键按**配置里声明的 hotkey** 解析，不按数组下标。
            // 两者今天恰好一致（配置就是按 1-8 建的），但那是巧合不是契约：
            // 建造栏的槽位顺序哪天为了分组而调整一下，数组下标法就会集体错位一位，
            // 而且不报错 —— 玩家按 3 拿到的是 4 号建筑，只能靠自己发现。
            // hotkey 字段是配置里早已存在的显式声明，以它为准才没有这种耦合。
            for (int i = 0; i < HotkeyCount; i++)
            {
                if (!Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i)))
                    continue;

                int slot = FindBuildableByHotkey(i + 1);
                if (slot < 0)
                {
                    game.ShowHint($"没有绑定到数字键 {i + 1} 的建筑");
                    return;
                }

                selectedBuildIndex = slot;
                game.ShowHint($"已选择：{DescribeBuildable(game.buildables[slot])}");
                return;
            }

            // 滚轮循环切换。比数字键好用 —— 建筑有 8 种，靠记住编号挑是很难受的事。
            // 它按槽位顺序走，与建造栏从左到右看到的顺序一致。
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.01f)
            {
                int count = game.buildables.Length;
                int step = scroll > 0f ? 1 : -1;
                int start = selectedBuildIndex < 0 ? (step > 0 ? -1 : 0) : selectedBuildIndex;
                selectedBuildIndex = WrapIndex(start + step, count);
                game.ShowHint($"已选择：{DescribeBuildable(game.buildables[selectedBuildIndex])}");
            }
        }

        /// <summary>
        /// 找出声明了该热键的建筑在 <c>game.buildables</c> 里的下标。没有返回 -1。
        ///
        /// 重复热键时取靠前的一个并留日志：这不是"选哪个都行"的情况 ——
        /// 两个建筑抢同一个数字键意味着其中一个按不出来，必须让人看见。
        /// </summary>
        private int FindBuildableByHotkey(int hotkey)
        {
            int found = -1;
            for (int i = 0; i < game.buildables.Length; i++)
            {
                BuildableConfig config = game.buildables[i];
                if (config == null || config.hotkey != hotkey)
                    continue;

                if (found >= 0)
                {
                    Debug.LogWarning(
                        $"[PlayerToolController] 热键 {hotkey} 被多个建筑声明："
                        + $"{game.buildables[found].displayName} 与 {config.displayName}，已取前者。",
                        this);
                    continue;
                }

                found = i;
            }

            return found;
        }

        private static int WrapIndex(int index, int count)
        {
            if (count <= 0)
                return -1;

            int wrapped = index % count;
            return wrapped < 0 ? wrapped + count : wrapped;
        }

        private static bool AnyHotkeyPressed()
        {
            for (int i = 0; i < HotkeyCount; i++)
            {
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i)))
                    return true;
            }
            return false;
        }

        private string DescribeBuildable(BuildableConfig config)
        {
            if (config == null)
                return "无";

            return $"{config.displayName}（金属{config.cost.metal} 能源{config.cost.energy} 晶体{config.cost.crystal}"
                   + $" / 容量{config.capacityCost}）";
        }

        private void UsePrimary()
        {
            if (fireTimer > 0f)
                return;

            if (currentTool == PlayerTool.Gun)
            {
                fireTimer = game.Balance.gunFireInterval;
                swingTimer = 0.11f;
                PlayToolAnimation();
                ShootGun();
                return;
            }

            fireTimer = 0.32f;
            swingTimer = 0.25f;
            PlayToolAnimation();

            // 镐子有两种用途，由模式决定 —— 这也是"平时都是采集模式"的落点：
            // 没按过 X 就永远是采集，不会因为之前选过建筑就突然开始往地上盖东西。
            if (buildMode)
                TryPlaceBuildable();
            else
                TryGather();
        }

        /// <summary>
        /// 把这一下开火 / 挥镐告诉 Animator。
        ///
        /// 不需要在这里自己防重入：控制器里进入这两个动作链的连线是
        /// "从站立 / 行走 / 奔跑出发"，而动作链自身不是这三个状态，
        /// 所以链条跑到一半时那些条件根本不会被求值，想截断也截断不了。
        /// 反过来说，任何"挂 AnyState"的写法都会让 0.32 s 一次的挥击
        /// 把 1.21 s 的三拍挥镐链反复拉回第一拍，看起来是原地抽搐。
        /// </summary>
        private void PlayToolAnimation()
        {
            if (currentTool == PlayerTool.Gun)
            {
                if (drivesAttack)
                    animator.SetTrigger(AnimatorParameters.Attack);
                return;
            }

            if (drivesSpecial)
                animator.SetTrigger(AnimatorParameters.Special);
        }

        private void ShootGun()
        {
            Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (Physics.Raycast(
                    ray, out RaycastHit hit, game.Balance.gunRange,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                EnemyUnit enemy = hit.collider.GetComponentInParent<EnemyUnit>();
                if (enemy != null)
                    enemy.ApplyDamage(game.Balance.gunDamage);
                game.SpawnTracer(playerCamera.transform.position + playerCamera.transform.forward * 0.8f, hit.point, Color.yellow);
            }
            else
            {
                game.SpawnTracer(playerCamera.transform.position + playerCamera.transform.forward * 0.8f, ray.origin + ray.direction * game.Balance.gunRange, Color.yellow);
            }
        }

        private void TryGather()
        {
            Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            // 施工中的建筑只留下触发代理；这里必须显式 Collide，不能依赖工程全局开关。
            if (!Physics.Raycast(
                    ray, out RaycastHit hit, game.Balance.pickaxeRange,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
                return;

            Buildable buildable = hit.collider.GetComponentInParent<Buildable>();
            if (buildable != null && !buildable.constructed)
            {
                buildable.AddBuildProgress(0.35f);
                game.ShowHint($"建造中：{buildable.config.displayName} {Mathf.RoundToInt(buildable.BuildProgress01 * 100f)}%");
                return;
            }

            ResourceNode node = hit.collider.GetComponentInParent<ResourceNode>();
            if (node == null)
                return;

            int gained = node.Gather();
            if (gained > 0)
            {
                game.AddResource(node.resourceType, gained);
                game.AddThreat(game.Balance.miningThreat);
                game.ShowHint($"采集 {node.resourceType} +{gained}");
            }
        }

        private void TryPlaceBuildable()
        {
            if (selectedBuildIndex < 0 || selectedBuildIndex >= game.buildables.Length)
            {
                game.ShowHint("还没选建筑：创造模式下按 1-8 或滚轮挑选");
                return;
            }

            if (preview != null)
            {
                // 用同一帧的数据，不吃上一帧的残留 —— 玩家快速转身时这一帧的差别看得出来。
                preview.Refresh();

                if (!preview.HasPlacement)
                {
                    game.ShowHint("准星没有指向可建造的地面");
                    return;
                }

                // 「资源不足」与「容量不足」的具体原因由 TryPlaceBuildable 内部报出，
                // 这里只在虚影判定不可放置时覆盖一句更贴切的说明。
                if (!preview.IsValid && game.HasCapacityFor(game.buildables[selectedBuildIndex])
                                      && game.resources.CanAfford(game.buildables[selectedBuildIndex].cost))
                    game.ShowHint("这里放不下：地面太陡，换一处平坦的位置");

                game.TryPlaceBuildable(game.buildables[selectedBuildIndex], preview.Position, preview.Rotation);
                return;
            }

            // 无虚影组件时的退路（老场景）：自己打一条射线，判定与虚影保持一致。
            Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (!Physics.Raycast(
                    ray, out RaycastHit hit, 18f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return;

            if (hit.normal.y < 0.7f)
                return;

            game.TryPlaceBuildable(game.buildables[selectedBuildIndex], hit.point);
        }

        /// <summary>X 键入口。手拿枪械时拒绝，并说清怎样才能进入 —— 静默无反应会被当成按键坏了。</summary>
        private void ToggleBuildMode()
        {
            if (game == null)
                return;

            if (currentTool != PlayerTool.Pickaxe)
            {
                game.ShowHint("只有手持镐子才能进入创造模式（按 Q 切换工具）");
                return;
            }

            SetBuildMode(!buildMode);
        }

        /// <summary>
        /// 切换创造 / 采集模式。虚影的显隐完全由 <see cref="buildMode"/> 驱动，
        /// 所以这里切完要立刻让虚影重算一次 —— 否则退出创造模式后，
        /// 那团蓝色虚影会一直停到下一帧才消失。
        /// </summary>
        public void SetBuildMode(bool on)
        {
            if (buildMode == on)
                return;

            buildMode = on;

            // 索引保护：场景重建后建筑数量可能变过，越界取值会直接抛异常。
            int count = game != null && game.buildables != null ? game.buildables.Length : 0;
            if (on && count > 0 && (selectedBuildIndex < 0 || selectedBuildIndex >= count))
                selectedBuildIndex = 0;

            if (game != null)
            {
                game.ShowHint(
                    on
                        ? $"创造模式：{DescribeBuildable(CurrentBuildable())}　左键放置 / 滚轮或 1-8 切换 / X 或右键退出"
                        : "采集模式：左键采矿，或继续敲未完工的建筑");
            }

            if (preview != null)
                preview.Refresh();
        }

        /// <summary>当前选中的建筑配置。索引非法时返回 null，调用方不必自己判边界。</summary>
        public BuildableConfig CurrentBuildable()
        {
            if (game == null || game.buildables == null)
                return null;
            if (selectedBuildIndex < 0 || selectedBuildIndex >= game.buildables.Length)
                return null;

            return game.buildables[selectedBuildIndex];
        }

        private void TryRepair()
        {
            Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (!Physics.Raycast(
                    ray, out RaycastHit hit, game.Balance.interactRange,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
                return;

            Buildable buildable = hit.collider.GetComponentInParent<Buildable>();
            if (buildable != null && buildable.Health != null)
            {
                swingTimer = 0.2f;
                buildable.Health.Heal(24f);
                game.ShowHint("维修建筑 +24");
            }
        }

        /// <summary>
        /// 拆解准星指向的建筑，释放容量点并返还一半成本。
        /// 用与维修一致的交互距离，避免"能修不能拆"的手感割裂。
        /// </summary>
        private void TryDemolish()
        {
            Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (!Physics.Raycast(
                    ray, out RaycastHit hit, game.Balance.interactRange,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
                return;

            Buildable buildable = hit.collider.GetComponentInParent<Buildable>();
            if (buildable != null)
                game.Demolish(buildable);
        }

        private void AnimateTool()
        {
            if (weaponPivot == null)
                return;

            swingTimer = Mathf.Max(0f, swingTimer - Time.deltaTime);
            float t = swingTimer > 0f ? Mathf.Sin((1f - swingTimer / 0.25f) * Mathf.PI) : 0f;
            if (currentTool == PlayerTool.Gun)
            {
                weaponPivot.localPosition = weaponBaseLocalPosition + new Vector3(0f, 0f, -t * 0.08f);
                weaponPivot.localRotation = weaponBaseLocalRotation * Quaternion.Euler(-t * 8f, 0f, 0f);
            }
            else
            {
                weaponPivot.localPosition = weaponBaseLocalPosition + new Vector3(0.08f * t, -0.05f * t, 0f);
                weaponPivot.localRotation = weaponBaseLocalRotation * Quaternion.Euler(45f * t, 0f, -35f * t);
            }
        }
    }
}
