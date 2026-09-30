using UnityEngine;
using UnityEngine.Rendering;

namespace StarDefense
{
    /// <summary>
    /// 建造虚影：把"当前选中的建筑"以半透明蓝色显示在准星指向的落点上。
    ///
    /// 它只负责一件事 —— **让玩家在按下左键之前就能看见自己正在造什么、造在哪、能不能造**。
    /// 读取选择状态（<see cref="PlayerToolController.buildMode"/> / selectedBuildIndex）并自己
    /// 每帧刷新，放置动作仍由 <see cref="PlayerToolController"/> 发起，两边不互相调用流程。
    ///
    /// 三条硬约束，每一条漏掉都会变成"看起来能用但一用就错"的故障：
    ///
    /// 1. **虚影必须放在 Ignore Raycast 层。** 建造射线用的是 <c>Physics.Raycast</c> 的默认层掩码，
    ///    它包含 Ignore Raycast 之外的所有层。虚影若留在 Default，射线会打在自己身上 ——
    ///    表现是"虚影永远停在脚下第一帧的位置，且再也放不到远处"。
    ///
    /// 2. **虚影的碰撞体要销毁、所有 Behaviour 要禁用。** Prefab 上可能挂着会自己跑的逻辑组件，
    ///    碰撞体更会直接干扰射线与 CharacterController。
    ///
    /// 3. **虚影的朝向必须与放置时完全一致。** 这里用玩家水平朝向，放置时读同一个值传下去；
    ///    若两边各用一套（虚影朝前、实物朝北），虚影就从"预览"变成了欺骗。
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildPreview : MonoBehaviour
    {
        /// <summary>虚影所在的层。Unity 内置且固定索引为 2，默认射线不检测它。</summary>
        public const string GhostLayerName = "Ignore Raycast";

        private const string GhostObjectName = "BuildPreview_Ghost";

        [Header("Wiring")]
        [Tooltip("读取模式与选择。留空则在层级里向上找。")]
        public PlayerToolController tools;

        [Tooltip("用于询问资源与容量是否足够，以及取建筑配置。")]
        public StarDefenseGame game;

        [Tooltip("发射建造射线的相机。留空则用玩家层级里的主相机。")]
        public Camera playerCamera;

        [Header("Placement")]
        [Tooltip("虚影能投到的最远距离（米）。与放置距离共用同一个值。")]
        public float maxDistance = 18f;

        [Tooltip("可放置的最小地面坡度（法线 y）。低于此值视为墙面/斜坡，不允许放置。")]
        public float minGroundNormalY = 0.7f;

        [Header("Colors")]
        [Tooltip("可放置：亮蓝色虚影。")]
        public Color validColor = new Color(0.24f, 0.62f, 1f, 0.42f);

        [Tooltip("不可放置（资源不足 / 容量已满 / 地面太陡）：偏红的虚影。\n" +
                 "只有「能不能放」两种状态在颜色上区分开，玩家才不会对着一个放不下去的位置反复按左键。")]
        public Color invalidColor = new Color(1f, 0.34f, 0.30f, 0.36f);

        private GameObject ghost;
        private BuildableConfig ghostConfig;
        private Renderer[] ghostRenderers;
        private Material ghostMaterial;

        /// <summary>
        /// 虚影资产**自带的**根世界旋转（美术件是 -90° X 的 Blender Z-up → Y-up 校正，
        /// 程序化件是 identity），在 <see cref="EnsureGhost"/> 里实例化后立刻取一次。
        ///
        /// 必须存下来而不是每帧读当前值：<see cref="Refresh"/> 每帧重设旋转，
        /// 若在"当前旋转"上继续乘 yaw，虚影会每帧多转一点 —— 表现为原地疯狂打转。
        /// 存了基准，<see cref="BuildingPose.Apply"/> 就能每次从同一个起点重算，幂等。
        /// </summary>
        private Quaternion ghostBaseRotation = Quaternion.identity;
        private int ghostLayer = -1;

        /// <summary>当前位置是否真的放得下去（含坡度、资源、容量三项判定）。</summary>
        public bool IsValid { get; private set; }

        /// <summary>虚影当前所在的世界坐标。未命中地面时是上一次的值，配合 <see cref="HasPlacement"/> 使用。</summary>
        public Vector3 Position { get; private set; }

        /// <summary>虚影当前的朝向。放置时原样传给建造逻辑。</summary>
        public Quaternion Rotation { get; private set; }

        /// <summary>本帧准星是否打到了可用的地面。false 表示虚影已隐藏。</summary>
        public bool HasPlacement { get; private set; }

        public bool HasGhost => ghost != null;

        private void Awake()
        {
            Rotation = Quaternion.identity;

            if (tools == null)
                tools = GetComponentInParent<PlayerToolController>();
            if (playerCamera == null)
                playerCamera = GetComponentInChildren<Camera>();
            if (game == null && tools != null)
                game = tools.game;
        }

        private void OnDisable()
        {
            // 组件被关掉（比如玩家倒地时 SetControlEnabled(false)）不该留一个虚影悬在场上。
            ClearGhost();
            HasPlacement = false;
            IsValid = false;
        }

        private void OnDestroy()
        {
            ClearGhost();
        }

        private void Update()
        {
            Refresh();
        }

        /// <summary>
        /// 重新计算虚影的位置、朝向与有效性。
        /// 由 <see cref="Update"/> 每帧调用，也会在"按下左键的同一帧"被
        /// <see cref="PlayerToolController"/> 再调一次 —— 这样放置用的是当前帧的数据，
        /// 而不是上一帧的残留，玩家不会遇到"看着能放却提示放不下"。
        /// </summary>
        public void Refresh()
        {
            BuildableConfig config = ResolveConfig();
            if (config == null || playerCamera == null || game == null)
            {
                HideGhost();
                return;
            }

            EnsureGhost(config);

            Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            RaycastHit hit;
            // 半成品建筑只留下施工射线代理；摆放射线要忽略它，位置仍取真正的地面。
            if (!Physics.Raycast(
                    ray, out hit, maxDistance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                HideGhost();
                return;
            }

            Position = hit.point;
            // 只取水平朝向：建筑不该跟着俯仰角倾斜（能量墙会被摆成斜坡）。
            Rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            HasPlacement = true;

            bool flatEnough = hit.normal.y >= minGroundNormalY;
            bool affordable = game.resources.CanAfford(config.cost);
            bool hasRoom = game.HasCapacityFor(config);
            IsValid = flatEnough && affordable && hasRoom;

            if (ghost != null)
            {
                ghost.SetActive(true);
                BuildingPose.Apply(ghost, Position, Rotation, ghostBaseRotation);
            }

            ApplyColor(IsValid ? validColor : invalidColor);
        }

        /// <summary>当前该显示的配置。不在创造模式 / 没选建筑时返回 null，虚影随之隐藏。</summary>
        private BuildableConfig ResolveConfig()
        {
            // tools.enabled 也要看：玩家被击倒时 PlayerDownedState 会关掉控制器，
            // 但不会关掉本组件。少了这一条，倒地期间准星前面还会挂着一团虚影。
            if (tools == null || !tools.enabled || !tools.BuildMode || tools.currentTool != PlayerTool.Pickaxe)
                return null;
            if (game == null || game.buildables == null)
                return null;

            int index = tools.selectedBuildIndex;
            if (index < 0 || index >= game.buildables.Length)
                return null;

            return game.buildables[index];
        }

        private void EnsureGhost(BuildableConfig config)
        {
            if (ghost != null && ghostConfig == config)
                return;

            ClearGhost();

            // 有美术资产就用资产（虚影于是和实物完全同形），没有就用程序化占位件 ——
            // 两者遵守同一套"原点在底面中心"的契约。**但旋转契约不同**：美术件的根节点
            // 自带 -90° X 校正，程序化件没有。所以这里不能直接 SetPositionAndRotation，
            // 姿态一律交给 BuildingPose（实物走的是同一个方法，虚影与实物才不会转向不一致）。
            ghost = config.prefab != null
                ? Instantiate(config.prefab)
                : ProcPrototypes.CreateBuilding(config, null);
            ghost.name = GhostObjectName;

            // 在任何人碰过 transform 之前取基准 —— 这里正是"还没被污染"的那一刻。
            ghostBaseRotation = BuildingPose.CaptureBase(ghost);

            ghostLayer = LayerMask.NameToLayer(GhostLayerName);
            if (ghostLayer < 0)
                ghostLayer = 2; // Ignore Raycast 的内置索引，NameToLayer 查不到时按已知值兜底

            Transform[] all = ghost.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                all[i].gameObject.layer = ghostLayer;

            Collider[] colliders = ghost.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                ProcPrototypes.DestroyObject(colliders[i]);

            // 关掉虚影身上的所有行为组件：Prefab 上若有会自己转的部件（转塔、动画），
            // 让它们跑起来既浪费又会让虚影和实物在放置瞬间不同步。
            Behaviour[] behaviours = ghost.GetComponentsInChildren<Behaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
                behaviours[i].enabled = false;

            ghostRenderers = ghost.GetComponentsInChildren<Renderer>(true);
            ghostMaterial = CreateGhostMaterial();

            for (int i = 0; i < ghostRenderers.Length; i++)
            {
                if (ghostRenderers[i] != null && ghostMaterial != null)
                    ghostRenderers[i].sharedMaterial = ghostMaterial;
            }

            ghostConfig = config;
        }

        /// <summary>
        /// 一份半透明自发光材质给整个虚影用。
        ///
        /// 走 Built-in 标准着色器的 Transparent 模式而不是自写 shader：虚影要能被看清、
        /// 又不能挡住后面的地形，唯一的办法是 alpha 混合 + 关闭深度写入。
        /// 加一点自发光是为了在白昼沙漠的强光下，蓝色虚影不会淡到看不见。
        /// </summary>
        private static Material CreateGhostMaterial()
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null)
                return null;

            Material material = new Material(shader);
            material.hideFlags = HideFlags.DontSave;

            material.SetFloat("_Mode", 3f); // 3 = Transparent
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)RenderQueue.Transparent;

            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            return material;
        }

        private void ApplyColor(Color color)
        {
            if (ghostMaterial == null)
                return;

            ghostMaterial.color = color;
            ghostMaterial.SetColor("_EmissionColor", new Color(color.r, color.g, color.b, 1f) * 0.9f);
        }

        private void HideGhost()
        {
            HasPlacement = false;
            IsValid = false;

            if (ghost != null)
                ghost.SetActive(false);
        }

        private void ClearGhost()
        {
            if (ghost != null)
                ProcPrototypes.DestroyObject(ghost);

            ghost = null;
            ghostConfig = null;
            ghostRenderers = null;
            ghostBaseRotation = Quaternion.identity;

            if (ghostMaterial != null)
                ProcPrototypes.DestroyObject(ghostMaterial);

            ghostMaterial = null;
        }
    }
}
