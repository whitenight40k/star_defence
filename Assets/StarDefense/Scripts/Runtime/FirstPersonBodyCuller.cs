using System.Collections.Generic;
using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 第一人称自身渲染剔除。
    ///
    /// **它修的是"低头看见自己头顶/护目镜内壁"这类穿模。**
    ///
    /// 排查结论（先量数据再下结论，没有改参数）：
    ///   · 相机眼位取自 <c>Socket_Camera</c>，实测 local = (0.004, 1.3315, 0.109)，
    ///     与美术契约的 (0, 1.335, 0.100) 吻合 —— 位置本身没有问题；
    ///   · 近裁剪面 0.10 —— 只够裁掉眼位正前方 10 cm 以内的面片；
    ///   · 相机 cullingMask 是全开的，**角色自己那一整块蒙皮网格照常参与渲染**。
    ///
    /// 于是"眼位在头骨内部 + 整块身体网格可见"必然导致：抬头看到头盔内壁、
    /// 低头看到护目镜框和下巴。这不是近裁剪面调大一点能解决的 —— 头骨半径本来就和
    /// 眼位到前脸的距离同一量级，把近裁剪面推到 0.3 m 只会连脖子和胸口一起裁掉，
    /// 换一种穿模而已。
    ///
    /// 正解是把"自己的身体"从第一人称相机的可见集合里移出去：
    ///   · 身体网格移到 <c>PlayerBody</c> 层，相机 cullingMask 清掉该层；
    ///   · **不改 Renderer.enabled**，所以影子照常投射 —— 在白昼沙漠里，
    ///     突然没了自己的影子比穿模更让人出戏；
    ///   · 手里的武器挂在 <c>Socket_RightHandTool</c> 上，不在身体网格里，
    ///     保持 Default 层，第一人称仍然看得到枪和镐在动。
    ///
    /// 只对本实例生效：队友是同一个 Prefab 的另一个实例，没有被挂上本组件，
    /// 所以第三人称视角下队友和玩家的身体都照常渲染。
    ///
    /// 层不存在时（工程被重置、TagManager 被改）退化为 <c>Renderer.forceRenderingOff</c>：
    /// 代价是自身的影子会一起消失，但**绝不会留着穿模**。这是刻意的取舍 ——
    /// 视觉退化看得见，穿模看不见却一直存在。
    /// </summary>
    [DisallowMultipleComponent]
    public class FirstPersonBodyCuller : MonoBehaviour
    {
        /// <summary>承载"自己的可见身体"的层名。由 <c>ProjectLayerSetup</c> 在构建管线里占好槽位。</summary>
        public const string BodyLayerName = "PlayerBody";

        /// <summary>
        /// 武器对象名兜底。正常情况下武器引用来自 <see cref="PlayerToolVisuals"/>，
        /// 这组片段只在组件缺失时使用 —— 名字取自美术契约（<c>GEO_HeldGun</c> / <c>GEO_HeldPickaxe</c>）。
        /// </summary>
        private static readonly string[] WeaponNameFragments = { "HeldGun", "HeldPickaxe", "WPN_" };

        [Header("Wiring")]
        [Tooltip("第一人称相机。留空则在自身层级里找。")]
        public Camera playerCamera;

        [Tooltip("提供枪与镐的引用，用来把它们排除在剔除范围之外。留空则向上找。")]
        public PlayerToolVisuals toolVisuals;

        [Tooltip("关掉则保留自己的身体参与渲染（会重新出现穿模，只用于对比排查）。")]
        public bool hideOwnBody = true;

        private readonly List<Transform> keptRoots = new List<Transform>();

        private void Awake()
        {
            Apply();
        }

        private void Apply()
        {
            if (playerCamera == null)
                playerCamera = GetComponentInChildren<Camera>();

            if (!hideOwnBody)
            {
                Debug.Log("[FirstPersonBodyCuller] 按配置保留自身身体渲染（穿模属预期）。", this);
                return;
            }

            if (playerCamera == null)
            {
                Debug.LogWarning(
                    "[FirstPersonBodyCuller] 找不到相机，无法剔除自身渲染，低头穿模会继续存在。",
                    this);
                return;
            }

            if (toolVisuals == null)
                toolVisuals = GetComponentInChildren<PlayerToolVisuals>();

            CollectKeptRoots();

            int bodyLayer = LayerMask.NameToLayer(BodyLayerName);
            bool useLayer = bodyLayer >= 0;

            if (!useLayer)
            {
                Debug.LogWarning(
                    $"[FirstPersonBodyCuller] 工程里没有 {BodyLayerName} 层（TagManager 被重置过？），"
                        + "退化为逐渲染器关闭绘制：穿模不会出现，但自身的影子也会一起消失。"
                        + "重跑一次 Star Defense/★ 全流程 可以自动把层补回来。",
                    this);
            }

            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            int hidden = 0;
            int kept = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                if (IsKept(renderer.transform))
                {
                    kept++;
                    continue;
                }

                if (useLayer)
                    renderer.gameObject.layer = bodyLayer;
                else
                    renderer.forceRenderingOff = true;

                hidden++;
            }

            if (useLayer)
                playerCamera.cullingMask &= ~(1 << bodyLayer);

            Debug.Log(
                $"[FirstPersonBodyCuller] 第一人称自身剔除：{hidden} 个渲染器"
                    + (useLayer ? $"移入 {BodyLayerName} 层并已从相机掩码移除（影子保留）" : "已关闭绘制（影子一并消失）")
                    + $"；保留 {kept} 个（枪 / 镐）。",
                this);
        }

        /// <summary>收集"必须继续可见"的子树根：手里的枪与镐。</summary>
        private void CollectKeptRoots()
        {
            keptRoots.Clear();

            if (toolVisuals != null)
            {
                if (toolVisuals.gunVisual != null)
                    keptRoots.Add(toolVisuals.gunVisual.transform);
                if (toolVisuals.pickaxeVisual != null)
                    keptRoots.Add(toolVisuals.pickaxeVisual.transform);
            }

            if (keptRoots.Count > 0)
                return;

            // 组件缺失时的退路：按名字找手持物。找不到也不报错 ——
            // 结果只是"武器也一起被剔掉了"，不影响穿模这个主要目标。
            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                for (int f = 0; f < WeaponNameFragments.Length; f++)
                {
                    if (all[i].name.Contains(WeaponNameFragments[f]))
                    {
                        keptRoots.Add(all[i]);
                        break;
                    }
                }
            }
        }

        private bool IsKept(Transform target)
        {
            for (int i = 0; i < keptRoots.Count; i++)
            {
                Transform kept = keptRoots[i];
                if (kept == null)
                    continue;

                Transform current = target;
                while (current != null)
                {
                    if (current == kept)
                        return true;
                    current = current.parent;
                }
            }
            return false;
        }
    }
}
