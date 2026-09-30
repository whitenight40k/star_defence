using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 建筑摆放姿态的唯一入口。
    ///
    /// 实物与虚影（<see cref="StarDefenseGame.CreateBuilding"/>、<see cref="BuildPreview"/>，**运行时**）
    /// 以及场景构建期的一次性摆放（<c>ArtPrefabLibrary.Instantiate</c>，**编辑器**）都必须走这里。
    /// 各处自己写一份的失败形态是"虚影和放下去的东西朝向不一致"，而那种不一致只在放置的
    /// 一瞬间可见，事后很难被发现。
    ///
    /// 之所以是 <c>public</c> 而不是 <c>internal</c>：编辑器程序集要调用它
    /// （Editor → Runtime 是 Unity 允许且推荐的依赖方向，反向不行）。
    /// </summary>
    public static class BuildingPose
    {
        /// <summary>
        /// 记下实例化后**尚未被写过**的根世界旋转。必须在 <c>Instantiate</c> 之后、
        /// 任何对 position / rotation 的赋值之前调用，否则拿到的已经是污染过的值。
        ///
        /// 为什么需要这个基准：<see cref="Apply"/> 会被虚影**每帧**调用，必须幂等。
        /// 每次都从这个基准重算，而不是在当前旋转上继续乘 —— 后者会让虚影每帧多转
        /// 一个 yaw，表现是"虚影自己在原地疯狂打转"。
        /// </summary>
        public static Quaternion CaptureBase(GameObject go)
        {
            return go != null ? go.transform.rotation : Quaternion.identity;
        }

        /// <summary>
        /// 把建筑摆到 <paramref name="position"/>，并叠加 <paramref name="yaw"/> 这个
        /// **世界空间水平偏航**。可重复调用而不累积。
        ///
        /// ── 为什么不能直接用 <c>transform.SetPositionAndRotation(position, yaw)</c> ──
        ///
        /// 因为两条来源的**根节点朝向约定不同**，而那句设的是**世界**旋转，
        /// 会把资产自带的那份校正整个抹掉：
        ///
        ///   · **美术 Prefab 的根节点带着 -90° X 旋转**。那是 Blender Z-up → Unity Y-up
        ///     的轴向校正。实证：<c>PFB_MachineGunTurret_A</c> / <c>PFB_TeslaTower_A</c> /
        ///     <c>PFB_EnergyWall_A</c> / <c>PFB_Core_A</c> 的 YAML 里都是
        ///     <c>m_LocalRotation = (x = -0.7071, w = 0.7071)</c>；而敌人资产
        ///     <c>PFB_Charger_A</c> 的根是 identity —— 敌人那条路径恰好只设 position
        ///     （朝向由 <see cref="EnemyUnit"/> 自己管），所以这个坑一直没在那边暴露。
        ///     校正是**资产的一部分，不是脏数据**：几何在 root-local 空间里是 Z = 高、
        ///     地面在 local Z = 0，"原点在底面中心"的契约和挂上去的碰撞体都建立在这上面。
        ///   · **ProcPrototypes 的产物**是在世界空间直接按 Y-up 拼出来的，根 rotation 是 identity。
        ///
        /// 一旦把世界旋转整个设成 yaw，美术件里那份 -90° 就没了：几何仍按 Z-up 立着，
        /// 于是整座建筑在 Y-up 的世界里**横躺**；同时"原点在底面中心"这个前提也失效
        /// （原点跑到了侧面），落点跟着偏 —— 看上去像"又歪又错位"两个 bug，其实只有一个。
        ///
        /// ── 正确做法 ──
        ///
        /// 只设位置，世界旋转一律**从 <paramref name="baseRotation"/> 重算**：
        /// <c>yaw × baseRotation</c>。yaw 为 identity 时是 no-op，所以两条来源共用这一份代码 ——
        /// 程序化件得到 <c>identity × identity = identity</c>，美术件得到 <c>yaw × (-90°X)</c>。
        ///
        /// <paramref name="yaw"/> 只该含水平分量：建筑不该跟着地形坡度倾斜（能量墙会被摆成
        /// 斜坡），清掉俯仰与横滚是调用方的责任。
        /// </summary>
        public static void Apply(GameObject go, Vector3 position, Quaternion yaw, Quaternion baseRotation)
        {
            if (go == null)
                return;

            Transform t = go.transform;
            t.position = position;

            // 注意是 `yaw * baseRotation`，不是 `yaw * t.rotation`。
            // 写成后者就是"在已有旋转上累积"，虚影每帧调一次 ⇒ 每帧多转一个 yaw。
            t.rotation = yaw * baseRotation;
        }
    }
}
