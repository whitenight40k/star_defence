using UnityEngine;

namespace StarDefense
{
    public class StarDefenseHUD : MonoBehaviour
    {
        [Header("Wiring")]
        public StarDefenseGame game;
        public PlayerToolController playerTools;
        public Health playerHealth;
        public PlayerDownedState playerDowned;

        /// <summary>
        /// 建造栏。可以为空 —— 空的时候提示行退回原来的位置，
        /// 只是创造模式下会和栏叠在一起。不为了这一个偏移量去把两者做成硬依赖。
        /// </summary>
        public BuildBarHud buildBar;

        private GUIStyle labelStyle;
        private GUIStyle titleStyle;
        private GUIStyle warningStyle;
        private GUIStyle buildModeStyle;

        private void Awake()
        {
            labelStyle = new GUIStyle { fontSize = 16, normal = { textColor = Color.white } };
            titleStyle = new GUIStyle { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            warningStyle = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.45f, 0.2f, 1f) } };

            // 创造模式是本作里唯一"同一个按键做完全不同的事"的状态。
            // 它必须比其它状态更显眼 —— 玩家一旦忘了自己还在创造模式，
            // 对着一块矿石按左键却放下一座塔，是很难自己想明白为什么的。
            buildModeStyle = new GUIStyle { fontSize = 16, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.45f, 0.78f, 1f, 1f) } };
        }

        private void OnGUI()
        {
            if (game == null)
                return;

            DrawStatusPanel();
            DrawControlPanel();
            DrawHint();

            if (playerDowned != null && playerDowned.IsDowned)
                DrawDownedBanner();

            if (game.phase == GamePhase.Victory || game.phase == GamePhase.Defeat)
                DrawResultBanner();

            GUI.Label(new Rect(Screen.width * 0.5f - 6, Screen.height * 0.5f - 12, 24, 24), "+", titleStyle);
        }

        private void DrawStatusPanel()
        {
            DrawPanel(new Rect(16, 16, 380, 250));

            GUI.Label(new Rect(28, 26, 340, 28), "星球防线 · 第一版", titleStyle);
            GUI.Label(new Rect(28, 58, 360, 22), $"阶段：{FormatPhase(game.phase)}   剩余：{FormatTime(game.RemainingSeconds)}", labelStyle);
            GUI.Label(new Rect(28, 82, 360, 22), $"核心：{Mathf.RoundToInt(game.CoreHealth01 * 100f)}%   电力：{game.PowerDemand}/{game.PowerSupply}   护盾：{game.ShieldedBuildingCount}", labelStyle);
            GUI.Label(new Rect(28, 106, 360, 22), $"资源  金属:{game.resources.metal}  能源:{game.resources.energy}  晶体:{game.resources.crystal}", labelStyle);
            GUI.Label(new Rect(28, 130, 360, 22), $"威胁：{Mathf.RoundToInt(game.threat)} / {Mathf.RoundToInt(game.Balance.waveThreshold)}   敌人：{game.ActiveEnemies.Count}", labelStyle);

            // 容量满时用警示色，这是玩家需要立刻处理的状态（拆解或放弃继续扩张）
            bool capacityFull = game.FreeCapacity <= 0;
            GUI.Label(new Rect(28, 154, 360, 22), $"建筑容量：{game.UsedCapacity}/{game.BuildingCapacity}（剩余 {game.FreeCapacity}）", capacityFull ? warningStyle : labelStyle);
            GUI.Label(new Rect(28, 178, 360, 22), PlayerHealthText(), labelStyle);
            GUI.Label(new Rect(28, 202, 360, 22), ToolAndModeText(), playerTools != null && playerTools.BuildMode ? buildModeStyle : labelStyle);
            GUI.Label(new Rect(28, 226, 360, 22), BuildSlotText(), labelStyle);
        }

        private void DrawControlPanel()
        {
            DrawPanel(new Rect(Screen.width - 405, 16, 390, 282));

            GUI.Label(new Rect(Screen.width - 390, 28, 360, 22), "操作", titleStyle);
            GUI.Label(new Rect(Screen.width - 390, 62, 360, 22), "WASD 移动 / 鼠标转向 / Shift 奔跑 / 空格跳跃", labelStyle);
            GUI.Label(new Rect(Screen.width - 390, 88, 360, 22), "Esc 解锁鼠标", labelStyle);
            GUI.Label(new Rect(Screen.width - 390, 114, 360, 22), "Q 切换枪械 / 镐子；枪械左键射击", labelStyle);
            GUI.Label(new Rect(Screen.width - 390, 140, 360, 22), "X 在镐子上切换 采集 / 创造 模式（右键退出）", labelStyle);
            GUI.Label(new Rect(Screen.width - 390, 166, 360, 22), "创造：1-8 或滚轮选建筑，左键在虚影处放置", labelStyle);
            GUI.Label(new Rect(Screen.width - 390, 192, 360, 22), "采集：看向资源左键采矿；看向未完工建筑续建", labelStyle);
            GUI.Label(new Rect(Screen.width - 390, 218, 360, 22), "E 维修准星指向的建筑", labelStyle);
            GUI.Label(new Rect(Screen.width - 390, 244, 360, 22), "R 拆解准星建筑（返还 50% 成本，释放容量）", labelStyle);
            // 建筑清单不再列在这里：建造栏已经逐个槽位画出了图标、热键与成本，
            // 两处各写一份必然分叉，而且这里那行文字长到会被面板边缘裁掉。
            GUI.Label(new Rect(Screen.width - 390, 270, 360, 22), "创造模式：屏幕底部出现建造栏", labelStyle);
        }

        private void DrawHint()
        {
            if (string.IsNullOrEmpty(game.HintText))
                return;

            // 提示行原本贴在屏幕底部，正好压在建造栏上。主动问建造栏占了多少高度，
            // 而不是写死一个偏移：栏高跟着槽位数变，写死的数字迟早对不上。
            float reserved = buildBar != null ? buildBar.ReservedHeightPixels() : 0f;
            GUI.Label(new Rect(Screen.width * 0.5f - 210, Screen.height - 90 - reserved, 420, 32),
                game.HintText, warningStyle);
        }

        private void DrawDownedBanner()
        {
            DrawPanel(new Rect(Screen.width * 0.5f - 190, Screen.height * 0.5f - 55, 380, 110));
            GUI.Label(new Rect(Screen.width * 0.5f - 160, Screen.height * 0.5f - 22, 320, 44),
                $"被击倒 · {playerDowned.DownRemaining:F1}s 后复起", warningStyle);
        }

        private void DrawResultBanner()
        {
            DrawPanel(new Rect(Screen.width * 0.5f - 190, Screen.height * 0.5f - 55, 380, 110));
            GUI.Label(new Rect(Screen.width * 0.5f - 150, Screen.height * 0.5f - 22, 300, 44),
                game.phase == GamePhase.Victory ? "撤离成功" : "核心失守", titleStyle);
        }

        private string PlayerHealthText()
        {
            if (playerHealth == null)
                return "生命：--";

            return $"生命：{Mathf.CeilToInt(playerHealth.CurrentHealth)}/{Mathf.RoundToInt(playerHealth.MaxHealth)}";
        }

        /// <summary>
        /// 工具 + 模式 + 虚影状态合成一行。
        ///
        /// 这三件事要一起看才有意义："手里是镐子"只是前提，"在创造模式里"才是决定左键
        /// 做什么的那一项，再加上虚影当前能不能放 —— 三样齐了玩家才能确定按下去会发生什么。
        /// 拆成三行反而会让人漏看其中一行。
        /// </summary>
        private string ToolAndModeText()
        {
            if (playerTools == null)
                return "工具：--";

            if (playerTools.currentTool != PlayerTool.Pickaxe)
                return "工具：枪械　模式：射击";

            if (!playerTools.BuildMode)
                return "工具：镐子　模式：采集（按 X 进入创造）";

            return "工具：镐子　模式：创造　" + PreviewText();
        }

        private string PreviewText()
        {
            BuildPreview preview = playerTools != null ? playerTools.preview : null;
            if (preview == null)
                return "预览：无";

            if (!preview.HasPlacement)
                return "预览：准星未指向地面";

            return preview.IsValid ? "预览：可放置" : "预览：此处不可放置";
        }

        private string BuildSlotText()
        {
            if (playerTools == null || game.buildables == null)
                return "建造槽：无";

            BuildableConfig current = playerTools.CurrentBuildable();
            return current == null ? "建造槽：未选择（1-8 / 滚轮）" : $"建造槽：{current.displayName}";
        }

        private static string FormatPhase(GamePhase phase)
        {
            switch (phase)
            {
                case GamePhase.Landing: return "登陆";
                case GamePhase.Expansion: return "资源发展";
                case GamePhase.Defense: return "基地防守";
                case GamePhase.BossWarning: return "Boss预警";
                case GamePhase.BossFight: return "Boss战";
                case GamePhase.Evacuation: return "撤离";
                case GamePhase.Victory: return "胜利";
                case GamePhase.Defeat: return "失败";
                default: return phase.ToString();
            }
        }

        private static string FormatTime(int seconds)
        {
            return $"{seconds / 60:00}:{seconds % 60:00}";
        }

        private static void DrawPanel(Rect rect)
        {
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }
    }
}
