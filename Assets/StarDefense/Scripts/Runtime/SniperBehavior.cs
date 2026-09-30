using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 狙击虫：维持在中远距离点名，玩家贴太近就后撤。
    /// 玩家没法靠"把炮塔堆在核心边上"解决它，必须主动出门清点。
    /// </summary>
    public class SniperBehavior : EnemyBehavior
    {
        private const float DefaultKeepDistance = 12f;
        private const float RetreatStep = 3f;
        private const float RetreatSpeedScale = 0.85f;

        private float retreatSign = 1f;

        public override bool TakeOverMovement => TooClose;

        public override bool TakeOverCombat => TooClose;

        private bool TooClose
        {
            get
            {
                if (!Ready || Unit.CurrentTarget == null)
                    return false;

                float keep = Config.behaviorRange > 0f ? Config.behaviorRange : DefaultKeepDistance;
                return StarDefenseMath.HorizontalDistance(Unit.transform.position, Unit.CurrentTarget.position) < keep;
            }
        }

        public override void Tick(float deltaTime)
        {
            if (!Ready || !TooClose)
                return;

            Transform target = Unit.CurrentTarget;
            Vector3 away = Unit.transform.position - target.position;
            away.y = 0f;

            // 和目标几乎重合时没有可用的背向，横向侧步脱开，避免原地卡死
            if (away.sqrMagnitude <= 0.01f)
            {
                away = Unit.transform.right * retreatSign;
                retreatSign = -retreatSign;
            }

            Vector3 destination = Unit.transform.position + away.normalized * RetreatStep;
            Unit.MoveToward(destination, deltaTime, RetreatSpeedScale);
        }
    }
}
