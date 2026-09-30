using UnityEngine;

namespace StarDefense
{
    [CreateAssetMenu(menuName = "Star Defense/Buildable Config", fileName = "BuildableConfig")]
    public class BuildableConfig : ScriptableObject
    {
        [Header("Identity")]
        public string id = "turret";
        public string displayName = "机枪塔";
        /// <summary>
        /// 数字键热键的**硬上限：9**。请注意这不是"目前只做到 9"，而是 Unity 侧的上限。
        ///
        /// 美术侧台账的 open item 3 写着「`[Range(1, 8)]` widened to 15」，**照做会静默失效**，
        /// 所以把原因钉在这里：<see cref="PlayerToolController"/> 是按
        /// `(KeyCode)((int)KeyCode.Alpha1 + i)` 逐个轮询数字键的，而 Unity 的 KeyCode 里
        /// Alpha1 = 49 … Alpha9 = 57，再往后 58 是**未定义值** —— 拿它去问
        /// <c>Input.GetKeyDown</c> 永远返回 false 且不报错。表现是改完编译通过、Inspector 里
        /// 也能填到 15，就是按不出来，而且查不出原因。
        ///
        /// 另一个容易踩的点：`Alpha0 = 48`，**排在 Alpha1 之前**，所以"第 10 个键用 0"
        /// 不能靠顺延得到，得单独映射一行。
        ///
        /// 若要超过 9 个建筑（美术已备好 15 张图标），唯一正路是把本字段换成
        /// <c>KeyCode</c> 类型、并把上面的轮询逻辑改成按 KeyCode 表驱动 —— 那是改造，
        /// 不是放松范围。在改造之前，这个数保持 9。
        /// </summary>
        public const int MaxHotkey = 9;

        [Tooltip("数字键热键，1–9。上限由 MaxHotkey 决定 —— 填 10 以上按键永远命不中，" +
                 "原因见该常量注释。")]
        [Range(1, MaxHotkey)] public int hotkey = 1;
        public Color color = Color.gray;

        [Header("Art")]
        [Tooltip("运行时建造用的 Prefab（Art/Prefabs/PFB_*.prefab）。\n" +
                 "留空则退回运行时图元，方便美术未就位时继续调试。\n" +
                 "填了 Prefab 就不再覆盖 transform.localScale —— 资产是按米 authored 的。")]
        public GameObject prefab;

        [Tooltip("建造栏图标（Art/Textures/UI/Buildings/UI_BLD_*.png，美术侧交付的单张插画）。\n" +
                 "由 BuildBarIconLibrary 按 id 分配，不再让 UI 代码自己按名字猜。\n" +
                 "**留空是允许的**：建造栏会退回一个纯色块，而不是画一个空槽 —— \n" +
                 "新建筑还没配图时不该连带把整条栏弄成一片黑洞。")]
        public Texture2D icon;

        [Header("Cost")]
        public ResourceCost cost = new ResourceCost(40, 0, 0);
        public int powerCost = 8;
        public int powerSupply;
        public float buildSeconds = 2.5f;

        [Header("Capacity")]
        [Tooltip("占用的基地建筑容量点数。策划案 13 章：基地共 20 点，机枪塔 2 / 炮塔 4 / 电磁塔 3 / 能量墙 1 / 雷达 3 / 维修站 4 / 护盾发生器 5。")]
        [Range(0, 20)] public int capacityCost = 3;

        [Header("Role")]
        [Tooltip("非火力建筑的附加职能。火力建筑请改用 isWeapon，本字段对武器无效。")]
        public BuildableRole role = BuildableRole.None;

        [Header("Durability")]
        public float maxHealth = 220f;
        public Vector3 size = new Vector3(2f, 2f, 2f);

        [Header("Support (按 role 生效)")]
        [Tooltip("支援半径（米）。护盾发生器=护盾覆盖范围；维修站=维修覆盖范围。")]
        public float supportRadius = 12f;
        [Tooltip("护盾发生器：每座目标建筑获得的护盾值。维修站不使用此字段。")]
        public float shieldAmount = 150f;
        [Tooltip("护盾发生器：护盾被击破后进入冷却的秒数，冷却结束整体回满。维修站不使用此字段。")]
        public float shieldRechargeDelay = 5f;
        [Tooltip("维修站：每秒修复量。护盾发生器不使用此字段。")]
        public float repairPerSecond = 14f;
        [Tooltip("维修站：每个维修节拍消耗的能源。能源不足时暂停维修。")]
        public int repairEnergyPerTick = 1;

        [Header("Combat")]
        public bool isWeapon;
        public float range = 20f;
        public float damagePerShot = 10f;
        public float fireInterval = 0.4f;
        public float splashRadius;
        [Range(0.1f, 1f)] public float slowMultiplier = 1f;
    }
}
