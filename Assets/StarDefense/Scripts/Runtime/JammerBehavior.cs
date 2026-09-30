using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 干扰虫：周期性瘫痪半径内的防御建筑，让防线反复出现窗口。
    /// 玩家必须优先点掉它，否则炮塔会在最需要的时候集体停机。
    /// </summary>
    public class JammerBehavior : EnemyBehavior
    {
        private const float DefaultRadius = 10f;
        private const float DefaultInterval = 4f;
        private const float DefaultDuration = 3f;
        private const float FirstJamDelay = 1.5f;

        private float jamTimer;

        public override void Initialize(EnemyUnit unit)
        {
            base.Initialize(unit);
            // 出场后很快给一次压力，不给玩家免费的开场窗口
            jamTimer = FirstJamDelay;
        }

        public override void Tick(float deltaTime)
        {
            if (!Ready)
                return;

            jamTimer -= deltaTime;
            if (jamTimer > 0f)
                return;

            jamTimer = Config.behaviorInterval > 0f ? Config.behaviorInterval : DefaultInterval;

            float radius = Config.behaviorRange > 0f ? Config.behaviorRange : DefaultRadius;
            float duration = Config.behaviorValue > 0f ? Config.behaviorValue : DefaultDuration;

            Unit.game.JamBuildings(Unit.transform.position, radius, duration, Config.color);
        }
    }
}
