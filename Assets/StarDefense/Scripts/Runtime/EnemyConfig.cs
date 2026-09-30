using UnityEngine;

namespace StarDefense
{
    [CreateAssetMenu(menuName = "Star Defense/Enemy Config", fileName = "EnemyConfig")]
    public class EnemyConfig : ScriptableObject
    {
        public string id = "crawler";
        public string displayName = "普通虫";
        public Color color = new Color(0.55f, 0.2f, 0.15f, 1f);
        public float maxHealth = 50f;
        public float moveSpeed = 3.5f;
        public float damage = 7f;
        public float attackInterval = 1.1f;
        public float attackRange = 1.8f;
        public float radius = 0.9f;
        public bool explodesOnContact;
        public float explosionRadius = 5f;
        public bool isBoss;

        [Header("Art")]
        [Tooltip("运行时刷怪用的 Prefab（Art/Prefabs/PFB_*.prefab）。\n" +
                 "留空则退回运行时图元，方便美术未就位时继续调试。\n" +
                 "填了 Prefab 就不再覆盖 transform.localScale —— 资产是按米 authored 的。")]
        public GameObject prefab;

        [Header("Behavior")]
        [Tooltip("决定出生时挂载哪个行为组件")]
        public EnemyBehaviorType behavior = EnemyBehaviorType.Charger;

        [Tooltip("语义随行为变化：挖掘虫=钻出距离，狙击虫=保持距离，干扰虫=干扰半径")]
        public float behaviorRange;

        [Tooltip("语义随行为变化：干扰虫=干扰间隔秒")]
        public float behaviorInterval;

        [Tooltip("语义随行为变化：挖掘虫=地下速度倍率，干扰虫=瘫痪持续秒")]
        public float behaviorValue;
    }
}
