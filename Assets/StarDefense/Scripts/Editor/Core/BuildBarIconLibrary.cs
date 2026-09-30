using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// 建造栏图标与九宫格切图的查找入口。
    ///
    /// 存在的理由和 <see cref="ArtPrefabLibrary"/> 一样：把命名规则收敛到一处，
    /// 并且让"资产还没到"和"资产到了"两条路径都跑得通。
    ///
    /// 与 <see cref="StarDefenseAssetBinder"/> 的分工：
    ///   · AssetBinder 解的是「玩法 id → 3D Prefab」，产物供运行时建造使用；
    ///   · 本类解的是「玩法 id → 2D 图标」，产物供建造栏 UI 使用。
    /// 两者都按 id 收敛，但**映射关系不同**，所以不能合并 —— 美术给图标起的是造型名
    /// （Mortar / ElectricFence），玩法用的是功能名（cannon / wall），中间这一跳必须显式写出来。
    ///
    /// 图标是**单张 958×958 插画**（导入时限到 256），不是图集：
    /// 每个图标自带暗色背景与投影，直接铺进槽位即可，不需要再切图。
    /// </summary>
    internal static class BuildBarIconLibrary
    {
        internal const string IconDir = "Assets/StarDefense/Art/Textures/UI/Buildings";
        internal const string SliceDir = "Assets/StarDefense/Art/Textures/UI/BuildBar";

        /// <summary>
        /// 「玩法 id → 图标文件名（不含扩展名）」。**这是唯一一份映射，且是完整的一份。**
        ///
        /// 为什么是完整的而不是只列例外：美术侧的图标名是**造型名 + CamelCase**
        /// （UI_BLD_MachineGunTurret / UI_BLD_PowerGenerator），玩法侧的 id 是**功能名 + 全小写**
        /// （turret / gen）。两套命名**一处都对不上**，所以"先按同名找"这条捷径在本资产集里
        /// 一次也不会命中 —— 留着它只会让人误以为映射是"大部分自动、少数特例"。
        ///
        /// 逐个的对应关系，分两类 —— **第二类是临时借用，不是最终映射**：
        ///
        /// [A] 确定映射（6 条）。图标与 3D 模型同造型，唯一差别是命名风格：
        ///   · turret 机枪塔     ← MachineGunTurret  同名
        ///   · tesla  电磁塔     ← TeslaTower        同名
        ///   · radar  雷达       ← Radar             同名
        ///   · repair 维修站     ← RepairStation     同名（箱体 + 机械臂）
        ///   · shield 护盾发生器 ← ShieldGenerator   同名
        ///   · gen    发电机     ← PowerGenerator    同名
        ///
        /// [B] 临时借用（2 条）。美术侧台账把这两条明确列为**仍缺图**（ASSET_BACKLOG 的
        ///     "Open items raised to the user" 第 2 条：「Two icons are still missing —
        ///     炮塔 CannonTurret and 能量墙 EnergyWall; slot 05 currently borrows the
        ///     electric-fence art」）。当前分配是在缺图前提下的**就近顶替**，形态能对上，
        ///     但美术的原始槽位意图不是这个：
        ///   · cannon 炮塔   ← Mortar        图标是一门口径很大的座架炮、旁有炮弹箱，
        ///                                   与 BLD_CannonTurret_A 同形态，不会看错成别的建筑。
        ///                                   不用 Plasma：那是带等离子球的环形发射器，
        ///                                   更像电磁类装置，给炮塔是错的。
        ///   · wall   能量墙 ← ElectricFence 图标是两根立柱夹一片发光能量板，与
        ///                                   BLD_EnergyWall_A + BLD_EnergyWall_Node_A
        ///                                   （立柱 + 中间板）逐件对上；且台账说的
        ///                                   "slot 05 borrows the electric-fence art"
        ///                                   正是指能量墙借这张，与美术侧意图一致。
        ///
        /// 等美术补上 `UI_BLD_CannonTurret` / `UI_BLD_EnergyWall`，**只需改这两行**，
        /// 其余代码不动（映射只在本表收敛）。
        ///
        /// 加新建筑时：美术若沿用造型名，就在这里补一行；若直接用了玩法 id 做文件名，
        /// <see cref="IconNameFor"/> 的同名回退会接住，不必改这里。
        /// </summary>
        private static readonly Dictionary<string, string> ById = new Dictionary<string, string>
        {
            { "turret", "UI_BLD_MachineGunTurret" },
            { "cannon", "UI_BLD_Mortar" },
            { "tesla", "UI_BLD_TeslaTower" },
            { "wall", "UI_BLD_ElectricFence" },
            { "radar", "UI_BLD_Radar" },
            { "repair", "UI_BLD_RepairStation" },
            { "shield", "UI_BLD_ShieldGenerator" },
            { "gen", "UI_BLD_PowerGenerator" },
        };

        /// <summary>
        /// 图标名。先查映射表；表里没有时，再试"文件名就是玩法 id"这一种约定。
        /// 都没有则返回空串，由调用方决定是警告还是静默回退。
        /// </summary>
        internal static string IconNameFor(string configId)
        {
            if (string.IsNullOrEmpty(configId))
                return string.Empty;

            string mapped;
            if (ById.TryGetValue(configId, out mapped) && File.Exists(IconPath(mapped)))
                return mapped;

            string sameName = "UI_BLD_" + configId;
            if (File.Exists(IconPath(sameName)))
                return sameName;

            return string.Empty;
        }

        /// <summary>
        /// 取图标贴图。资产缺失时返回 null —— 建造栏会退化成纯色块，不会画一个空槽。
        ///
        /// 两级加载：先按 Texture2D 直接取，再退回 Sprite.texture。
        /// 这不是多余的防御：切图被导入成 Sprite 类型时，"主资产是 Texture2D 还是 Sprite"
        /// 在 Unity 各版本间变过，两种写法都出现过。分开写能同时兼容。
        /// 注意**贴图是不是 Sprite 类型都不影响本类可用性** —— 九宫格在 IMGUI 里靠
        /// GUIStyle.border 生效，那只认像素值，不认导入类型。所以九宫格切图的圆角
        /// 与导入设置无关，只要 PNG 边缘画对了就画得对。
        ///
        /// **图标的四角在 IMGUI 下是方的 —— 这是已知取舍，不是 bug，不要"顺手补齐"。**
        /// 图标母版刻意保留方角（958×958，四角不带透明圆弧），圆角由 UI 层负责：规范的
        /// 做法是拿 `UI_BLD_Slice_Mask` 作 uGUI 的 `Mask` Graphic 并设
        /// `showMaskGraphic = false`。之所以这么拆，是有实测依据的 —— 图标四角平均
        /// `#282D31`，而槽位底是 `#141A22`，亮度差约 20 级，方角在深色槽里会显出一道
        /// 可见的缝，所以非裁不可。但 **IMGUI 没有圆角裁剪能力**，本类又只能交出
        /// Texture2D，因此当前建造栏画出来的图标是方角的；规范同时记录了这个退化路径。
        /// 若要真圆角，唯一解是把建造栏整体换成 uGUI —— 那是重写，不是调参。
        /// </summary>
        internal static Texture2D LoadIcon(string configId)
        {
            string name = IconNameFor(configId);
            return string.IsNullOrEmpty(name) ? null : LoadTexture(IconPath(name));
        }

        /// <summary>按文件名取九宫格切图（如 "UI_BLD_Slice_Slot"）。</summary>
        internal static Texture2D LoadSlice(string sliceName)
        {
            if (string.IsNullOrEmpty(sliceName))
                return null;
            return LoadTexture(SlicePath(sliceName));
        }

        internal static Texture2D LoadTexture(string path)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture != null)
                return texture;

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            return sprite != null ? sprite.texture : null;
        }

        /// <summary>
        /// 目录里有、但没有任何建筑在用的图标。
        ///
        /// 美术按一份更长的建筑名单交付了 15 张图标，而玩法侧目前只实现了 8 种；
        /// 剩下几张不是错误，是"还没做到"。把差异报出来，免得下次有人以为漏导了。
        /// </summary>
        internal static string DescribeUnusedIcons(IEnumerable<BuildableConfig> buildables)
        {
            if (!AssetDatabase.IsValidFolder(IconDir))
                return "图标目录不存在：" + IconDir;

            var used = new HashSet<string>();
            if (buildables != null)
            {
                foreach (BuildableConfig config in buildables)
                {
                    if (config == null)
                        continue;
                    string name = IconNameFor(config.id);
                    if (!string.IsNullOrEmpty(name))
                        used.Add(name);
                }
            }

            var unused = new List<string>();
            int total = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { IconDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase))
                    continue;

                total++;
                string name = Path.GetFileNameWithoutExtension(path);
                if (!used.Contains(name))
                    unused.Add(name);
            }

            if (total == 0)
                return "图标目录为空：" + IconDir;

            if (unused.Count == 0)
                return total + " 张图标全部在用";

            unused.Sort();
            var sb = new StringBuilder();
            sb.Append(total).Append(" 张里 ").Append(unused.Count).Append(" 张未被任何建筑引用：");
            sb.Append(string.Join(" ", unused.ToArray()));
            return sb.ToString();
        }

        private static string IconPath(string name)
        {
            return IconDir + "/" + name + ".png";
        }

        private static string SlicePath(string name)
        {
            return SliceDir + "/" + name + ".png";
        }
    }
}
