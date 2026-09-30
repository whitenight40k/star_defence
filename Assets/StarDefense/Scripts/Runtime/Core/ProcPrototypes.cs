using System.Collections.Generic;
using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 程序化占位模型工厂。
    ///
    /// 存在的理由：美术资产按批次交付，总有一段时间某个玩法对象"有规则、没模型"。
    /// 旧做法是丢一个 <c>GameObject.CreatePrimitive</c> 出去 —— 一座雷达在场景里就是一根
    /// 光秃秃的圆柱，玩家一眼认出是占位物，而且**没法从剪影分辨它是什么建筑**。
    ///
    /// 这里用少量内置图元拼出可辨识的组合体：形状按 <see cref="BuildableConfig.id"/> 分派，
    /// 尺度取 <see cref="BuildableConfig.size"/>。所有产物遵守与美术资产**完全相同**的契约 ——
    /// 原点在底面中心、按米 authored —— 所以调用方不需要为它写第二套摆放代码，
    /// 将来换成真模型时摆放逻辑一行都不用改。
    ///
    /// 美术交付同名 Prefab 后，<c>StarDefenseAssetBinder</c> 会把它绑到
    /// <see cref="BuildableConfig.prefab"/> 上，运行时自然改走资产路径；
    /// 本工厂只在这些配置仍为空时被调用，所以它是一条**自我淘汰**的回退路径。
    /// </summary>
    public static class ProcPrototypes
    {
        /// <summary>
        /// 产物名字前缀。除了可读性，它还被两处用到：
        /// 场景构建器的"还剩多少图元"统计要靠它把占位件排除掉（占位件内部全是内置图元网格，
        /// 不排除的话报告里永远显示"还有 40 个图元"），
        /// 以及 <see cref="Buildable"/> 判断"这个实例的材质是共享的，染色必须走属性块"。
        /// </summary>
        public const string NamePrefix = "PROC_";

        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        /// <summary>
        /// 共享材质缓存。占位件的配色是固定几档，按颜色各建一份即可 ——
        /// 每个部件一份材质会让 8 座建筑多出上百个材质实例，且全是内存垃圾。
        /// </summary>
        private static readonly Dictionary<string, Material> MaterialCache = new Dictionary<string, Material>();

        // ── 调色板 ──────────────────────────────────────────────────────────
        private static readonly Color HullColor = new Color(0.56f, 0.59f, 0.63f);
        private static readonly Color DarkColor = new Color(0.23f, 0.26f, 0.30f);
        private static readonly Color ConcreteColor = new Color(0.47f, 0.45f, 0.42f);
        private static readonly Color WarningColor = new Color(1f, 0.52f, 0.15f);
        private static readonly Color RockColor = new Color(0.36f, 0.30f, 0.26f);

        /// <summary>产物的某个祖先（含自身）是否带着占位件前缀。</summary>
        public static bool IsPrototype(GameObject go)
        {
            if (go == null)
                return false;

            Transform t = go.transform;
            while (t != null)
            {
                if (t.name.StartsWith(NamePrefix, System.StringComparison.Ordinal))
                    return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// 按配置产出一座建筑的占位外观。根节点原点在底面中心，与美术资产契约一致。
        /// </summary>
        /// <param name="parent">父级。为 null 时留在场景根 —— 建造虚影走的就是这条路径。</param>
        public static GameObject CreateBuilding(BuildableConfig config, Transform parent)
        {
            Vector3 size = config != null ? config.size : new Vector3(2f, 2f, 2f);
            string id = config != null ? config.id : string.Empty;

            // size 是图元时代的"替代尺寸"，语义是"宽 × 高 × 深"。占位件的尺度全部由它推出来，
            // 这样改数值表就能让占位件跟着变，不需要再来这里改一遍硬编码。
            float width = Mathf.Max(0.4f, Mathf.Max(size.x, size.z));
            float height = Mathf.Max(0.4f, size.y);

            GameObject root = new GameObject(NamePrefix + (string.IsNullOrEmpty(id) ? "building" : id));
            if (parent != null)
                root.transform.SetParent(parent, false);

            switch (id)
            {
                case "radar":
                    BuildRadar(root.transform, width, height);
                    break;
                case "repair":
                    BuildRepairStation(root.transform, width, height);
                    break;
                case "shield":
                    BuildShieldGenerator(root.transform, width, height);
                    break;
                case "gen":
                    BuildPowerGenerator(root.transform, width, height);
                    break;
                default:
                    BuildGeneric(root.transform, width, height, config != null && config.isWeapon);
                    break;
            }

            return root;
        }

        /// <summary>
        /// 能量泉。它是唯一没有美术资产的环境资源点（金属矿用补给箱、晶体矿用晶簇），
        /// 此前是一个孤零零的 <c>PrimitiveType.Cylinder</c> —— 场景里那两根"圆柱"就是它。
        /// </summary>
        public static GameObject CreateEnergyGeyser(Transform parent, Color glowColor, float scale = 1f)
        {
            GameObject root = new GameObject(NamePrefix + "energy_geyser");
            if (parent != null)
                root.transform.SetParent(parent, false);

            Material rock = GetMaterial(RockColor);
            Material glow = GetMaterial(glowColor, true);
            Material shard = GetMaterial(Color.Lerp(glowColor, Color.white, 0.55f), true);
            float s = Mathf.Max(0.3f, scale);

            // 喷口基岩：一圈粗矮的火山口
            Cylinder(root.transform, new Vector3(0f, 0f, 0f), 1.05f * s, 0.36f * s, rock);
            Cylinder(root.transform, new Vector3(0f, 0.36f * s, 0f), 0.78f * s, 0.16f * s, rock);

            // 三根晶柱：高低错开并各自倾斜一点，避免看起来像复制粘贴
            Crystal(root.transform, new Vector3(0.34f * s, 0.5f * s, -0.16f * s), 0.17f * s, 1.45f * s, 7f, -5f, shard);
            Crystal(root.transform, new Vector3(-0.30f * s, 0.5f * s, 0.24f * s), 0.21f * s, 1.0f * s, -5f, 6f, shard);
            Crystal(root.transform, new Vector3(0.04f * s, 0.5f * s, 0.34f * s), 0.14f * s, 1.8f * s, 3f, 4f, glow);

            // 悬浮在柱顶的能量核与环，让资源点在天光下也自发光、远远就能认出来
            Cylinder(root.transform, new Vector3(0f, 1.15f * s, 0f), 0.82f * s, 0.10f * s, glow);
            Sphere(root.transform, new Vector3(0f, 1.95f * s, 0f), 0.34f * s, glow);

            return root;
        }

        /// <summary>
        /// 资源点的程序化占位件。
        ///
        /// 三种资源给三种明显不同的剪影 —— 玩家在几十米外扫一眼就该知道
        /// "那边是金属、跑一趟值不值"。之前能量泉是一根圆柱，和补给箱、晶簇放在一起，
        /// 从远处完全看不出是个资源点。
        /// </summary>
        public static GameObject CreateResourceNode(ResourceType type, Color color, Transform parent)
        {
            switch (type)
            {
                case ResourceType.Energy:
                    return CreateEnergyGeyser(parent, color);
                case ResourceType.Crystal:
                    return CreateCrystalCluster(parent, color);
                default:
                    return CreateMetalCache(parent, color);
            }
        }

        /// <summary>金属矿：一堆错落的补给箱，剪影是"方方正正的堆"。</summary>
        public static GameObject CreateMetalCache(Transform parent, Color color)
        {
            GameObject root = new GameObject(NamePrefix + "metal_cache");
            if (parent != null)
                root.transform.SetParent(parent, false);

            Material body = GetMaterial(Color.Lerp(color, Color.white, 0.12f));
            Material trim = GetMaterial(new Color(0.78f, 0.62f, 0.28f));

            Box(root.transform, new Vector3(0f, 0.45f, 0f), new Vector3(1.50f, 0.90f, 1.20f), body);
            Box(root.transform, new Vector3(-0.38f, 1.15f, 0.05f), new Vector3(0.70f, 0.50f, 0.80f), trim);
            Box(root.transform, new Vector3(0.40f, 1.10f, -0.10f), new Vector3(0.66f, 0.42f, 0.72f), body);

            GameObject top = Box(
                root.transform,
                new Vector3(0.02f, 1.52f, 0.08f),
                new Vector3(0.62f, 0.40f, 0.66f),
                trim);
            top.transform.localRotation = Quaternion.Euler(0f, 24f, 0f);

            return root;
        }

        /// <summary>晶体矿：一丛高低错落的晶柱，剪影是"竖着的尖"。</summary>
        public static GameObject CreateCrystalCluster(Transform parent, Color color)
        {
            GameObject root = new GameObject(NamePrefix + "crystal_cluster");
            if (parent != null)
                root.transform.SetParent(parent, false);

            Material rock = GetMaterial(RockColor);
            Material core = GetMaterial(color, true);
            Material shard = GetMaterial(Color.Lerp(color, Color.white, 0.40f), true);

            Cylinder(root.transform, Vector3.zero, 0.90f, 0.30f, rock);
            Crystal(root.transform, new Vector3(0f, 0.30f, 0f), 0.32f, 1.45f, 0f, 0f, core);
            Crystal(root.transform, new Vector3(0.42f, 0.30f, 0.18f), 0.20f, 0.90f, 12f, -9f, shard);
            Crystal(root.transform, new Vector3(-0.38f, 0.30f, -0.16f), 0.22f, 1.05f, -7f, 11f, shard);
            Crystal(root.transform, new Vector3(-0.18f, 0.30f, 0.42f), 0.16f, 0.72f, 8f, 6f, shard);
            Crystal(root.transform, new Vector3(0.26f, 0.30f, -0.40f), 0.15f, 0.62f, -10f, -5f, shard);

            return root;
        }

        /// <summary>
        /// 敌人的程序化占位件。目前只有搬运虫走到这条路 —— 它在台账里明确写着
        /// "等搬运玩法落地后再建模"，但波次表里已经排进去了，此前刷出来的是一颗光球。
        ///
        /// 不考虑骨架：所有 <c>ANM_</c> 动作还没做，敌人本来就只是"带血量的移动体"，
        /// 占位件只要剪影对（甲壳 + 六条腿）就够用，不必为了它先造一套骨骼。
        /// 原点同样在底面中心，所以 <c>groundOffset</c> 与美术资产共用同一个值 0。
        /// </summary>
        public static GameObject CreateEnemyPlaceholder(EnemyConfig config, Transform parent)
        {
            GameObject root = new GameObject(NamePrefix + "enemy");
            if (parent != null)
                root.transform.SetParent(parent, false);

            Color shellColor = config != null ? config.color : new Color(0.45f, 0.40f, 0.32f);
            bool boss = config != null && config.isBoss;
            float radius = config != null ? Mathf.Max(0.3f, config.radius) : 0.8f;

            // radius 在配置里是"碰撞/接触半径"，偏小；乘一个系数才接近虫子躯干的实际横向尺寸。
            float body = boss ? 2.60f : radius * 1.70f;

            Material plate = GetMaterial(shellColor);
            Material dark = GetMaterial(Color.Lerp(shellColor, Color.black, 0.48f));

            // 腹部：贴地压扁的椭球（球体网格直径 1，scale 即三轴直径）
            Part(
                root.transform,
                PrimitiveType.Sphere,
                new Vector3(0f, body * 0.42f, 0f),
                new Vector3(body, body * 0.62f, body * 1.25f),
                plate);

            // 背甲：比腹部小一圈、颜色更深，从上方看才分得出背和腹
            Part(
                root.transform,
                PrimitiveType.Sphere,
                new Vector3(0f, body * 0.66f, -body * 0.05f),
                new Vector3(body * 0.86f, body * 0.50f, body * 0.95f),
                dark);

            // 头：朝 +Z（与角色 / 建筑的正面朝向约定一致）
            Part(
                root.transform,
                PrimitiveType.Sphere,
                new Vector3(0f, body * 0.46f, body * 0.72f),
                new Vector3(body * 0.52f, body * 0.44f, body * 0.62f),
                plate);

            // 六条腿：三对，每对向外斜撑
            float legLength = body * 0.60f;
            float legThickness = Mathf.Max(0.05f, body * 0.10f);
            for (int i = 0; i < 3; i++)
            {
                float z = (i - 1) * body * 0.45f;
                for (int side = -1; side <= 1; side += 2)
                {
                    GameObject leg = Box(
                        root.transform,
                        new Vector3(side * body * 0.52f, legLength * 0.5f, z),
                        new Vector3(legThickness, legLength, legThickness),
                        dark);
                    leg.transform.localRotation = Quaternion.Euler(0f, 0f, -side * 24f);
                }
            }

            return root;
        }

        // ── 各建筑造型 ──────────────────────────────────────────────────────

        /// <summary>雷达：底盘 + 立柱 + 倾斜抛物面 + 馈源杆。剪影特征是"高杆顶一只斜盘"。</summary>
        private static void BuildRadar(Transform root, float w, float h)
        {
            Material hull = GetMaterial(HullColor);
            Material dark = GetMaterial(DarkColor);
            Material glow = GetMaterial(WarningColor, true);

            Box(root, new Vector3(0f, 0.11f, 0f), new Vector3(w, 0.22f, w), dark);
            Cylinder(root, new Vector3(0f, 0.22f, 0f), w * 0.14f, h * 0.62f, hull);

            float platformY = 0.22f + h * 0.62f;
            Cylinder(root, new Vector3(0f, platformY, 0f), w * 0.24f, 0.14f, dark);

            // 抛物面：用一枚扁圆柱斜置代替，够读出"雷达盘"这个剪影
            GameObject dish = Cylinder(root, new Vector3(0f, 0f, w * 0.06f), w * 0.52f, 0.09f, glow);
            dish.transform.localPosition = new Vector3(0f, h - 0.42f, w * 0.10f);
            dish.transform.localRotation = Quaternion.Euler(64f, 0f, 0f);

            Box(root, new Vector3(0f, h - 0.72f, 0f), new Vector3(w * 0.06f, 0.62f, w * 0.06f), hull);
            Sphere(root, new Vector3(0f, h + 0.04f, 0f), 0.16f, glow);
        }

        /// <summary>维修站：方舱 + 顶棚 + 侧伸吊臂 + 正面十字标。</summary>
        private static void BuildRepairStation(Transform root, float w, float h)
        {
            Material hull = GetMaterial(HullColor);
            Material dark = GetMaterial(DarkColor);
            Material concrete = GetMaterial(ConcreteColor);
            Material glow = GetMaterial(new Color(0.42f, 0.85f, 0.46f), true);

            Box(root, new Vector3(0f, 0.10f, 0f), new Vector3(w, 0.20f, w), concrete);
            Box(root, new Vector3(0f, 0.20f + h * 0.30f, 0f), new Vector3(w * 0.78f, h * 0.60f, w * 0.78f), hull);
            Box(root, new Vector3(0f, 0.20f + h * 0.64f, 0f), new Vector3(w, 0.12f, w), dark);

            // 吊臂沿 +Z 伸出，末端一只吊钩 —— 侧影上一眼能看出是维修/工程设施
            Box(root, new Vector3(0f, 0.20f + h * 0.50f, w * 0.42f), new Vector3(0.16f, 0.16f, w * 0.62f), dark);
            Sphere(root, new Vector3(0f, 0.20f + h * 0.30f, w * 0.66f), 0.18f, hull);

            // 正面十字标（朝 -Z，与角色/建筑的正面朝向约定一致）
            float face = -w * 0.40f;
            Box(root, new Vector3(0f, 0.20f + h * 0.30f, face), new Vector3(0.10f, h * 0.34f, 0.06f), glow);
            Box(root, new Vector3(0f, 0.20f + h * 0.30f, face), new Vector3(w * 0.34f, 0.10f, 0.06f), glow);
        }

        /// <summary>护盾发生器：底座 + 中柱 + 悬浮环 + 能量穹顶。</summary>
        private static void BuildShieldGenerator(Transform root, float w, float h)
        {
            Material hull = GetMaterial(HullColor);
            Material concrete = GetMaterial(ConcreteColor);
            Material glow = GetMaterial(new Color(0.35f, 0.72f, 1f), true);

            Box(root, new Vector3(0f, h * 0.14f, 0f), new Vector3(w, h * 0.28f, w), concrete);

            float columnBase = h * 0.28f;
            Cylinder(root, new Vector3(0f, columnBase, 0f), w * 0.16f, h * 0.34f, hull);

            // 四根斜撑：把"塔"读成"发生器支架"，而不是又一根柱子
            for (int i = 0; i < 4; i++)
            {
                float angle = 45f + i * 90f;
                float rad = angle * Mathf.Deg2Rad;
                GameObject brace = Box(
                    root,
                    new Vector3(Mathf.Sin(rad) * w * 0.34f, h * 0.30f, Mathf.Cos(rad) * w * 0.34f),
                    new Vector3(0.08f, h * 0.42f, 0.08f),
                    hull);
                brace.transform.localRotation = Quaternion.Euler(Mathf.Cos(rad) * 18f, 0f, -Mathf.Sin(rad) * 18f);
            }

            float ringY = columnBase + h * 0.36f;
            Cylinder(root, new Vector3(0f, ringY, 0f), w * 0.46f, h * 0.06f, glow);
            Sphere(root, new Vector3(0f, ringY + h * 0.10f, 0f), w * 0.60f, glow);
        }

        /// <summary>发电机：机箱 + 散热鳍 + 双排气管 + 指示灯。</summary>
        private static void BuildPowerGenerator(Transform root, float w, float h)
        {
            Material hull = GetMaterial(HullColor);
            Material dark = GetMaterial(DarkColor);
            Material concrete = GetMaterial(ConcreteColor);
            Material glow = GetMaterial(new Color(1f, 0.82f, 0.28f), true);

            Box(root, new Vector3(0f, h * 0.06f, 0f), new Vector3(w, h * 0.12f, w), concrete);
            Box(root, new Vector3(0f, h * 0.12f + h * 0.28f, 0f), new Vector3(w * 0.84f, h * 0.56f, w * 0.84f), hull);

            // 散热鳍：贴在 -Z 面外侧，竖着排四片
            float finFace = -w * 0.44f;
            for (int i = 0; i < 4; i++)
            {
                Box(
                    root,
                    new Vector3(0f, h * 0.24f + i * h * 0.10f, finFace),
                    new Vector3(w * 0.62f, 0.05f, 0.10f),
                    dark);
            }

            // 排气管：顶部两侧各一根，高度不一
            Cylinder(root, new Vector3(w * 0.28f, h * 0.68f, -w * 0.18f), 0.10f, h * 0.46f, dark);
            Cylinder(root, new Vector3(-w * 0.28f, h * 0.68f, -w * 0.18f), 0.10f, h * 0.36f, dark);

            Sphere(root, new Vector3(0f, h * 0.70f, w * 0.30f), 0.15f, glow);
        }

        /// <summary>
        /// 兜底造型。既用于映射表里没有的 id，也用于"表里有、但这次是新加的一种建筑"——
        /// 有剪影总比一根圆柱强，而且它同样遵守底面原点契约。
        /// </summary>
        private static void BuildGeneric(Transform root, float w, float h, bool weapon)
        {
            Material hull = GetMaterial(HullColor);
            Material dark = GetMaterial(DarkColor);
            Material concrete = GetMaterial(ConcreteColor);

            Box(root, new Vector3(0f, h * 0.06f, 0f), new Vector3(w, h * 0.12f, w), concrete);
            Box(root, new Vector3(0f, h * 0.12f + h * 0.30f, 0f), new Vector3(w * 0.72f, h * 0.60f, w * 0.72f), hull);
            Box(root, new Vector3(0f, h * 0.76f, 0f), new Vector3(w * 0.84f, h * 0.10f, w * 0.84f), dark);

            if (!weapon)
                return;

            // 火力建筑：加一根水平炮管，方向沿用"正面朝 +Z"的约定
            GameObject barrel = Cylinder(
                root,
                new Vector3(0f, 0f, 0f),
                0.11f,
                w * 0.62f,
                dark);
            barrel.transform.localPosition = new Vector3(0f, h * 0.62f, w * 0.55f);
            barrel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        }

        // ── 图元辅助 ────────────────────────────────────────────────────────
        //
        // 内置图元网格的原始尺寸各不相同，这是最容易写错、又最不容易被发现的地方：
        //   · Cube     1×1×1（边长 1，局部跨 ±0.5）
        //   · Sphere   直径 1（局部跨 ±0.5）
        //   · Cylinder 直径 1、**高 2**（局部 y 跨 ±1），所以高度方向的缩放要除以 2
        // 下面的包装函数把换算收在内部，参数一律用"真实尺寸"，调用点不必再想这件事。

        private static GameObject Box(Transform parent, Vector3 center, Vector3 size, Material material)
        {
            return Part(parent, PrimitiveType.Cube, center, size, material);
        }

        /// <summary>竖直圆柱。<paramref name="bottom"/> 是底面中心，不是几何中心。</summary>
        private static GameObject Cylinder(Transform parent, Vector3 bottom, float radius, float height, Material material)
        {
            return Part(
                parent,
                PrimitiveType.Cylinder,
                bottom + Vector3.up * (height * 0.5f),
                new Vector3(radius * 2f, height * 0.5f, radius * 2f),
                material);
        }

        private static GameObject Sphere(Transform parent, Vector3 center, float diameter, Material material)
        {
            return Part(parent, PrimitiveType.Sphere, center, Vector3.one * diameter, material);
        }

        /// <summary>倾斜的晶柱。<paramref name="bottom"/> 是底面中心。</summary>
        private static GameObject Crystal(Transform parent, Vector3 bottom, float radius, float height, float tiltX, float tiltZ, Material material)
        {
            GameObject go = Cylinder(parent, bottom, radius, height, material);
            go.transform.localRotation = Quaternion.Euler(tiltX, 0f, tiltZ);
            return go;
        }

        private static GameObject Part(Transform parent, PrimitiveType type, Vector3 localPosition, Vector3 localScale, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = "part_" + type.ToString().ToLowerInvariant();
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;

            // 占位件不参与碰撞与射线：建造虚影要能被自己的建造射线穿过，
            // 兜底建筑的碰撞由 BuildableConfig / 上层补，留一个内置盒体会让"点不中"和"挡路"同时发生。
            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
                DestroyObject(collider);

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null && material != null)
                renderer.sharedMaterial = material;

            return go;
        }

        /// <summary>
        /// 按当前是否在播放模式选择销毁方式。写成一处是因为这个坑很容易漏：
        /// 编辑期用 <c>Object.Destroy</c> 是**延迟到下一帧**的，在场景构建器里等于没删 ——
        /// 生成的占位件会带着一堆内置碰撞体进场景，而且全程不报错。
        /// </summary>
        public static void DestroyObject(Object target)
        {
            if (target == null)
                return;

            if (Application.isPlaying)
                Object.Destroy(target);
            else
                Object.DestroyImmediate(target);
        }

        private static Material GetMaterial(Color color, bool emissive = false)
        {
            string key = ColorUtility.ToHtmlStringRGBA(color) + (emissive ? "+e" : string.Empty);

            Material cached;
            if (MaterialCache.TryGetValue(key, out cached) && cached != null)
                return cached;

            Shader shader = Shader.Find("Standard");
            if (shader == null)
                shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");

            if (shader == null)
            {
                Debug.LogWarning("[ProcPrototypes] 找不到可用着色器，占位件将使用默认材质。");
                return null;
            }

            Material material = new Material(shader);

            // **编辑期创建的材质必须允许保存。**
            //
            // 场景构建器会在编辑期调用本工厂，产出的占位件要写进 .unity 文件；它们引用的
            // 材质如果被标了 DontSave，保存场景时引用就会断掉 —— 重新打开工程后占位件
            // 是清一色的洋红（丢失材质的默认表现），而构建日志里一切正常。
            //
            // 播放期创建的材质只服务当次会话，才反过来标记 DontSave，
            // 免得在编辑器里反复 Play 之后堆出一批无名材质资产。
            if (Application.isPlaying)
                material.hideFlags = HideFlags.DontSave;

            material.color = color;

            if (emissive)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor(EmissionId, color * 1.6f);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            MaterialCache[key] = material;
            return material;
        }
    }
}
