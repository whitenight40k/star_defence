# 星球防线 / Star Defense

Unity 做的第一人称塔防小游戏，个人项目，目前是个能跑的原型。

玩家以宇航员身份降落在一颗永远白天的沙漠星球上，拿镐子采资源、造防御塔，挡住从四个方向压过来的虫群。基地是一座八边形平台，正中是核心：撑到撤离窗口结束就算通关，核心被拆就失败。

美术已经从最初的程序化方块换成正式的卡通低模。音效还没做，联网和存档也都没有。

## 跑起来

需要 **Unity 2022.3.62f3c1**（中国区版）。`Packages/manifest.json` 里依赖了 `cn.unity.uos.launcher`，用国际版编辑器打开可能拉不到包。

打开工程后等编辑器自动跑完资产生成管线，然后打开 `Assets/StarDefense/Scenes/PlanetDefense_FirstVersion.unity` 按 Play。第一次打开会自动跑一遍，过程记在 `Logs/StarDefensePipeline.txt`。

**场景是脚本生成的，别手动改。** 要加东西就改 `Assets/StarDefense/Scripts/Editor/` 下的构建器，然后重跑管线 —— 手改的场景下次重建就没了。

## 操作

| 按键 | 作用 |
|---|---|
| WASD / 鼠标 | 移动 / 转视角 |
| Shift | 跑 |
| Space | 跳 |
| 左键 | 用当前工具（开枪 / 挖矿 / 放建筑） |
| 右键 | 退出建造模式 / 取消选中 |
| Q | 切镐子和枪 |
| X | 切采集 / 建造（拿着镐子才能切） |
| 1–8 / 滚轮 | 建造模式下选建筑 |
| E / R | 修理 / 拆除 |
| Esc | 锁定或释放鼠标 |

## 玩法

- 地表有 16 个矿点：金属 6、能源 6、晶体 4。半径分三环（内环 20~30 m、中环 30~45 m、外环 45~65 m），晶体只在最外环 —— 想提升火力就得往虫子来的方向走。
- 攒够资源按 X 进建造模式，把建筑摆到地上。建筑要施工一段时间才建成，**没建完的可以直接穿过去**。
- 敌人有四个刷怪点，各自独立计时、批量进场，首批延迟、间隔和每批数量都能在 Inspector 里单独调。
- 敌人先打基地核心，核心打不到才追玩家；路上撞到**已经建成**的建筑会停下来拆它。
- 血量归零倒地，几秒后在基地平台上的复活点起来。

## 内容

**建筑 8 种**：机枪塔、加农炮、特斯拉塔、能量墙、发电机、维修站、护盾发生器、雷达

**敌人 7 种**：爬虫、爆破虫、掘地虫、干扰虫、狙击虫、窃贼虫、行星野兽（Boss）

代码里玩法 id 和美术资产名是两套（`crawler` 对应 `Charger`，`boss` 对应 `PlanetBeast`），映射集中在 `Editor/Core/StarDefenseAssetBinder.cs` 一处。

## 目录

```text
Assets/StarDefense/
  Scripts/Runtime/      运行时脚本
  Scripts/Editor/       编辑器脚本：场景构建管线、资产构建器
  ScriptableObjects/    数值配置：建筑 / 敌人 / 全局平衡
  Art/                  模型、贴图、材质、动画、Prefab
  Scenes/               主场景
  Meshes/               烘焙出来的地形网格
.workbuddy/             项目规范与 AI 协作文档，也是协作时的唯一副本
```

## 已知问题

- 窃贼虫还没有模型，现在是占位件顶替，属于预期状态。
- 没有音效，素材工程的 `Audio/` 是空的。
- 只有一个场景，没有联网、没有存档。

## 改代码前值得知道的几件事

都是那种改错了不报错、只能靠看画面发现的：

- 资产从 Blender 导进来时是 `axis_forward=-Z / axis_up=Y`，所以 Blender 的 `(x, y, z)` 到 Unity 变成 `(x, z, -y)`；建筑那一批的根节点还带 `-90°X` 校正，直接写世界旋转会把校正抹掉，模型整个横躺。摆位置统一走 `Runtime/Core/BuildingPose.cs`，别自己拼落点和朝向。
- 地形高度只有 `Runtime/Core/Terrain/PlanetTerrain.cs` 一个来源，是个纯函数。改了地形参数要重建场景。
- 所有摆放物的原点都在底面中心，落点直接取地形高度就行，不要额外加偏移。
- 可调数值一律放 ScriptableObject，不硬编码进场景里的组件。
- 给已经存在的组件加序列化字段时，老场景里读出来全是默认值（`0` / `false` / `null`）。所以开关类字段用否定式命名（比如 `suppressAutoWaves`），让"忘了接线"的后果是少做一件事，而不是做错事。
- 构建器失败的时候不要写完成标记，否则会出现"修好了却像没生效"。改了构建器逻辑记得同步升 `BuilderRevision`。

细节都在 `.workbuddy/` 里：

- `project-rules/PROJECT_PATHS.md` 双工程结构、目录约定、素材流转
- `project-rules/OBJECT_TAXONOMY.md` 对象分类与命名
- `project-rules/ASSET_BACKLOG.md` 资产缺口台账
- `skills/star-defense-production/` 生产管线与交接清单
- `skills/star-defense-art-style/` 美术风格与建模契约
