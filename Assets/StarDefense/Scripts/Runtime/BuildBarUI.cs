using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 建造栏 UI 的规范常量。
    ///
    /// 数值出处：《异星物业_UI切图规范_建造栏.md》与设计稿
    /// 《异星物业 · 建造栏 UI 设计稿》(Ardot fileId 731326624174755)。
    ///
    /// 本类只承载"取自设计稿的硬数值"，不含行为 —— IMGUI 与 uGUI 两条实现都从这里取，
    /// 免得程序凭印象填 border / 圆角 / 间距，把稿子做成另一版。
    ///
    /// 切图已导入 Assets/StarDefense/Art/Textures/UI/BuildBar/（10 张）
    /// 图标已导入 Assets/StarDefense/Art/Textures/UI/Buildings/（15 张，958 母版，导入限 256）
    /// </summary>
    public static class BuildBarUI
    {
        // ── 坐标体系 ────────────────────────────────────────────────────
        /// <summary>设计稿参考分辨率。CanvasScaler 用 ScaleWithScreenSize，Match 0.5。</summary>
        public const float ReferenceWidth = 1920f;
        public const float ReferenceHeight = 1080f;

        // ── 九宫格边框 ──────────────────────────────────────────────────
        /// <summary>容器类切图（64×64）：Panel / Panel_Accent / Slot 四态 / Mask。</summary>
        /// <remarks>
        /// 16 这个值比贴图里真实的圆角大得多，但**偏大是安全的一侧**：九宫格的四角区域
        /// 是 1:1 绘制、只有中间被拉伸，所以只要 border ≥ 圆角，边角轮廓就与原图完全一致，
        /// 多出来的部分只是被拉伸的直边（直线沿自身拉伸看不出来）。偏小才会把圆弧切掉，
        /// 表现出来是"四角被削平"。
        ///
        /// 实测（scripts/ui_slice_probe.py 解 PNG 像素）：64×64 这套的圆角半径
        /// Panel/Panel_Accent = 7 px、Slot 四态 = 6 px、Mask = 4 px，描边 1~2 px。
        /// 所以 border 的下界是 8；取 16 留了一倍余量，代价只是槽位不能小于 32 px。
        /// **不要把这里的 16 和下方 RadiusContainer/RadiusControl 的"设计稿圆角"混为一谈**：
        /// 前者是贴图的裁切框，后者是稿子上的视觉圆角，两者并不需要相等。
        /// </remarks>
        public static readonly RectOffset BorderBox = new RectOffset(16, 16, 16, 16);

        /// <summary>胶囊类切图（24×16）：Tag / Bar。</summary>
        public static readonly RectOffset BorderPill = new RectOffset(6, 6, 6, 6);

        // ── 圆角尺度 ────────────────────────────────────────────────────
        // 一套尺度，别再各处随手填。嵌套时内层 = 外层 − 内缩。
        public const int RadiusContainer = 12;  // 建造栏底 / HUD 面板 / 详情卡
        public const int RadiusControl = 10;    // 槽位
        public const int RadiusIcon = 8;        // 图标遮罩（槽位 10 − 内缩 3 ≈ 8）
        public const int RadiusTag = 4;         // 分组标签
        public const int RadiusBar = 6;         // 进度条（胶囊）

        // ── 建造栏几何 ──────────────────────────────────────────────────
        public const float BarWidth = 1576f;
        public const float BarHeight = 168f;
        public const float BarPadding = 12f;
        public const float SlotGap = 8f;
        public const float SlotHeight = 144f;
        public const float DividerWidth = 2f;

        public const float SlotPaddingHorizontal = 3f;
        public const float SlotPaddingVertical = 6f;
        public const float SlotContentGap = 6f;
        public const float IconSize = 72f;
        public const float HotkeyInsetX = 5f;
        public const float HotkeyInsetY = 4f;
        public const float IndicatorHeight = 3f;

        public const float DetailCardWidth = 620f;
        public const float DetailCardHeight = 156f;
        public const float DetailCardPadding = 14f;
        public const float DetailCardGap = 14f;
        public const float DetailIconSize = 128f;

        public const float PanelWidth = 404f;
        public const float PanelHeight = 132f;
        public const float PanelPadding = 16f;
        public const float PanelGap = 10f;

        public const float BarThickness = 12f;

        /// <summary>
        /// 单个槽位宽度。15 格等分后是 94.6667 —— <b>不要取整再补余量</b>，
        /// 直接按 float 布点；取整会让每格差 1px，凑近 4K 就看得出来。
        /// 分组标签另计，不参与等分。
        /// </summary>
        public static float SlotWidth(int slotCount, int dividerCount)
        {
            float inner = BarWidth - BarPadding * 2f;
            float gaps = SlotGap * Mathf.Max(0, slotCount + dividerCount - 1);
            float dividers = DividerWidth * dividerCount;
            return (inner - gaps - dividers) / Mathf.Max(1, slotCount);
        }

        /// <summary>
        /// 设计稿里一个槽位应有的宽度 —— 即"15 格 + 2 分组条刚好铺满 BarWidth"时的格子宽。
        /// 这是个**槽位尺寸**，不是栏宽：栏宽应当跟着槽位走（见 <see cref="BarWidthFor"/>）。
        /// </summary>
        public static float DesignSlotWidth
        {
            get { return SlotWidth(DesignSlotCount, DesignDividerCount); }
        }

        /// <summary>设计稿槽位数。美术按 15 种建筑交付了图标，玩法侧目前实现了 8 种。</summary>
        public const int DesignSlotCount = 15;

        /// <summary>设计稿的分组分隔条数量。</summary>
        public const int DesignDividerCount = 2;

        /// <summary>
        /// 按**实际**槽位数算栏宽：槽位保持设计尺寸，栏宽缩短。
        ///
        /// 为什么不直接恒用 <see cref="BarWidth"/>：1576 是按 15 格定的。只做 8 种建筑时，
        /// 若栏宽不变、格子改由 8 等分，每格会变成 184 px —— 图标仍是 72 px，
        /// 于是槽内空出一大半，整条栏像没画完。
        /// 反过来（栏宽随槽位收缩、格子尺寸不变）得到的是"一格就是一格"，
        /// 以后建筑加到 15 种，宽度会自己长回 BarWidth，不需要再改这里。
        /// </summary>
        public static float BarWidthFor(int slotCount, int dividerCount)
        {
            if (slotCount <= 0)
                return BarWidth;

            return BarPadding * 2f
                   + DesignSlotWidth * slotCount
                   + SlotGap * Mathf.Max(0, slotCount + dividerCount - 1)
                   + DividerWidth * dividerCount;
        }

        // ── 配色 ────────────────────────────────────────────────────────
        public static readonly Color Panel = Hex("#12161B");
        public static readonly Color PanelLine = Hex("#293036");
        public static readonly Color Accent = Hex("#E8A33D");

        public static readonly Color Slot = Hex("#141A22");
        public static readonly Color SlotLine = Hex("#252B34");
        public static readonly Color SlotHover = Hex("#1B222C");
        public static readonly Color SlotHoverLine = Hex("#8A6E35");
        public static readonly Color SlotSelected = Hex("#2A2113");
        public static readonly Color SlotLocked = Hex("#10151B");
        public static readonly Color SlotLockedLine = Hex("#1B2129");

        public static readonly Color BarTrack = Hex("#1B222B");
        public static readonly Color TagBackground = Hex("#3A2E17");

        public static readonly Color Gain = Hex("#4FBF8B");
        public static readonly Color Danger = Hex("#E5484D");
        public static readonly Color Capacity = Hex("#5B9BD5");

        public static readonly Color TextPrimary = Hex("#DCE3EC");
        public static readonly Color TextDim = Hex("#9AA6B4");
        public static readonly Color TextFaint = Hex("#6E7885");
        public static readonly Color TextNameSelected = Hex("#F0D9A8");
        public static readonly Color TextLocked = Hex("#6B7480");
        public static readonly Color Hotkey = Hex("#F2C14E");
        public static readonly Color HotkeyLocked = Hex("#6B7280");

        // ── 槽位状态 ────────────────────────────────────────────────────
        public enum SlotState
        {
            Normal,
            Hover,
            Selected,
            Locked,
        }

        /// <summary>锁定态下图标的不透明度。锁定不换底图叠加，只压暗图标 + 数字标红。</summary>
        public const float LockedIconAlpha = 0.35f;

        /// <summary>槽位底图文件名（不含扩展名），放 Resources 或序列化引用时按这个取。</summary>
        public static string SliceName(SlotState state)
        {
            switch (state)
            {
                case SlotState.Hover: return SlotHoverName;
                case SlotState.Selected: return SlotSelectedName;
                case SlotState.Locked: return SlotLockedName;
                default: return SlotName;
            }
        }

        // ── 切图文件名 ──────────────────────────────────────────────────
        // 与 Art/Textures/UI/BuildBar/ 下的资产一一对应。集中放这里，是为了让"场景构建器要加载哪几张"
        // 和"运行时要哪几张"读的是同一份名字 —— 名字散在两处，漏掉一张的失败形态是
        // 那一块安静地不画（GUIStyle 背景为 null 就什么都不画），不报错。
        public const string PanelName = "UI_BLD_Slice_Panel";
        public const string PanelAccentName = "UI_BLD_Slice_Panel_Accent";
        public const string SlotName = "UI_BLD_Slice_Slot";
        public const string SlotHoverName = "UI_BLD_Slice_Slot_Hover";
        public const string SlotSelectedName = "UI_BLD_Slice_Slot_Selected";
        public const string SlotLockedName = "UI_BLD_Slice_Slot_Locked";

        /// <summary>
        /// 资产目录里有、但当前建造栏**没有引用**的切图。**不是错误，也不是漏导入** ——
        /// 每一张都有具体原因，逐条写在下面，免得下次有人当成"差四张图"去补齐。
        /// </summary>
        // 分组标签的底图。规范里它配的是 TagBackground + RadiusTag = 4，用途是给槽位**分组**；
        // 当前 8 种建筑不分组的，所以用不上。
        // 特别注意：热键数字**不吃这张图**。热键在规范里另有一组常量
        // （HotkeyInsetX/Y + Hotkey / HotkeyLocked 两个颜色），走的是裸文字、没有胶囊底 ——
        // 有底的话规范里会多出一张 HotkeyTag 之类的切图，没有。
        public const string TagName = "UI_BLD_Slice_Tag";

        // 纯白可染色轨道（像素实测 255,255,255），规范里是给进度条用的。
        // 曾经被当成深色底板直铺过一次 —— 结果是整条栏变成白条。底板改用 Panel 后它就闲置了。
        // 留着是因为将来做"建造进度条"时正需要它。
        public const string BarName = "UI_BLD_Slice_Bar";

        // 圆形遮罩。规范要求图标的圆角由 UI 层来裁（`Mask` Graphic + showMaskGraphic = false），
        // 因为图标母版是**方角**的，而方角会在深色槽里显出一道缝 —— 实测图标四角 #282D31，
        // 槽底 #141A22，约 20 级亮度差，是看得见的。
        // 但**IMGUI 没有圆角裁剪能力**，而建造栏正是 IMGUI 的（OnGUI）—— 所以这张图在这里
        // 是死资产，当前画出来的图标必然带方角。要让图标真圆角，唯一解是把建造栏整体换成
        // uGUI（重写，不是调参）；在那之前它一直会用不上，这不是遗漏。
        public const string MaskName = "UI_BLD_Slice_Mask";

        // 4×4 纯白块，用来画纯色矩形（进度条填充、分割线之类）。当前没有这种需求 ——
        // 栏内的分隔线是 1px 的 GUI.DrawTexture，不需要九宫格。
        public const string WhiteName = "UI_BLD_White_4";

        /// <summary>
        /// 建造栏真正用到的切图清单。构建器按这份清单加载并逐张判存在 ——
        /// 让"资产少了一张"变成一条可以被报出来的事实，而不是等谁看出来某块没画东西。
        /// </summary>
        public static readonly string[] RequiredSlices =
        {
            PanelName,
            PanelAccentName,
            SlotName,
            SlotHoverName,
            SlotSelectedName,
            SlotLockedName,
        };

        /// <summary>
        /// 底图取哪一态。<b>选中优先于锁定</b>：选中同时资源不足时底图仍取 Selected，
        /// 锁定改由"图标压暗 + 不足项标红"表达（这两个是可叠加的前景层）。
        /// 一底图 + 可叠加前景，省掉"选中且锁定"的第四张切图。
        /// </summary>
        public static SlotState BackgroundState(bool selected, bool hovered, bool locked)
        {
            if (selected) return SlotState.Selected;
            if (locked) return SlotState.Locked;
            if (hovered) return SlotState.Hover;
            return SlotState.Normal;
        }

        // ── 槽位文案 ────────────────────────────────────────────────────
        // 槽位宽度只放得下 3 枚 chip（88.67px / 11px 字号），所以能源与晶体不进槽位，
        // 收进详情卡；另含消耗时缀一个「·」告诉玩家"这还不是全部"。
        public static string CostChip(ResourceCost cost)
        {
            return cost.energy + cost.crystal > 0 ? "矿" + cost.metal + "·" : "矿" + cost.metal;
        }

        public static string CostChipFull(ResourceCost cost)
        {
            string s = "金属 " + cost.metal;
            if (cost.energy > 0) s += "   ·   能源 " + cost.energy;
            if (cost.crystal > 0) s += "   ·   晶体 " + cost.crystal;
            return s;
        }

        public static string PowerChip(int powerCost, int powerSupply)
        {
            if (powerSupply > 0) return "电+" + powerSupply;
            if (powerCost > 0) return "电" + powerCost;
            return "电0";
        }

        public static Color PowerChipColor(int powerCost, int powerSupply)
        {
            if (powerSupply > 0) return Gain;
            if (powerCost > 0) return Accent;
            return Hex("#4E5866");
        }

        public static string CapacityChip(int capacityCost)
        {
            return "容" + capacityCost;
        }

        // ── 工具 ────────────────────────────────────────────────────────
        public static Color Hex(string hex)
        {
            Color c;
            if (ColorUtility.TryParseHtmlString(hex[0] == '#' ? hex : "#" + hex, out c))
                return c;
            return Color.magenta;
        }

        /// <summary>
        /// IMGUI 用的九宫格 style。<b>九宫格在 IMGUI 里靠 GUIStyle.border 生效，
        /// 不是 DrawTextureWithTexCoords</b> —— 后者只会把整张图拉扁，四角跟着变形。
        /// </summary>
        public static GUIStyle SlicedBox(Texture2D texture, RectOffset border)
        {
            var style = new GUIStyle
            {
                border = border,
                stretchWidth = true,
                stretchHeight = true,
            };
            style.normal.background = texture;
            return style;
        }
    }
}
