using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 动画控制器参数名的**唯一来源**，外加"控制器里到底有没有这个参数"的安全写入。
    ///
    /// 为什么值得单独开一个文件：这些名字是运行时脚本和编辑器里的 Animator Controller
    /// 之间唯一的约定，而两边写错任何一个字都**不会报错** —— 参数不存在时
    /// <c>Animator.SetFloat</c> 之类只在 Console 里刷一句警告，控制器那边则永远等不到
    /// 条件成立，表现是"某个动作死活不播"。
    ///
    /// 本项目已经踩过一次更隐蔽的变体：参数建好了、连线也画了，但**没有任何脚本写它**，
    /// 于是它永远停在 Unity 的默认初值上，用到它的转移条件恒真 —— 角色一进场景就
    /// 无限重播跳跃循环。所以这里的命名规则不只是风格问题（见 <see cref="Airborne"/>）。
    ///
    /// <see cref="Has"/> 的调用方必须把结果**缓存在字段里**，不要每帧问：
    /// <c>Animator.parameters</c> 每次访问都会拷一份数组出来。
    /// </summary>
    public static class AnimatorParameters
    {
        /// <summary>移动速度（米/秒），驱动站立 / 行走 / 奔跑之间的切换。</summary>
        public const string SpeedName = "Speed";

        /// <summary>
        /// 是否腾空。
        ///
        /// **这个参数刻意是肯定式（"在空中"而不是"在地上"）**，因为"没有任何脚本写它"
        /// 这件事必须退化成安全状态：
        ///
        ///   · <c>Airborne</c> 没人写 → Unity 的初值 false → "站在地上" → 不播跳跃。安全。
        ///   · <c>Grounded</c> 没人写 → 初值 false → "不在地上" → 场场都跳。这就是本 bug。
        ///
        /// 换句话说，用否定式的名字时，"忘了接线"的故障形态是**做错事**；用肯定式的名字时，
        /// 同样的疏忽只会**少做一件事**。而少做一件事看得见，做错事只在真机上暴露。
        /// </summary>
        public const string AirborneName = "Airborne";

        /// <summary>开火，一次性触发。</summary>
        public const string AttackName = "Attack";

        /// <summary>挥镐，一次性触发。</summary>
        public const string SpecialName = "Special";

        /// <summary>
        /// 手上是不是举着枪：0 = 镐子在手（手臂自然垂放），1 = 枪举着。
        ///
        /// **刻意是 Float 而不是 Bool**，因为消费方是控制器里三个地面状态的 1D
        /// 混合树，而混合树只接受 Float 参数。它只有 0 和 1 两个端点，中间值出现在
        /// 切枪那一瞬间的过渡里，不是可玩的档位。
        ///
        /// 命名规则同 <see cref="AirborneName"/>：**肯定式**。没人写它时取 0，
        /// 表现是"枪垂在身侧" —— 少做一件事，而不是"拿着镐子却摆出举枪姿势"。
        /// 这条规则在本项目已经救过一次场（见 <see cref="AirborneName"/> 的注释）。
        /// </summary>
        public const string HoldingGunName = "HoldingGun";

        public static readonly int Speed = Animator.StringToHash(SpeedName);
        public static readonly int Airborne = Animator.StringToHash(AirborneName);
        public static readonly int Attack = Animator.StringToHash(AttackName);
        public static readonly int Special = Animator.StringToHash(SpecialName);
        public static readonly int HoldingGun = Animator.StringToHash(HoldingGunName);

        /// <summary>
        /// 控制器里是否真的有这个参数（名字与类型都对得上）。
        ///
        /// 图元回退的玩家没有 Animator、老控制器里可能没有新参数，这两种情况下
        /// <c>SetXxx</c> 只会刷警告而不会做事，所以先问一句再写。
        /// </summary>
        public static bool Has(Animator animator, int nameHash, AnimatorControllerParameterType type)
        {
            if (animator == null || animator.runtimeAnimatorController == null)
                return false;

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].nameHash == nameHash && parameters[i].type == type)
                    return true;
            }

            return false;
        }
    }
}
