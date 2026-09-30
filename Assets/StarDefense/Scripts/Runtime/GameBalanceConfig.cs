using UnityEngine;

namespace StarDefense
{
    [CreateAssetMenu(menuName = "Star Defense/Game Balance", fileName = "GameBalance")]
    public class GameBalanceConfig : ScriptableObject
    {
        [Header("Match Timeline (seconds)")]
        public float expansionStart = 60f;
        public float defenseStart = 240f;
        public float bossWarningStart = 480f;
        public float bossStart = 600f;
        public float evacuationStart = 900f;
        public float evacuationDuration = 90f;

        [Header("Starting Resources")]
        public ResourceWallet startingResources = new ResourceWallet { metal = 80, energy = 30, crystal = 0 };

        [Header("Buildings")]
        [Tooltip("基地建筑容量点数上限。来自策划案 6.1 / 13 章，固定 20 点；每座建筑按 BuildableConfig.capacityCost 占点。")]
        public int buildingCapacity = 20;
        [Tooltip("拆解建筑时返还的建造成本比例。给玩家一个主动腾出容量的出口，否则容量占满后局面会彻底锁死。")]
        [Range(0f, 1f)] public float demolishRefundRatio = 0.5f;

        [Header("Threat")]
        public float passiveThreatPerSecond = 0.035f;
        public float miningThreat = 0.75f;
        public float buildThreat = 2.5f;
        public float waveThreshold = 100f;

        [Header("Player")]
        public float playerMaxHealth = 120f;
        public float gunDamage = 18f;
        public float gunRange = 45f;
        public float gunFireInterval = 0.16f;
        public float pickaxeDamage = 22f;
        public float pickaxeRange = 3.5f;
        public float interactRange = 4f;

        [Header("AI Allies")]
        public int allyCount = 3;
        public float allyResourceInterval = 3.5f;
        public int allyResourceAmount = 6;
    }
}
