using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 自爆虫：不做常规近战，贴到目标立刻引爆，对范围内的建筑、敌人与玩家同时造成伤害。
    /// 因为爆炸会波及同族，玩家可以用自爆虫清掉它自己的集群。
    /// </summary>
    public class BomberBehavior : EnemyBehavior
    {
        private const float DefaultRadius = 5f;
        private const float DamageMultiplier = 2.4f;
        private const float MinDamage = 20f;

        private bool detonated;

        /// <summary>自爆虫不挥爪，攻击时机完全由引爆条件决定。</summary>
        public override bool TakeOverCombat => true;

        public override void Tick(float deltaTime)
        {
            if (detonated || !Ready)
                return;

            Transform target = Unit.CurrentTarget;
            if (target == null)
                return;

            float distance = StarDefenseMath.HorizontalDistance(Unit.transform.position, target.position);
            if (distance > Config.attackRange)
                return;

            Detonate();
        }

        private void Detonate()
        {
            detonated = true;

            float radius = Config.explosionRadius > 0f ? Config.explosionRadius : DefaultRadius;
            float damage = Mathf.Max(MinDamage, Config.damage * DamageMultiplier);

            Unit.game.Explode(Unit.transform.position, radius, damage, Config.color);
            Unit.KillSelf();
        }
    }
}
