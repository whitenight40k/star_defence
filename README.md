# 星球防线 / Star Defense

第一人称视角的星球塔防原型。

**技术栈**：Unity `2022.3.62f3c1`（中国区版）· Built-in 渲染管线 · Linear 色彩空间 · 无第三方运行时依赖

---

## 这是什么

玩家以宇航员的身份降落在一颗白昼沙漠星球上，在一座八边形平台基地周围展开防御。视野是第一人称；
资源要自己拿镐子采，防御要自己选位置摆，虫群从四个方向按自己的节奏压过来。

当前状态是一个**可玩原型**：核心闭环（采集 → 建造 → 抵御 → 倒地复活）已经跑通，
美术资产已从程序化占位件切换为正式的卡通低多边形模型。

> **本仓库是引擎工程。**
> 素材工程（Blender `.blend` 源文件、FBX 导出、贴图、概念图）按项目约定单独存放、**不进入本仓库**。
> 进入运行时所需的最终资产已导入 `Assets/StarDefense/Art/`，本仓库可以独立打开并运行。

---

## 玩法概要

- **第一人称**：鼠标视角，眼位取自角色骨架上的相机插槽（不写死高度），自身模型通过独立图层从相机中剔除。
- **采集**：手持镐子在星球表面开采金属 / 能源 / 晶体三种资源。资源点共 16 个，分布在三圈环形带上，
  越远的圈带风险越高、回报越好。
- **建造**：按 `X` 进入创造模式，把建筑摆到地面上。施工需要时间，**没建完的建筑不挡路**。
- **抵御**：敌人从四个刷怪点按可配置的节奏进入战场（首批延迟 / 批次间隔 / 每批数量各自可调），
  目标是基地核心，其次是玩家；**路上撞到已建成的建筑会停下来拆它**。
- **失败与复起**：血量归零后倒地，倒计时结束在基地平台上的复活点复起。

### 操作

| 按键 | 作用 |
|---|---|
| `WASD` / 鼠标 | 移动 / 视角 |
| `Shift` | 奔跑 |
| `Space` | 跳跃 |
| `Esc` | 锁定 / 解锁鼠标 |
| 鼠标左键 | 使用当前工具（射击 / 采集 / 放置） |
| 鼠标右键 | 退出建造模式 / 取消选择 |
| `Q` | 切换 镐子 ↔ 枪 |
| `X` | 切换 采集 / 创造（仅手持镐子时可用） |
| `1`–`8` / 滚轮 | 创造模式下选择建筑 |
| `E` / `R` | 修理 / 拆除 |

---

## 目录结构

```text
Assets/StarDefense/
  Scripts/Runtime/       # 37 个运行时脚本（含 Core、Core/Terrain、Core/Waves、Core/Variables）
  Scripts/Editor/        # 18 个编辑器脚本：场景构建管线、资产构建器、核验器
  ScriptableObjects/     # 数据层：8 种建筑 + 7 种敌人 + 全局平衡配置
  Art/                   # 导入的模型、贴图、材质、动画、Prefab
  Scenes/                # 主场景 PlanetDefense_FirstVersion.unity
  Meshes/                # 程序化烘焙出的地形网格
.workbuddy/              # 项目规范、美术风格指南、AI 协作 skills
Logs/                    # 管线日志（未入库）
```

---

## 快速开始

1. 用 **Unity 2022.3.62f3c1（中国区版）** 打开本目录。
   `Packages/manifest.json` 依赖 `cn.unity.uos.launcher`，用国际版编辑器打开可能拉取失败。
2. 首次打开时，编辑器会自动执行资产管线并在 `Logs/StarDefensePipeline.txt` 写下过程记录。
   管线是全工程唯一的资产与场景生成入口，共 7 步。
3. 打开 `Assets/StarDefense/Scenes/PlanetDefense_FirstVersion.unity` 并 Play。

场景是**纯生成物**：不手工编辑场景文件，改逻辑改的是构建器，然后重跑管线。

---

## 架构约定

几条不读代码猜不到、且**写错了也不会报错**的规则：

- **轴向与原点**：`Assets/` 里的资产来自 Blender，FBX 导入为 `axis_forward=-Z / axis_up=Y`，
  于是 Blender 里的 `(x, y, z)` 在 Unity 中是 `(x, z, -y)`。资产分 Z-up 与 Y-up 两批
  （建筑全部属于 Z-up 批，根节点带 `-90°X` 校正）。因此**摆姿态绝不能直接写世界旋转** ——
  那会抹掉这套校正，表现为建筑整体横躺且落点错位。统一走 `Runtime/Core/BuildingPose.cs`：
  先从当前姿态取基准，再用 `yaw * base` 写入（从基准重算，所以是幂等的，虚影每帧调用不会自转）。
  所有摆放物的**原点都在底面中心**，因此落点直接取地形高度即可。

- **高度唯一来源**：`Runtime/Core/Terrain/PlanetTerrain.cs` 是全工程唯一的地形高度函数，
  纯函数、不使用 `UnityEngine.Random`。地形网格由编辑器侧烘焙成 `Meshes/` 下的资产。
  单位移动只推 XZ，`y` 每帧吸附到地面。改地形参数后必须重建场景。

- **数据驱动**：可调数值一律放 ScriptableObject，不硬编码进场景里的 MonoBehaviour。

- **构建器失败绝不写标记**：管线的跳过条件是「标记 + 代表性产物 + 源戳」三者齐全。
  失败却写标记，会导致"修好了却像没生效"。改了构建器逻辑必须同步提升 `BuilderRevision`。

- **新增组件字段与老场景**：给已存在场景中的组件加序列化字段时，老场景里全是默认值
  （`0` / `false` / `null`）。因此开关字段优先用**否定式命名**（如 `suppressAutoWaves`），
  让"忘了接线"的失败形态是**少做一件事**，而不是做错事。

---

## 内容清单

**可建造（8）**：`MachineGunTurret` · `CannonTurret` · `TeslaTower` · `EnergyWall` ·
`PowerGenerator` · `RepairStation` · `ShieldGenerator` · `Radar`

**敌人（7）**：`Crawler` · `Bomber` · `Burrower` · `Jammer` · `SniperBug` · `Thief` · `PlanetBeast`（Boss）

玩法 id 与美术资产名**故意是两套命名**（例如 `crawler` → `Charger`、`boss` → `PlanetBeast`），
两者只在 `Editor/Core/StarDefenseAssetBinder.cs` 的一处映射表里收敛。

---

## 当前状态

已实现：第一人称控制、地形、资源采集、8 种建筑与施工/修理/拆除、7 种敌人与寻路攻击、
四向刷怪点、威胁值驱动的波次、玩家倒地与复活、HUD 与新手提示。

已知限制：

- `Thief`（窃贼虫）暂无独立模型，当前由占位件顶替 —— 这是**预期状态**，不是缺陷。
- 无音频资产（素材工程 `Audio/` 为空）。
- 仅一个场景、单人开发者本地流程，无联网 / 存档系统。

---

## 相关文档

本仓库的 `.workbuddy/` 下保存了项目的规范文档与自动化 skills，是协作时的**唯一副本**：

- `project-rules/PROJECT_PATHS.md` —— 双工程结构、目录约定、素材流转流程
- `project-rules/OBJECT_TAXONOMY.md` —— 对象分类与命名规则
- `project-rules/ASSET_BACKLOG.md` —— 资产缺口与已完成批次台账
- `skills/star-defense-production/` —— 生产管线（分类 / 建模 / 动作 / 集成）与交接清单
- `skills/star-defense-art-style/` —— 美术风格与建模契约
