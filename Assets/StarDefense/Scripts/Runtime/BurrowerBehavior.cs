using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 挖掘虫：出场即潜入地下，无视沿途防线直奔核心，接近核心后钻出。
    /// 地下状态不可被选中也不可被击中，玩家必须给后方留防守，
    /// 或者靠雷达提前发现，否则核心会从内部被撕开。
    /// </summary>
    public class BurrowerBehavior : EnemyBehavior
    {
        private const float EmergeSeconds = 0.9f;
        private const float MinEmergeDistance = 4f;
        private const float DefaultBurrowSpeedScale = 1.7f;

        private enum Stage
        {
            Burrowed,
            Emerging,
            Surface
        }

        private Stage stage = Stage.Burrowed;
        private float emergeTimer;
        private Renderer[] cachedRenderers;
        private Collider[] cachedColliders;

        public override bool TakeOverMovement => stage != Stage.Surface;

        public override void Initialize(EnemyUnit unit)
        {
            base.Initialize(unit);
            cachedRenderers = unit.GetComponentsInChildren<Renderer>(true);
            cachedColliders = unit.GetComponentsInChildren<Collider>(true);
            SetUnderground(true);
        }

        public override void Tick(float deltaTime)
        {
            if (!Ready)
                return;

            switch (stage)
            {
                case Stage.Burrowed:
                    TickBurrowed(deltaTime);
                    break;

                case Stage.Emerging:
                    emergeTimer -= deltaTime;
                    if (emergeTimer <= 0f)
                    {
                        SetUnderground(false);
                        stage = Stage.Surface;
                    }
                    break;
            }
        }

        private void TickBurrowed(float deltaTime)
        {
            Transform core = Unit.CoreTransform;
            if (core == null)
            {
                SetUnderground(false);
                stage = Stage.Surface;
                return;
            }

            float distance = StarDefenseMath.HorizontalDistance(Unit.transform.position, core.position);
            float emergeAt = Mathf.Max(MinEmergeDistance, Config.behaviorRange);
            if (distance <= emergeAt)
            {
                stage = Stage.Emerging;
                emergeTimer = EmergeSeconds;
                Unit.game.ShowHint("挖掘虫从地下钻出！");
                return;
            }

            float speedScale = Config.behaviorValue > 0f ? Config.behaviorValue : DefaultBurrowSpeedScale;
            Unit.MoveToward(core.position, deltaTime, speedScale);
        }

        private void SetUnderground(bool underground)
        {
            if (cachedRenderers != null)
            {
                for (int i = 0; i < cachedRenderers.Length; i++)
                {
                    if (cachedRenderers[i] != null)
                        cachedRenderers[i].enabled = !underground;
                }
            }

            if (cachedColliders != null)
            {
                for (int i = 0; i < cachedColliders.Length; i++)
                {
                    if (cachedColliders[i] != null)
                        cachedColliders[i].enabled = !underground;
                }
            }
        }
    }
}
