using System;
using UnityEngine;

namespace StarDefense
{
    public class Health : MonoBehaviour
    {
        [SerializeField] private float maxHealth = 100f;
        [SerializeField] private float currentHealth = 100f;
        [SerializeField] private float maxShield;
        [SerializeField] private float currentShield;

        public event Action<Health> Died;
        public event Action<Health, float, float> Changed;

        public float MaxHealth => maxHealth;
        public float CurrentHealth => currentHealth;
        public float Normalized => maxHealth <= 0f ? 0f : currentHealth / maxHealth;
        public bool IsDead => currentHealth <= 0f;

        /// <summary>护盾上限。为 0 表示这个单位不受护盾发生器保护。</summary>
        public float MaxShield => maxShield;
        public float CurrentShield => currentShield;
        public bool HasShield => currentShield > 0f;

        public void Configure(float newMaxHealth)
        {
            maxHealth = Mathf.Max(1f, newMaxHealth);
            currentHealth = maxHealth;
            Changed?.Invoke(this, currentHealth, maxHealth);
        }

        /// <summary>
        /// 设置护盾池并立即充满。护盾发生器接管目标时调用；
        /// 传 0 表示撤销护盾，用于发生器被摧毁时把保护一并收回。
        /// </summary>
        public void ConfigureShield(float amount)
        {
            maxShield = Mathf.Max(0f, amount);
            currentShield = maxShield;
            Changed?.Invoke(this, currentHealth, maxHealth);
        }

        /// <summary>护盾冷却结束后整体回满。按策划案 11.2，护盾不做持续小额回充。</summary>
        public void RestoreShield()
        {
            currentShield = maxShield;
            Changed?.Invoke(this, currentHealth, maxHealth);
        }

        public void TakeDamage(float amount)
        {
            if (IsDead || amount <= 0f)
                return;

            // 护盾先吸收，吸满后剩下的才扣血。
            // 这一步是"护盾耗尽后进入冷却"能成立的前提：小口径敌人打不穿时不会蹭到血。
            if (currentShield > 0f)
            {
                float absorbed = Mathf.Min(currentShield, amount);
                currentShield -= absorbed;
                amount -= absorbed;
            }

            if (amount <= 0f)
                return;

            currentHealth = Mathf.Max(0f, currentHealth - amount);
            Changed?.Invoke(this, currentHealth, maxHealth);

            if (currentHealth <= 0f)
                Died?.Invoke(this);
        }

        public void Heal(float amount)
        {
            if (IsDead || amount <= 0f)
                return;

            currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
            Changed?.Invoke(this, currentHealth, maxHealth);
        }
    }
}
