using System;
using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 以资产形式存在的共享数值。系统之间只互持这个资产，不互持对方引用。
    /// 写入走 Value / SetValue / ApplyChange，读取用 Value，监听用 Changed。
    ///
    /// 典型用途：威胁值。StarDefenseGame 往里加、WaveDirector 读它判断开波、
    /// HUD 读它显示——三方都不需要认识彼此。
    /// </summary>
    [CreateAssetMenu(menuName = "Star Defense/Variables/Float", fileName = "Var_Float")]
    public class FloatVariable : ScriptableObject
    {
        [SerializeField] private float value;

        [Header("Reset")]
        [Tooltip("初始值。勾选 Reset On Enable 时，进入播放模式会回到这个值。")]
        [SerializeField] private float initialValue;

        [Tooltip("进入播放模式时重置为初始值并清空监听。仅调试残留数据时才关掉。")]
        [SerializeField] private bool resetOnEnable = true;

        /// <summary>值变化时触发。写入同一个值不会刷事件。</summary>
        public event Action<float> Changed;

        public float Value
        {
            get => value;
            set
            {
                if (Mathf.Approximately(this.value, value))
                    return;

                this.value = value;
                Changed?.Invoke(value);
            }
        }

        public void SetValue(float newValue) => Value = newValue;
        public void ApplyChange(float delta) => Value += delta;

        /// <summary>把另一个资产的当前值赋给自己。</summary>
        public void SetValue(FloatVariable source) => Value = source != null ? source.Value : 0f;

        /// <summary>把另一个资产的当前值加到自己身上。</summary>
        public void ApplyChange(FloatVariable source) => Value += source != null ? source.Value : 0f;

        public void ResetToInitial() => Value = initialValue;

        private void OnEnable()
        {
            if (!resetOnEnable)
                return;

            // 直接写字段而不是走属性：重置不应该触发 Changed。此刻场景对象通常还没
            // OnEnable 完，触发也没人接得住。
            value = initialValue;

            // 清掉上一轮域重载残留的失效委托，否则会累积到 MissingReferenceException。
            Changed = null;
        }
    }
}
