using System.Text;
using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 创造模式的建造栏：一排槽位显示所有可建建筑，当前选中的那个高亮，
    /// 上方一张详情卡说明它的成本、耗电、容量与为什么现在建不了。
    ///
    /// <b>为什么是 IMGUI 而不是 uGUI</b>
    /// 本工程的界面一直走 OnGUI（见 <see cref="StarDefenseHUD"/>），本栏沿用同一条路：
    ///   · 界面是**纯代码产物**，不往场景里塞 Canvas / Image / Text / EventSystem 那一串对象，
    ///     于是"场景重建后 UI 消失""精灵引用在序列化里断掉"这类故障形态根本不存在；
    ///   · 九宫格在 IMGUI 里靠 <c>GUIStyle.border</c> 生效，与 uGUI 的
    ///     <c>Image.type = Sliced</c> 是同一套切图，边角轮廓完全一致；
    ///   · 中文字形由内置 GUI 字体回退到系统字体解决，不需要额外引入字体资产。
    ///
    /// <b>为什么不做鼠标悬停 / 点击</b>
    /// 第一人称游玩时鼠标是锁定状态（<c>Cursor.lockState = Locked</c>），
    /// <c>Event.current.mousePosition</c> 停在屏幕中心不动 —— 悬停与点击这两态在正常游玩中
    /// **根本到达不了**。而真去做的话会引入一个实打实的 bug：左键点槽位的同时，
    /// <see cref="PlayerToolController.UsePrimary"/> 也在跑，于是"选了一座塔"会连带"在地上放一座塔"。
    /// 要正确实现鼠标交互，需要先有一个"UI 是否接管了本次输入"的仲裁（两个组件共享的输入所有权标记），
    /// 那不是本栏该顺手加的东西。所以本栏是**纯键盘**的：数字键切换，这是设计稿与需求指定的方式。
    /// 代价是 <c>UI_BLD_Slice_Slot_Hover</c> 这张切图暂时用不上（见 <see cref="HoverUnusedNote"/>）。
    ///
    /// 切图与图标都来自美术交付的 <c>Art/Textures/UI/</c>；
    /// 全部几何数值取自 <see cref="BuildBarUI"/>，本类不自己填 border / 间距 / 圆角。
    /// </summary>
    public class BuildBarHud : MonoBehaviour
    {
        [Header("Wiring")]
        public StarDefenseGame game;
        public PlayerToolController tools;

        [Header("九宫格切图（Art/Textures/UI/BuildBar）")]
        [Tooltip("容器底板。**建造栏的底板与详情卡共用这一张** —— 名字里的 Panel 是\"容器\"的意思。\n" +
                 "不要改用 UI_BLD_Slice_Bar：那张是**纯白**的可染色条（像素实测 255,255,255），\n" +
                 "是给进度条/电量条用的 24×16 胶囊轨道（配 BuildBarUI.BarTrack 染色），\n" +
                 "24×16 的宽高比与 3px 圆角在 168px 高的板子上会铺出一条白边。")]
        public Texture2D panel;
        [Tooltip("橙色描边的面板。选中项当前建不了时用它，把\"看中了但放不下\"单独标出来。")]
        public Texture2D panelAccent;
        public Texture2D slotNormal;
        public Texture2D slotHover;
        public Texture2D slotSelected;
        public Texture2D slotLocked;

        /// <summary>
        /// 当前用不上的切图及其原因。列在这里而不是散在注释里，因为它会出现在构建日志中。
        ///
        /// 「用不上」含两种情况，**别当成同一种错**：
        ///   · <b>没被引用</b>（Tag / Bar / Mask / White_4）—— 资产在，代码里却没有它的位置；
        ///   · <b>被引用了但触发不到</b>（Slot_Hover）—— 字段与 GUIStyle 都建好了，
        ///     只是本栏是纯键盘交互、游玩时鼠标锁定，那个状态永远不成立。
        /// 前者是"暂时没用"，后者是"结构上留着但不生效"。两种都不该在下次被当漏导入去补。
        /// </summary>
        public static readonly string[] UnusedSlices =
        {
            "UI_BLD_Slice_Tag：规范里它是**分组标签**（RadiusTag=4、TagBackground=#3A2E17），"
                + "给槽位分组用的。当前 8 种建筑不分组的，所以没用上；"
                + "热键数字走的是裸文字（HotkeyInset + Hotkey 颜色），不吃这张图",
            "UI_BLD_Slice_Slot_Hover：本栏为纯键盘交互，游玩时鼠标是锁定的，悬停态到不了",
            "UI_BLD_Slice_Bar：进度/电量条轨道（纯白，配 BarTrack 染色），本栏没有进度条，底板用的是 Panel",
            "UI_BLD_Slice_Mask：给 uGUI 的 Mask 组件裁**图标自己的四角**用。图标母版是方角的，"
                + "而方角在深色槽里会显出约 20 级亮度差的缝（实测图标四角 #282D31 / 槽底 #141A22），"
                + "所以规范要求圆角由 UI 层负责。但 **IMGUI 没有圆角裁剪能力**，"
                + "建造栏是 OnGUI 的 —— 做不到，于是当前画出来的图标必然是方角。"
                + "要圆角只能把整条栏换成 uGUI",
            "UI_BLD_White_4：纯白填充块；IMGUI 里用内置 Texture2D.whiteTexture 代替即可",
        };

        /// <summary>栏底距屏幕底边的留白（参考分辨率下的像素）。</summary>
        private const float BottomMargin = 24f;

        private GUIStyle panelStyle;
        private GUIStyle panelAccentStyle;
        private GUIStyle[] slotStyles;      // 下标 = (int)BuildBarUI.SlotState
        private GUIStyle slotNameStyle;
        private GUIStyle slotNameSelectedStyle;
        private GUIStyle slotNameLockedStyle;
        private GUIStyle slotChipStyle;
        private GUIStyle slotCostGoodStyle;
        private GUIStyle slotCostBadStyle;
        private GUIStyle hotkeyTextStyle;
        private GUIStyle hotkeyLockedTextStyle;
        private GUIStyle cardTitleStyle;
        private GUIStyle cardBodyStyle;
        private GUIStyle cardGoodStyle;
        private GUIStyle cardBadStyle;

        private float builtScale = -1f;

        /// <summary>
        /// 参考分辨率 → 屏幕的缩放系数。
        ///
        /// 这里手算一遍 CanvasScaler(ScaleWithScreenSize, Match 0.5) 的公式。
        /// 那个 Match 是**对数插值**，几何平均即 <c>sqrt(宽比 × 高比)</c>，不是算术平均 ——
        /// 写成 (宽比+高比)/2 在 21:9 上会偏大约 10%，整条栏的高度就飘了。
        /// </summary>
        public float Scale
        {
            get
            {
                float sx = Screen.width / BuildBarUI.ReferenceWidth;
                float sy = Screen.height / BuildBarUI.ReferenceHeight;
                return Mathf.Max(0.35f, Mathf.Sqrt(Mathf.Max(0.0001f, sx * sy)));
            }
        }

        /// <summary>建造栏现在是否应当出现在屏幕上。</summary>
        public bool IsVisible
        {
            get
            {
                return game != null
                       && tools != null
                       && tools.BuildMode
                       && game.buildables != null
                       && game.buildables.Length > 0;
            }
        }

        /// <summary>
        /// 建造栏在屏幕底部占掉的高度（像素）。返回 0 表示没显示，提示文字可以照原位置画。
        ///
        /// 给 <see cref="StarDefenseHUD"/> 用：它的提示行原本贴在屏幕底部，
        /// 正好落在建造栏的位置上。让 HUD 主动问一句，比各自猜一个魔法偏移量可靠 ——
        /// 栏高会随槽位数变化（见 <see cref="BuildBarUI.BarWidthFor"/>），写死数字迟早对不上。
        /// </summary>
        public float ReservedHeightPixels()
        {
            if (!IsVisible)
                return 0f;

            return (BottomMargin + BuildBarUI.DetailCardHeight + BuildBarUI.DetailCardGap
                    + BuildBarUI.BarHeight) * Scale;
        }

        private void OnGUI()
        {
            if (!IsVisible)
                return;

            float scale = Scale;
            if (Mathf.Abs(scale - builtScale) > 0.001f)
            {
                BuildStyles(scale);
                builtScale = scale;
            }

            BuildableConfig[] buildables = game.buildables;
            int slotCount = buildables.Length;
            int dividerCount = 0;

            float slotWidth = BuildBarUI.DesignSlotWidth;
            float barWidth = BuildBarUI.BarWidthFor(slotCount, dividerCount);
            float barHeight = BuildBarUI.BarHeight;

            // 竖向上从屏幕底边往上排，而不是从参考坐标系顶部往下推：
            // Scale 是宽高比的几何平均，ReferenceHeight × Scale 并不等于 Screen.height，
            // 按顶边定位会让整条栏随分辨率上下漂 —— 而栏是贴在底边的。
            float bottomMargin = BottomMargin * scale;
            float barTop = Screen.height - bottomMargin - barHeight * scale;
            float barLeft = (Screen.width - barWidth * scale) * 0.5f;

            GUI.Box(new Rect(barLeft, barTop, barWidth * scale, barHeight * scale),
                GUIContent.none, panelStyle);

            float slotTopRef = (barHeight - BuildBarUI.SlotHeight) * 0.5f;

            for (int i = 0; i < slotCount; i++)
            {
                BuildableConfig config = buildables[i];
                bool selected = i == tools.selectedBuildIndex;
                bool locked = !IsBuildableNow(config);

                Rect slotRect = new Rect(
                    barLeft + (BuildBarUI.BarPadding + i * (slotWidth + BuildBarUI.SlotGap)) * scale,
                    barTop + slotTopRef * scale,
                    slotWidth * scale,
                    BuildBarUI.SlotHeight * scale);

                DrawSlot(slotRect, config, selected, locked, scale);
            }

            DrawDetailCard(config: tools.CurrentBuildable(), barLeft: barLeft, barTop: barTop, scale: scale);
        }

        // ── 槽位 ────────────────────────────────────────────────────────────

        private void DrawSlot(Rect slot, BuildableConfig config, bool selected, bool locked, float scale)
        {
            // 底图选态：选中优先于锁定（BuildBarUI.BackgroundState 的约定）。
            // 于是"选中但资源不足"仍然是橙框选中态，锁定的表达交给图标压暗 + 成本标红这两个前景层。
            BuildBarUI.SlotState state = BuildBarUI.BackgroundState(selected, hovered: false, locked: locked);
            GUI.Box(slot, GUIContent.none, slotStyles[(int)state]);

            float padH = BuildBarUI.SlotPaddingHorizontal * scale;
            float iconSize = BuildBarUI.IconSize * scale;

            // 热键数字：**裸文字**，没有底图。
            // 依据是规范里的字段划分 —— 热键有自己的一组常量（HotkeyInsetX/Y + Hotkey /
            // HotkeyLocked 两个颜色），而 UI_BLD_Slice_Tag 配的是 TagBackground + RadiusTag，
            // 那一组是给**分组标签**用的。有胶囊底的话规范里会有一张 HotkeyTag 之类的切图，没有。
            //
            // 顺带这也是更好的信号设计：可用时亮黄、不可用时转灰，比"所有槽位都套同一个
            // 棕色底"多带一层"这个键现在按得出来吗"的信息。
            Rect hotkeyRect = new Rect(
                slot.x + BuildBarUI.HotkeyInsetX * scale,
                slot.y + BuildBarUI.HotkeyInsetY * scale,
                24f * scale,
                16f * scale);

            GUI.Label(hotkeyRect, config != null ? config.hotkey.ToString() : "-",
                locked ? hotkeyLockedTextStyle : hotkeyTextStyle);

            // 图标：水平居中。图标自带暗色背景与投影，是"一张小卡片"。
            //
            // 关于**四角**，有两件不同的事，别混：
            //   ①「图标会不会溢出槽位的圆角」—— 不会。图标比槽位小得多，压根碰不到
            //      槽位的圆角，所以不需要为**这个**加遮罩。
            //   ②「图标**自己**的四角要不要圆」—— 规范要求要，用 UI_BLD_Slice_Mask
            //      作 uGUI 的 Mask Graphic。理由是实测的：图标四角平均 #282D31，
            //      槽底 #141A22，约 20 级亮度差，方角在深色槽里是一道看得见的缝。
            //      但 **IMGUI 没有圆角裁剪能力**，而本栏是 OnGUI 的 —— 所以这里
            //      画出来的图标就是**方角**的，这是已知取舍。
            //      要真圆角只能把整条栏换成 uGUI，不是此处加几行能解决的。
            Rect iconRect = new Rect(
                slot.x + (slot.width - iconSize) * 0.5f,
                slot.y + (BuildBarUI.HotkeyInsetY + 16f + BuildBarUI.SlotContentGap) * scale,
                iconSize,
                iconSize);

            // 锁定态只压暗、不换图：换一张"锁定版图标"意味着美术要再交一套，
            // 而两种状态在语义上只差"现在建不建得了"，压暗就够了。
            Color old = GUI.color;
            if (locked)
                GUI.color = new Color(1f, 1f, 1f, BuildBarUI.LockedIconAlpha);

            Texture2D icon = config != null ? config.icon : null;
            if (icon != null)
            {
                // 贴图不是可读资产（isReadable=false），但绘制不需要可读，只有读像素才需要。
                GUI.DrawTexture(iconRect, icon, ScaleMode.StretchToFill, true);
            }
            else
            {
                // 没有图标不回退成空槽：空槽看起来像"这里本该有东西但丢了"，
                // 而真相是"这个建筑还没配图"。画一个它的代表色块，信息量反而更大。
                DrawSolid(iconRect, config != null ? config.color : Color.gray);
            }

            GUI.color = old;

            float nameY = iconRect.yMax + BuildBarUI.SlotContentGap * scale;
            float nameH = 20f * scale;
            float chipH = 16f * scale;

            GUIStyle nameStyle = selected
                ? slotNameSelectedStyle
                : (locked ? slotNameLockedStyle : slotNameStyle);

            GUI.Label(
                new Rect(slot.x + padH, nameY, slot.width - padH * 2f, nameH),
                config != null ? config.displayName : "—",
                nameStyle);

            // 成本 chip：槽位宽度只放得下"矿 40"这种短写，完整成本在详情卡里。
            // 颜色承载"够不够"：够用是常规色，不够标红 —— 否则玩家要点进去才知道差多少。
            string costText = config != null ? BuildBarUI.CostChip(config.cost) : string.Empty;
            GUIStyle costStyle = slotChipStyle;
            if (config != null && (selected || locked))
            {
                bool affordable = game.resources.CanAfford(config.cost);
                costStyle = affordable ? slotCostGoodStyle : slotCostBadStyle;
            }

            GUI.Label(
                new Rect(slot.x + padH, nameY + nameH, slot.width - padH * 2f, chipH),
                costText,
                costStyle);

            // 电力指示条：贴着槽位底边的一道细线，绿=发电、橙=耗电、灰=不耗电。
            // 这是唯一一处"一眼看出这条栏里谁是发电的"的地方，省得逐个点开详情卡。
            if (config != null)
            {
                float indicatorH = BuildBarUI.IndicatorHeight * scale;
                Rect indicator = new Rect(
                    slot.x + padH,
                    slot.yMax - indicatorH,
                    slot.width - padH * 2f,
                    indicatorH);

                Color power = BuildBarUI.PowerChipColor(config.powerCost, config.powerSupply);
                if (locked)
                    power.a = 0.45f;
                DrawSolid(indicator, power);
            }
        }

        // ── 详情卡 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 选中建筑的详情卡。位置在建造栏正上方、左对齐栏边。
        ///
        /// 它承担"当前选中的是哪个建筑"里最重要的那一半 —— 槽位受宽度限制只能放名字与成本，
        /// 而玩家真正要判断的是"我现在建得起吗、它吃多少电、占几点容量"。
        /// </summary>
        private void DrawDetailCard(BuildableConfig config, float barLeft, float barTop, float scale)
        {
            float width = BuildBarUI.DetailCardWidth * scale;
            float height = BuildBarUI.DetailCardHeight * scale;
            Rect card = new Rect(
                barLeft,
                barTop - (BuildBarUI.DetailCardGap + BuildBarUI.DetailCardHeight) * scale,
                width,
                height);

            bool affordable = config != null && game.resources.CanAfford(config.cost);
            bool capacityOk = config != null && game.HasCapacityFor(config);
            bool placeable = config != null && affordable && capacityOk;

            // 橙色描边只在"看中了却建不了"时出现。始终用描边的话，这个色就没有意义了。
            GUI.Box(card, GUIContent.none, placeable || config == null ? panelStyle : panelAccentStyle);

            float pad = BuildBarUI.DetailCardPadding * scale;
            float iconSize = BuildBarUI.DetailIconSize * scale;
            float textLeft = card.x + pad + iconSize + BuildBarUI.DetailCardGap * scale;
            float textWidth = card.xMax - pad - textLeft;

            Rect iconRect = new Rect(card.x + pad, card.y + pad, iconSize, iconSize);
            Texture2D icon = config != null ? config.icon : null;
            if (icon != null)
            {
                Color old = GUI.color;
                if (!placeable)
                    GUI.color = new Color(1f, 1f, 1f, BuildBarUI.LockedIconAlpha);
                GUI.DrawTexture(iconRect, icon, ScaleMode.StretchToFill, true);
                GUI.color = old;
            }
            else
            {
                DrawSolid(iconRect, config != null ? config.color : new Color(0.25f, 0.27f, 0.3f, 1f));
            }

            float lineH = 22f * scale;
            float y = card.y + pad;

            GUI.Label(new Rect(textLeft, y, textWidth, lineH * 1.4f),
                config != null
                    ? config.displayName + "　热键 " + config.hotkey
                    : "未选择建筑",
                cardTitleStyle);
            y += lineH * 1.4f + 2f * scale;

            if (config == null)
            {
                GUI.Label(new Rect(textLeft, y, textWidth, lineH * 3f),
                    "按 1-8 或滚轮挑选要修建的建筑。", cardBodyStyle);
                return;
            }

            GUI.Label(new Rect(textLeft, y, textWidth, lineH), BuildBarUI.CostChipFull(config.cost), cardBodyStyle);
            y += lineH;

            GUI.Label(new Rect(textLeft, y, textWidth, lineH),
                BuildBarUI.PowerChip(config.powerCost, config.powerSupply)
                + "　　" + BuildBarUI.CapacityChip(config.capacityCost)
                + "　　" + config.buildSeconds.ToString("0.#") + " 秒建成",
                cardBodyStyle);
            y += lineH;

            GUI.Label(new Rect(textLeft, y, textWidth, lineH), StatusText(config, affordable, capacityOk),
                placeable ? cardGoodStyle : cardBadStyle);
        }

        /// <summary>
        /// 当前能不能建。不可建时**说清差多少**，而不是只说"资源不足"——
        /// 玩家看到"金属差 20"，就知道再去敲两块矿；看到"资源不足"，只能靠猜。
        /// </summary>
        private string StatusText(BuildableConfig config, bool affordable, bool capacityOk)
        {
            var sb = new StringBuilder();

            if (!affordable)
            {
                sb.Append("资源不足：");
                AppendShortage(sb, "金属", game.resources.metal, config.cost.metal);
                AppendShortage(sb, "能源", game.resources.energy, config.cost.energy);
                AppendShortage(sb, "晶体", game.resources.crystal, config.cost.crystal);
                if (sb[sb.Length - 1] == '：')
                    sb.Append("—");
            }

            if (!capacityOk)
            {
                if (sb.Length > 0)
                    sb.Append("　");
                sb.Append("容量不足：需 ").Append(config.capacityCost)
                  .Append(" 点，剩 ").Append(game.FreeCapacity).Append(" 点");
            }

            return sb.Length == 0 ? "可以建造　左键放在虚影处" : sb.ToString();
        }

        private static void AppendShortage(StringBuilder sb, string label, int have, int need)
        {
            if (need <= have)
                return;
            if (sb[sb.Length - 1] != '：')
                sb.Append("　");
            sb.Append(label).Append("差 ").Append(need - have);
        }

        /// <summary>当前这一刻该建筑能不能建（资源 + 容量都够）。</summary>
        private bool IsBuildableNow(BuildableConfig config)
        {
            return config != null
                   && game.resources.CanAfford(config.cost)
                   && game.HasCapacityFor(config);
        }

        // ── 样式 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 建样式。字号是像素值，所以缩放一变就得重建 —— 只在 <see cref="Scale"/> 变化时调一次，
        /// 不是每帧。每帧 new GUIStyle 会产生持续托管垃圾，在 OnGUI 里这条尤其明显。
        /// </summary>
        private void BuildStyles(float scale)
        {
            // 字号必须跟着 Scale 走。留一个下限：极小窗口下字号归零会让标签直接消失，
            // 那种"界面看着在、字没了"的故障很难判断是布局错还是字体错。
            int Px(float baseSize)
            {
                return Mathf.Max(8, Mathf.RoundToInt(baseSize * scale));
            }

            // 底板与详情卡共用 Panel；栏底那一层不是"更暗的板"，
            // 而是靠槽位自身的暗底与内缩留白把层次做出来。
            panelStyle = Sliced(panel, BuildBarUI.BorderBox);
            panelAccentStyle = Sliced(panelAccent, BuildBarUI.BorderBox);

            slotStyles = new[]
            {
                Sliced(slotNormal, BuildBarUI.BorderBox),
                Sliced(slotHover, BuildBarUI.BorderBox),
                Sliced(slotSelected, BuildBarUI.BorderBox),
                Sliced(slotLocked, BuildBarUI.BorderBox),
            };

            slotNameStyle = Label(Px(13f), BuildBarUI.TextPrimary, FontStyle.Normal, TextAnchor.UpperCenter);
            slotNameSelectedStyle = Label(Px(13f), BuildBarUI.TextNameSelected, FontStyle.Bold, TextAnchor.UpperCenter);
            slotNameLockedStyle = Label(Px(13f), BuildBarUI.TextLocked, FontStyle.Normal, TextAnchor.UpperCenter);
            slotChipStyle = Label(Px(11f), BuildBarUI.TextDim, FontStyle.Normal, TextAnchor.UpperCenter);

            // 槽位里的成本 chip 与详情卡的状态行**不能共用样式**：一个居中、一个左对齐。
            // 共用的话槽位里的文字会贴着左边框，看着像被裁掉了半截。
            slotCostGoodStyle = Label(Px(11f), BuildBarUI.Gain, FontStyle.Bold, TextAnchor.UpperCenter);
            slotCostBadStyle = Label(Px(11f), BuildBarUI.Danger, FontStyle.Bold, TextAnchor.UpperCenter);

            hotkeyTextStyle = Label(Px(11f), BuildBarUI.Hotkey, FontStyle.Bold, TextAnchor.MiddleCenter);
            hotkeyLockedTextStyle = Label(Px(11f), BuildBarUI.HotkeyLocked, FontStyle.Bold, TextAnchor.MiddleCenter);

            cardTitleStyle = Label(Px(20f), BuildBarUI.TextPrimary, FontStyle.Bold, TextAnchor.UpperLeft);
            cardBodyStyle = Label(Px(13f), BuildBarUI.TextDim, FontStyle.Normal, TextAnchor.UpperLeft);
            cardGoodStyle = Label(Px(13f), BuildBarUI.Gain, FontStyle.Bold, TextAnchor.UpperLeft);
            cardBadStyle = Label(Px(13f), BuildBarUI.Danger, FontStyle.Bold, TextAnchor.UpperLeft);
        }

        /// <summary>
        /// 九宫格样式一律走 <see cref="BuildBarUI.SlicedBox"/>。
        /// 本类**不自己填 border** —— 那些数值只该有一份，填两处必然分叉。
        /// </summary>
        private static GUIStyle Sliced(Texture2D texture, RectOffset border)
        {
            if (texture == null)
                return Label(16, BuildBarUI.TextFaint, FontStyle.Normal, TextAnchor.MiddleCenter);

            return BuildBarUI.SlicedBox(texture, border);
        }

        private static GUIStyle Label(int fontSize, Color color, FontStyle fontStyle, TextAnchor anchor)
        {
            return new GUIStyle
            {
                fontSize = fontSize,
                fontStyle = fontStyle,
                alignment = anchor,
                wordWrap = false,
                richText = false,
                normal = { textColor = color },
            };
        }

        /// <summary>
        /// 纯色矩形。用 <c>Texture2D.whiteTexture</c> 而不是切图里的 UI_BLD_White_4：
        /// 它已经是一张 1×1 的可读白图，而白图**可以靠 GUI.color 染色**；
        /// 切图白块是资源资产，染色要额外绕一圈，收益为零。
        /// </summary>
        private static void DrawSolid(Rect rect, Color color)
        {
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }
    }
}
