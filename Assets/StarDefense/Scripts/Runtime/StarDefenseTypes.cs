using System;
using UnityEngine;

namespace StarDefense
{
    public enum ResourceType
    {
        Metal,
        Energy,
        Crystal
    }

    public enum PlayerTool
    {
        Gun,
        Pickaxe
    }

    /// <summary>
    /// 敌人行为类型。决定 EnemyUnit 出生时挂载哪个行为组件。
    /// Charger 是默认的直线推进，其余每种都对应策划案里一条独立的威胁逻辑。
    /// </summary>
    public enum EnemyBehaviorType
    {
        /// <summary>普通虫：直奔最近目标，无特殊行为。</summary>
        Charger,

        /// <summary>自爆虫：接触目标即刻引爆，造成范围伤害。</summary>
        Bomber,

        /// <summary>挖掘虫：潜入地下无视防线，接近核心后钻出。</summary>
        Burrower,

        /// <summary>狙击虫：保持中远距离点名，不贴脸。</summary>
        Sniper,

        /// <summary>干扰虫：周期性瘫痪附近的防御建筑。</summary>
        Jammer,

        /// <summary>搬运虫：预留，需要先有"地面掉落资源"概念。</summary>
        Thief
    }

    /// <summary>
    /// 非火力建筑的附加职能。火力建筑由 BuildableConfig.isWeapon 直接决定挂 TurretDefense，不走这里。
    /// None 必须保持为 0：已存在的资产不会带 role 字段，反序列化后落到 0，
    /// 这样旧资产的行为与引入本枚举之前完全一致。
    /// </summary>
    public enum BuildableRole
    {
        None = 0,

        /// <summary>护盾发生器：为半径内建筑撑起可消耗的护盾。</summary>
        ShieldEmitter = 1,

        /// <summary>维修站：持续消耗能源，自动修复半径内受损建筑。</summary>
        RepairAura = 2
    }

    public enum GamePhase
    {
        Landing,
        Expansion,
        Defense,
        BossWarning,
        BossFight,
        Evacuation,
        Victory,
        Defeat
    }

    [Serializable]
    public struct ResourceCost
    {
        public int metal;
        public int energy;
        public int crystal;

        public ResourceCost(int metal, int energy, int crystal)
        {
            this.metal = metal;
            this.energy = energy;
            this.crystal = crystal;
        }
    }

    [Serializable]
    public struct ResourceWallet
    {
        public int metal;
        public int energy;
        public int crystal;

        public bool CanAfford(ResourceCost cost)
        {
            return metal >= cost.metal && energy >= cost.energy && crystal >= cost.crystal;
        }

        public void Spend(ResourceCost cost)
        {
            metal -= cost.metal;
            energy -= cost.energy;
            crystal -= cost.crystal;
        }

        public void Add(ResourceType type, int amount)
        {
            switch (type)
            {
                case ResourceType.Metal:
                    metal += amount;
                    break;
                case ResourceType.Energy:
                    energy += amount;
                    break;
                case ResourceType.Crystal:
                    crystal += amount;
                    break;
            }
        }
    }

    public static class StarDefenseMath
    {
        public static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        public static Color FromHex(float r, float g, float b)
        {
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        }
    }
}
