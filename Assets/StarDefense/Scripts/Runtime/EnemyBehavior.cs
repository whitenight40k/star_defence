using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 敌人行为的单一职责基类。
    /// 每个子类只回答一个问题："这个虫子和别的虫子不一样在哪"。
    /// 通用推进与近战仍由 EnemyUnit 负责，行为组件通过 TakeOver 开关局部接管。
    /// </summary>
    public abstract class EnemyBehavior : MonoBehaviour
    {
        /// <summary>宿主敌人，由 EnemyUnit 在挂载后注入。</summary>
        protected EnemyUnit Unit { get; private set; }

        /// <summary>配置数据，来自 ScriptableObject，行为不自己存数值。</summary>
        protected EnemyConfig Config => Unit != null ? Unit.config : null;

        /// <summary>宿主与配置都已就绪，可以安全读取数据。</summary>
        protected bool Ready => Unit != null && Unit.config != null && Unit.game != null;

        public virtual void Initialize(EnemyUnit unit)
        {
            Unit = unit;
        }

        /// <summary>返回 true 表示本帧由行为接管移动决策。</summary>
        public virtual bool TakeOverMovement => false;

        /// <summary>返回 true 表示本帧由行为接管攻击决策。</summary>
        public virtual bool TakeOverCombat => false;

        public virtual void Tick(float deltaTime)
        {
        }
    }
}
