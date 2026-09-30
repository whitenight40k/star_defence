using System.Collections.Generic;
using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 护盾发生器。按策划案 11.2 实现："为一定范围内建筑提供护盾，护盾受到攻击后消耗，护盾耗尽后进入冷却"。
    /// 因此护盾不做"边挨打边小额回充"，否则持续的小口径火力永远打不穿护盾，
    /// 护盾会变成无敌效果而不是一层可被磨掉的缓冲。
    /// 状态机：无护盾 → 建立护盾（满）→ 被消耗 → 归零 → 冷却计时 → 整体回满。
    /// </summary>
    [RequireComponent(typeof(Buildable))]
    public class ShieldEmitter : MonoBehaviour
    {
        public StarDefenseGame game;

        private Buildable self;
        private float tickTimer;

        /// <summary>本发生器当前守护的目标，用于发生器被摧毁时收回护盾。</summary>
        private readonly List<Health> protectedTargets = new List<Health>();

        /// <summary>目标护盾被击破的时刻；-1 表示护盾尚在，不需要冷却计时。</summary>
        private readonly Dictionary<Health, float> brokenSince = new Dictionary<Health, float>();

        private void Awake()
        {
            self = GetComponent<Buildable>();
        }

        private void OnDestroy()
        {
            // 发生器没了，它给的护盾也必须收回，否则建筑会永久挂着一层无主护盾
            for (int i = 0; i < protectedTargets.Count; i++)
            {
                Health target = protectedTargets[i];
                if (target != null)
                    target.ConfigureShield(0f);
            }
            protectedTargets.Clear();
            brokenSince.Clear();
        }

        private void Update()
        {
            if (game == null || self == null || self.config == null)
                return;

            tickTimer -= Time.deltaTime;
            if (tickTimer > 0f)
                return;
            tickTimer = 0.2f;

            // 建造中/停机/被干扰时不继续撑盾，但已建立的护盾保留原状，等恢复运转再继续维护
            if (!self.IsOperational)
                return;

            ShieldNearby();
        }

        private void ShieldNearby()
        {
            float radius = Mathf.Max(1f, self.config.supportRadius);

            ApplyTo(game.CoreHealth, radius);

            IReadOnlyList<Buildable> buildings = game.ActiveBuildings;
            for (int i = 0; i < buildings.Count; i++)
            {
                Buildable building = buildings[i];
                if (building == null || building == self || building.Health == null || building.Health.IsDead)
                    continue;

                ApplyTo(building.Health, radius);
            }
        }

        private void ApplyTo(Health target, float radius)
        {
            if (target == null || target.IsDead)
                return;

            if (StarDefenseMath.HorizontalDistance(target.transform.position, transform.position) > radius)
                return;

            // 首次接管：建立满护盾
            if (target.MaxShield <= 0f)
            {
                target.ConfigureShield(self.config.shieldAmount);
                protectedTargets.Add(target);
                brokenSince[target] = -1f;
                return;
            }

            if (target.HasShield)
            {
                brokenSince[target] = -1f;
                return;
            }

            // 护盾已被打空：记录击破时刻并开始冷却，冷却结束后整体回满
            if (!brokenSince.TryGetValue(target, out float since) || since <= 0f)
            {
                brokenSince[target] = Time.time;
                return;
            }

            if (Time.time - since >= self.config.shieldRechargeDelay)
                target.RestoreShield();
        }
    }
}
