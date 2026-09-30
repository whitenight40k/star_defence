using System.Collections.Generic;
using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 维修站。按策划案 11.3 实现："自动维修附近建筑，需要持续消耗能源"。
    /// 能源是持续开销而不是一次性建造成本，所以维修站是唯一会让能源账户持续流出的建筑——
    /// 玩家必须在"多建一座维修站"和"把能源留给其他建筑"之间做选择。
    /// 能源不足时悬挂维修但不停机，避免能源一断整条防线连锁失效。
    /// </summary>
    [RequireComponent(typeof(Buildable))]
    public class RepairAura : MonoBehaviour
    {
        /// <summary>维修节拍。0.5 秒一次，既够快能看出效果，也不会每帧结算。</summary>
        private const float TickInterval = 0.5f;

        public StarDefenseGame game;

        private Buildable self;
        private float tickTimer;
        private bool reportedNoEnergy;

        /// <summary>复用同一份缓冲，避免每 0.5 秒产生一次列表垃圾。</summary>
        private readonly List<Buildable> damagedBuffer = new List<Buildable>();

        private void Awake()
        {
            self = GetComponent<Buildable>();
        }

        private void Update()
        {
            if (game == null || self == null || self.config == null)
                return;

            tickTimer -= Time.deltaTime;
            if (tickTimer > 0f)
                return;
            tickTimer = TickInterval;

            if (!self.IsOperational)
                return;

            RepairNearby();
        }

        private void RepairNearby()
        {
            damagedBuffer.Clear();

            float radius = Mathf.Max(1f, self.config.supportRadius);
            IReadOnlyList<Buildable> buildings = game.ActiveBuildings;
            for (int i = 0; i < buildings.Count; i++)
            {
                Buildable building = buildings[i];
                if (building == null || building.Health == null || building.Health.IsDead)
                    continue;
                if (building.Health.CurrentHealth >= building.Health.MaxHealth)
                    continue;
                if (StarDefenseMath.HorizontalDistance(building.transform.position, transform.position) > radius)
                    continue;

                damagedBuffer.Add(building);
            }

            if (damagedBuffer.Count == 0)
                return;

            int cost = Mathf.Max(1, self.config.repairEnergyPerTick);
            if (game.resources.energy < cost)
            {
                // 只在刚断能源时提示一次，否则 0.5 秒一次的提示会把 HUD 刷屏
                if (!reportedNoEnergy)
                {
                    game.ShowHint("维修站能源不足，暂停维修");
                    reportedNoEnergy = true;
                }
                return;
            }

            reportedNoEnergy = false;
            game.AddResource(ResourceType.Energy, -cost);

            float heal = self.config.repairPerSecond * TickInterval;
            for (int i = 0; i < damagedBuffer.Count; i++)
                damagedBuffer[i].Health.Heal(heal);
        }
    }
}
