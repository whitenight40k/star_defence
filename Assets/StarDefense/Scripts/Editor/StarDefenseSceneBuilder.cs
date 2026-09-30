using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StarDefense.EditorTools
{
    public static class StarDefenseSceneBuilder
    {
        private const string Root = "Assets/StarDefense";
        private const string ScenePath = Root + "/Scenes/PlanetDefense_FirstVersion.unity";

        /// <summary>重建会替换整个场景，旧文件先复制到这里（.bak 后缀不会被 Unity 当场景导入）。</summary>
        private const string SceneBackupPath = "Logs/PlanetDefense_FirstVersion.scene.bak";

        /// <summary>角色契约里的右手持械插槽。PlayerAssetBuilder 把枪和镐都挂在这里。</summary>
        private const string HandSocketName = "Socket_RightHandTool";

        /// <summary>角色契约里的眼位插槽。FirstPersonController 与这里共用同一个名字。</summary>
        private const string EyeSocketName = FirstPersonController.EyeSocketName;

        /// <summary>竖直 FOV。第一人称视口，别随手调大 —— 越宽，近处的自身身体在画面边缘越膨胀。</summary>
        private const float PlayerFieldOfView = 74f;

        /// <summary>近裁剪面，理由见 FirstPersonController.DefaultNearClipPlane。</summary>
        private const float PlayerNearClipPlane = FirstPersonController.DefaultNearClipPlane;

        /// <summary>造型资产缺席（图元胶囊玩家）时的眼高。</summary>
        private static readonly Vector3 FallbackEyeLocal = new Vector3(0f, 1.62f, 0f);

        /// <summary>Unity 内置图元网格的名字，用来数「还有多少对象是回退图元」。</summary>
        private static readonly string[] PrimitiveMeshNames =
        {
            "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad",
        };

        /// <summary>按名在子树里找节点（含隐藏节点）。找不到返回 null。</summary>
        private static Transform FindDeep(Transform root, string name)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name == name)
                    return all[i];
            }
            return null;
        }

        /// <summary>
        /// 玩家眼位（相对玩家根节点）：优先取美术的 <c>Socket_Camera</c>，缺席或数值不可信时回退固定眼高。
        ///
        /// 为什么不能写死 1.62：本角色净高 1.60 m、头在 1.20~1.55 m，1.62 落在头顶**外面** ——
        /// 低头就把自己的头顶和护目镜收进画面，也就是"穿模"。造型侧给出的眼位是
        /// <c>(0, 1.335, 0.100)</c>，那才是瞳孔位置。
        ///
        /// 校验只做量级判断：插槽还在骨头上的话，把它父亲到骨骼根上量出来的坐标会是
        /// 完全另一个数量级，这里拦下来、退回固定眼高，比默默把相机甩到天上强。
        /// </summary>
        private static Vector3 ResolveEyeLocal(Transform playerRoot, out string source)
        {
            Transform socket = FindDeep(playerRoot, EyeSocketName);
            if (socket == null)
            {
                source = $"回退固定眼高 {FallbackEyeLocal:F3}（没有 {EyeSocketName}）";
                return FallbackEyeLocal;
            }

            Vector3 local = playerRoot.InverseTransformPoint(socket.position);
            bool plausible =
                !float.IsNaN(local.x) && !float.IsNaN(local.y) && !float.IsNaN(local.z)
                && local.y > 0.3f && local.y < 2.5f
                && Mathf.Abs(local.x) < 1f && Mathf.Abs(local.z) < 1f;

            if (!plausible)
            {
                source = $"回退固定眼高 {FallbackEyeLocal:F3}（{EyeSocketName} 数值异常 {local}）";
                return FallbackEyeLocal;
            }

            source = $"{EyeSocketName} {local:F3}";
            return local;
        }

        [MenuItem("Star Defense/Build First Version Scene", false, 10)]
        public static void BuildFirstVersionScene()
        {
            // 播放模式下 EditorSceneManager.NewScene 会被 Unity 直接拒绝
            // （"This cannot be used during play mode, please use SceneManager.CreateScene()"）。
            // 提前拦掉，给出可读信息，而不是让调用方收到一串原生栈。
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new System.InvalidOperationException(
                    "播放模式下不能重建场景：EditorSceneManager.NewScene 在运行期被 Unity 禁止。"
                        + "请先退出播放模式再重建。"
                );

            EnsureFolders();
            GameBalanceConfig balance = CreateBalance();
            BuildableConfig[] buildables = CreateBuildables();
            EnemyConfig[] enemies = CreateEnemies(out EnemyConfig boss);

            // 重建前的顺序是有讲究的：
            //   1) 备份旧场景文件（可回滚）；
            //   2) 把当前打开的场景落盘 —— NewScene(Single) 遇到未保存改动会弹模态保存框，
            //      而本流程由 [InitializeOnLoad] 自动触发，一个弹窗就能把编辑器卡在启动阶段；
            //   3) 删掉旧场景资产（此刻已无未保存内容，删除不会弹框）；
            //   4) 最后 NewScene，全程无交互。
            BackupSceneFile();
            FlushOpenScenes();
            DeleteSceneAsset();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "PlanetDefense_FirstVersion";

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.74f, 0.78f, 0.84f, 1f);
            RenderSettings.fog = true;
            RenderSettings.fogColor = new Color(0.73f, 0.83f, 0.92f, 1f);
            RenderSettings.fogDensity = 0.004f;

            Material sand = CreateMaterial("Mat_DesertSand", new Color(0.82f, 0.62f, 0.38f, 1f));
            Material rock = CreateMaterial("Mat_Rock", new Color(0.43f, 0.34f, 0.28f, 1f));
            Material blue = CreateMaterial("Mat_PlayerBlue", new Color(0.22f, 0.5f, 0.95f, 1f));

            CreateSun();
            CreateGround(sand);
            CreateRocks(rock);

            Transform enemyRoot = new GameObject("Enemies").transform;
            Transform buildingRoot = new GameObject("Buildings").transform;
            Transform resourceRoot = new GameObject("Resources").transform;
            Transform spawnRoot = new GameObject("EnemySpawnPoints").transform;

            Transform[] spawns = CreateSpawnPoints(spawnRoot);
            CreateResources(resourceRoot);
            Transform core = CreateCore(buildingRoot);
            Transform respawn = CreatePlayerRespawnPoint(buildingRoot);
            Transform player = CreatePlayer(blue, out PlayerToolController tools, out Health playerHealth, out BuildPreview buildPreview);
            CreateAllies(blue, out AllyAgent[] allies);

            GameObject gameObject = new GameObject("StarDefenseGame");
            StarDefenseGame game = gameObject.AddComponent<StarDefenseGame>();
            game.balance = balance;
            game.buildables = buildables;
            game.enemies = enemies;
            game.bossConfig = boss;
            game.core = core;
            game.player = player;
            game.enemyRoot = enemyRoot;
            game.buildingRoot = buildingRoot;
            game.resourceRoot = resourceRoot;
            game.spawnPoints = spawns;

            // 刷怪点的出口与虫种只能在 game 存在之后回填（CreateSpawnPoints 跑在前面）。
            WireSpawnPoints(spawns, game, enemies);

            tools.game = game;
            // 建造虚影要在 StarDefenseGame 存在之后才能接线：CreatePlayer 跑在它前面，
            // 那时候连 game 对象都还没有。虚影缺 game 时只是保持隐藏，不会报错。
            buildPreview.game = game;
            foreach (AllyAgent ally in allies)
                ally.game = game;

            PlayerDownedState downedState = player.GetComponent<PlayerDownedState>();
            if (downedState != null)
            {
                downedState.game = game;
                downedState.respawnPoint = respawn;
            }

            StarDefenseHUD hud = gameObject.AddComponent<StarDefenseHUD>();
            hud.game = game;
            hud.playerTools = tools;
            hud.playerHealth = playerHealth;
            hud.playerDowned = downedState;

            // 建造栏要在 StarDefenseGame 之后建：它读 game.buildables 与 game.resources。
            hud.buildBar = CreateBuildBar(game, tools);

            CreateStartingDefenses(buildingRoot, game, buildables);

            ReportSceneComposition(scene);

            Selection.activeGameObject = gameObject;
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[StarDefenseSceneBuilder] 第一版场景已生成：{ScenePath}");
        }

        /// <summary>重建是破坏性的，先把旧场景文件复制一份。失败不阻断（重建仍然继续）。</summary>
        private static void BackupSceneFile()
        {
            try
            {
                if (!File.Exists(ScenePath))
                {
                    Debug.Log("[StarDefenseSceneBuilder] 无旧场景文件可备份，直接新建。");
                    return;
                }

                Directory.CreateDirectory("Logs");
                File.Copy(ScenePath, SceneBackupPath, true);
                Debug.Log("[StarDefenseSceneBuilder] 旧场景已备份：" + SceneBackupPath);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[StarDefenseSceneBuilder] 备份旧场景失败（继续重建）：" + e.Message);
            }
        }

        /// <summary>
        /// NewScene(Single) 在当前场景有未保存改动时会弹保存对话框。本流程由 [InitializeOnLoad]
        /// 自动触发，弹窗会卡住编辑器加载，所以先把脏场景落盘，让弹窗条件不成立：
        ///   · 文件还在的 → 存回原处；
        ///   · 文件已不在的（场景被删过、或未命名场景）→ 另存到 Logs/SceneRecovery，避免丢数据。
        /// </summary>
        private static void FlushOpenScenes()
        {
            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                Scene open = SceneManager.GetSceneAt(i);
                if (!open.IsValid() || !open.isLoaded || !open.isDirty)
                    continue;

                try
                {
                    if (!string.IsNullOrEmpty(open.path) && File.Exists(open.path))
                    {
                        EditorSceneManager.SaveScene(open);
                        Debug.Log("[StarDefenseSceneBuilder] 重建前已保存：" + open.path);
                    }
                    else
                    {
                        const string recoveryDir = "Logs/SceneRecovery";
                        Directory.CreateDirectory(recoveryDir);
                        string name = string.IsNullOrEmpty(open.name) ? "Untitled" : open.name;
                        foreach (char invalid in Path.GetInvalidFileNameChars())
                            name = name.Replace(invalid, '_');
                        string path =
                            recoveryDir + "/" + name + "_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".unity";
                        EditorSceneManager.SaveScene(open, path);
                        Debug.Log("[StarDefenseSceneBuilder] 未保存的场景已另存：" + path);
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[StarDefenseSceneBuilder] 保存打开中的场景失败（继续重建）：" + e.Message);
                }
            }
        }

        /// <summary>删掉旧场景资产，确保生成的是全新文件、不残留任何旧对象。</summary>
        private static void DeleteSceneAsset()
        {
            if (File.Exists(ScenePath) && AssetDatabase.DeleteAsset(ScenePath))
                Debug.Log("[StarDefenseSceneBuilder] 已删除旧场景，将重新生成：" + ScenePath);
        }

        /// <summary>
        /// 重建后自检：数一遍场景里有多少对象是美术 Prefab 实例、多少还是回退图元。
        ///
        /// 「场景里还是一堆胶囊体」只有两种成因 —— 资产没生成，或者绑定没跑；
        /// 两者都会让回退数量偏高。把它打成一行显式数字，出问题时不用再逐个翻资产目录比对。
        ///
        /// 两个计数口径都刻意收窄了，否则数字会大得没有意义：
        ///   · Prefab 实例只数「顶层实例根」—— Prefab 内部每个子物体都报告 Connected，
        ///     照单全收会把 40 块岩石数成两百多个；
        ///   · 图元只看对象**自身**的 MeshFilter —— 否则 Buildings / Resources 这类空壳父节点
        ///     会因为"子物体里有图元"被算进去，凭空多出几项。
        /// </summary>
        private static void ReportSceneComposition(Scene scene)
        {
            var prefabRoots = new List<string>();
            var fallbacks = new List<string>();
            var artBuildings = new List<string>();
            var procBuildings = new List<string>();
            var primitiveBuildings = new List<string>();

            foreach (GameObject sceneRoot in scene.GetRootGameObjects())
            {
                foreach (Transform t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = t.gameObject;

                    // 建筑单独归类：运行时建造走的是 Instantiate，克隆体不带 Prefab 连接，
                    // 所以不能按"是否 Prefab 实例"判断，要看它自己的网格是不是内置图元。
                    //
                    // 程序化占位建筑单列一桶：它们是"该资产还没交付"的**已知**回退，
                    // 和"本该有资产却没有、裸着一个图元"不是一回事，混在一起会让这份报告
                    // 在美术逐步交付的过程中一直显示"还有图元"，真正的问题反而被淹掉。
                    if (t.GetComponent<Buildable>() != null)
                    {
                        if (ProcPrototypes.IsPrototype(go))
                            procBuildings.Add(t.name);
                        else if (HasPrimitiveMesh(go))
                            primitiveBuildings.Add(t.name);
                        else
                            artBuildings.Add(t.name);
                        continue;
                    }

                    // 占位件的子部件本身全是内置图元网格，不排除的话这份报告永远显示"还有几十个图元"。
                    if (ProcPrototypes.IsPrototype(go))
                        continue;

                    if (PrefabUtility.GetPrefabInstanceStatus(go) == PrefabInstanceStatus.Connected)
                    {
                        // 父级也是 Prefab 实例，说明它只是 Prefab 的内部结构，不是摆在场景里的一件资产。
                        bool topLevel =
                            t.parent == null
                            || PrefabUtility.GetPrefabInstanceStatus(t.parent.gameObject)
                                != PrefabInstanceStatus.Connected;

                        if (topLevel)
                            prefabRoots.Add(t.name);
                        continue;
                    }

                    if (HasPrimitiveMesh(go))
                        fallbacks.Add(t.name);
                }
            }

            int buildings = artBuildings.Count + procBuildings.Count + primitiveBuildings.Count;
            Debug.Log(
                $"[StarDefenseSceneBuilder] 场景构成：美术 Prefab 实例 {prefabRoots.Count} 个；"
                    + $"建筑 {buildings} 座（真模型 {artBuildings.Count} / 程序化占位 {procBuildings.Count} / 裸图元 {primitiveBuildings.Count}）；"
                    + $"其它回退图元 {fallbacks.Count} 个。"
            );

            // 程序化占位件单独交代一行：它意味着"这个玩法对象的美术资产还没到"，
            // 是个可以接受的中间状态，但必须让人一眼看得出是哪几样，而不是靠翻资产目录猜。
            if (procBuildings.Count > 0)
                Debug.Log(
                    "[StarDefenseSceneBuilder] 使用程序化占位模型的建筑（对应美术资产尚未交付）："
                        + string.Join(", ", procBuildings)
                );

            // 地面不再是图元 Plane，而是按 PlanetTerrain 高度场生成的网格 ——
            // 它不在"回退图元"统计里是对的（不是缺资产的替代品），但要单独交代清楚，
            // 否则看日志的人会以为地面凭空消失了。
            Debug.Log(
                $"[StarDefenseSceneBuilder] 地面：{PlanetTerrain.Size:F0}×{PlanetTerrain.Size:F0} m 程序化高度场"
                    + $"（基地平台 {PlanetTerrain.FlatRadius:F0} m 内水平，外围沙丘脊 {PlanetTerrain.RimHeight:F0} m）；"
                    + $"出生点高度 {FormatSpawnHeights(scene)}。"
            );

            var remaining = primitiveBuildings.Concat(fallbacks).Distinct().ToList();
            if (remaining.Count > 0)
                Debug.Log(
                    "[StarDefenseSceneBuilder] 仍是图元的对象（若不在预期内，说明对应资产缺失或未绑定）："
                        + string.Join(", ", remaining)
                );

            // 敌人是运行时刷出来的，场景里没有实例，所以上面那份统计永远看不到它们。
            // 单独报一行 —— 否则"敌人到底有没有换上真模型"在这份报告里查不到，
            // 只能等开打才看见虫子还是球。
            var missingEnemies = ArtPrefabLibrary.EnemyPrefabs
                .Where(name => ArtPrefabLibrary.Find(name) == null)
                .ToList();

            Debug.Log(
                $"[StarDefenseSceneBuilder] 敌人资产："
                    + $"{ArtPrefabLibrary.EnemyPrefabs.Length - missingEnemies.Count}/{ArtPrefabLibrary.EnemyPrefabs.Length} 只有真模型"
                    + (
                        missingEnemies.Count > 0
                            ? "；缺 " + string.Join(", ", missingEnemies) + "（运行时退回图元）"
                            : "（含 Boss），运行时刷怪不再用图元。"
                    )
            );
        }

        /// <summary>对象自身的网格是 Unity 内置图元 —— 即"这个对象本身没用上美术资产"。</summary>
        private static bool HasPrimitiveMesh(GameObject go)
        {
            MeshFilter filter = go.GetComponent<MeshFilter>();
            return filter != null
                && filter.sharedMesh != null
                && PrimitiveMeshNames.Contains(filter.sharedMesh.name);
        }

        /// <summary>
        /// 出生点的实际落点高度。地形高度贯穿到摆件这一步没有任何编译期保障 ——
        /// 漏了就只是"敌人从地里钻出来"，所以把它打进日志，至少能一眼看出是平的还是起伏的。
        /// </summary>
        private static string FormatSpawnHeights(Scene scene)
        {
            var parts = new List<string>();
            foreach (GameObject sceneRoot in scene.GetRootGameObjects())
            {
                foreach (Transform t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.StartsWith("Spawn_"))
                        parts.Add($"{t.name}={t.position.y:F2}");
                }
            }

            parts.Sort();
            return parts.Count > 0 ? string.Join(" / ", parts) : "无出生点";
        }

        private static void EnsureFolders()
        {
            Directory.CreateDirectory(Root + "/Scenes");
            Directory.CreateDirectory(Root + "/Materials");
            Directory.CreateDirectory(Root + "/Meshes");
            Directory.CreateDirectory(Root + "/ScriptableObjects/Balance");
            Directory.CreateDirectory(Root + "/ScriptableObjects/Buildables");
            Directory.CreateDirectory(Root + "/ScriptableObjects/Enemies");
        }

        private static Material CreateMaterial(string name, Color color)
        {
            string path = Root + "/Materials/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(material, path);
            }
            material.color = color;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static T CreateAsset<T>(string path) where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<T>();
                AssetDatabase.CreateAsset(asset, path);
            }
            return asset;
        }

        private static GameBalanceConfig CreateBalance()
        {
            GameBalanceConfig balance = CreateAsset<GameBalanceConfig>(Root + "/ScriptableObjects/Balance/GameBalance.asset");
            balance.expansionStart = 60f;
            balance.defenseStart = 240f;
            balance.bossWarningStart = 480f;
            balance.bossStart = 600f;
            balance.evacuationStart = 900f;
            balance.evacuationDuration = 90f;
            balance.startingResources = new ResourceWallet { metal = 120, energy = 45, crystal = 6 };
            balance.buildingCapacity = 20;
            balance.demolishRefundRatio = 0.5f;
            balance.passiveThreatPerSecond = 0.05f;
            balance.miningThreat = 0.65f;
            balance.buildThreat = 2.5f;
            balance.waveThreshold = 100f;
            balance.playerMaxHealth = 120f;
            balance.gunDamage = 18f;
            balance.gunRange = 45f;
            balance.gunFireInterval = 0.16f;
            balance.pickaxeDamage = 22f;
            balance.pickaxeRange = 3.5f;
            balance.interactRange = 4f;
            balance.allyCount = 3;
            balance.allyResourceInterval = 3.5f;
            balance.allyResourceAmount = 6;
            EditorUtility.SetDirty(balance);
            return balance;
        }

        private static BuildableConfig[] CreateBuildables()
        {
            BuildableConfig[] buildables =
            {
                Build("MachineGunTurret", "turret", "机枪塔", 1, new ResourceCost(40, 0, 0), 8, 0, 2.6f, 220f, new Vector3(1.6f, 1.8f, 1.6f), true, 24f, 10f, 0.28f, 0f, 1f, new Color(0.35f, 0.42f, 0.5f, 1f)),
                Build("CannonTurret", "cannon", "炮塔", 2, new ResourceCost(70, 0, 5), 14, 0, 4.2f, 300f, new Vector3(2.1f, 1.7f, 2.1f), true, 33f, 32f, 1.25f, 5.5f, 1f, new Color(0.44f, 0.35f, 0.28f, 1f)),
                Build("TeslaTower", "tesla", "电磁塔", 3, new ResourceCost(55, 10, 0), 12, 0, 3.6f, 220f, new Vector3(1.5f, 2.5f, 1.5f), true, 18f, 14f, 0.65f, 0f, 0.45f, new Color(0.37f, 0.31f, 0.55f, 1f)),
                Build("EnergyWall", "wall", "能量墙", 4, new ResourceCost(20, 0, 0), 2, 0, 1.6f, 460f, new Vector3(3.8f, 1.6f, 0.8f), false, 0f, 0f, 1f, 0f, 1f, new Color(0.48f, 0.55f, 0.65f, 1f)),
                Build("Radar", "radar", "雷达", 5, new ResourceCost(35, 8, 0), 6, 0, 3f, 170f, new Vector3(1.3f, 3.2f, 1.3f), false, 0f, 0f, 1f, 0f, 1f, new Color(0.3f, 0.48f, 0.55f, 1f)),
                Build("RepairStation", "repair", "维修站", 6, new ResourceCost(50, 12, 0), 10, 0, 3.6f, 200f, new Vector3(2.2f, 1.4f, 2.2f), false, 0f, 0f, 1f, 0f, 1f, new Color(0.36f, 0.52f, 0.36f, 1f)),
                Build("ShieldGenerator", "shield", "护盾发生器", 7, new ResourceCost(60, 0, 6), 16, 0, 4.2f, 240f, new Vector3(2.2f, 2.2f, 2.2f), false, 0f, 0f, 1f, 0f, 1f, new Color(0.25f, 0.45f, 0.72f, 1f)),
                Build("PowerGenerator", "gen", "发电机", 8, new ResourceCost(45, 10, 0), 0, 32, 3f, 190f, new Vector3(2f, 2f, 2f), false, 0f, 0f, 1f, 0f, 1f, new Color(0.66f, 0.58f, 0.28f, 1f))
            };

            ConfigureBuildingCapacities(buildables);
            ConfigureBuildingRoles(buildables);
            AssignBuildableIcons(buildables);
            return buildables;
        }

        /// <summary>
        /// 把美术图标灌进建筑配置。图标是建造栏（<see cref="BuildBarHud"/>）直接读的字段，
        /// 所以这里灌完，UI 那边不需要再按名字猜 —— 名字这一跳只在
        /// <see cref="BuildBarIconLibrary"/> 里发生一次。
        ///
        /// 缺图不抛异常：建造栏会退回一个代表色块。但必须留日志 ——
        /// 否则"某个建筑在栏里是灰方块"会被当成 UI 的 bug 查半天，而真相是资产没配。
        /// </summary>
        private static void AssignBuildableIcons(BuildableConfig[] buildables)
        {
            var missing = new List<string>();

            for (int i = 0; i < buildables.Length; i++)
            {
                BuildableConfig config = buildables[i];
                if (config == null)
                    continue;

                Texture2D icon = BuildBarIconLibrary.LoadIcon(config.id);
                if (icon == null)
                    missing.Add($"{config.id}（{config.displayName}）");

                config.icon = icon;
                EditorUtility.SetDirty(config);
            }

            if (missing.Count > 0)
            {
                Debug.LogWarning(
                    "[StarDefenseSceneBuilder] 以下建筑没有对应图标，建造栏将显示为色块："
                        + string.Join("、", missing.ToArray()));
            }

            // 美术按 15 种建筑交了图标，玩法侧目前只实现 8 种。把差额报出来，
            // 免得下次有人看到 7 张没用上的图，以为是导入漏了。
            Debug.Log("[StarDefenseSceneBuilder] 建造栏图标：" + BuildBarIconLibrary.DescribeUnusedIcons(buildables));
        }

        /// <summary>
        /// 装配支援建筑的职能。护盾发生器与维修站在补上本方法之前只有外观和电力消耗，
        /// 玩家花容量点建出来却没有任何效果。数值取策划案 11.2 / 11.3 的描述。
        /// </summary>
        private static void ConfigureBuildingRoles(BuildableConfig[] builds)
        {
            // 电磁塔 9.2：要求"电击 + 减速 + 小范围群体伤害"。
            // 减速由 slowMultiplier 承担，这里补上缺失的小范围群体伤害。
            builds[2].splashRadius = 3.5f;

            // 维修站 11.3：自动维修附近建筑，需要持续消耗能源
            builds[5].role = BuildableRole.RepairAura;
            builds[5].supportRadius = 14f;
            builds[5].repairPerSecond = 14f;
            builds[5].repairEnergyPerTick = 1;

            // 护盾发生器 11.2：为范围内建筑提供护盾，护盾耗尽后进入冷却
            builds[6].role = BuildableRole.ShieldEmitter;
            builds[6].supportRadius = 12f;
            builds[6].shieldAmount = 150f;
            builds[6].shieldRechargeDelay = 5f;

            for (int i = 0; i < builds.Length; i++)
            {
                if (builds[i] != null)
                    EditorUtility.SetDirty(builds[i]);
            }
        }

        /// <summary>
        /// 建筑容量占用点数，数值直接取自策划案 13 章「建筑容量」表。
        /// 目前 8 种建筑里有 7 种在表内，发电机是原型阶段补的支援建筑，按同类支援取 3 点。
        /// </summary>
        private static void ConfigureBuildingCapacities(BuildableConfig[] builds)
        {
            SetCapacity(builds[0], 2); // 机枪塔
            SetCapacity(builds[1], 4); // 炮塔
            SetCapacity(builds[2], 3); // 电磁塔
            SetCapacity(builds[3], 1); // 能量墙
            SetCapacity(builds[4], 3); // 雷达
            SetCapacity(builds[5], 4); // 维修站
            SetCapacity(builds[6], 5); // 护盾发生器
            SetCapacity(builds[7], 3); // 发电机（策划案未列，按支援建筑取 3）
        }

        private static void SetCapacity(BuildableConfig config, int cost)
        {
            if (config == null)
                return;
            config.capacityCost = cost;
            EditorUtility.SetDirty(config);
        }

        private static BuildableConfig Build(string assetName, string id, string displayName, int hotkey, ResourceCost cost, int powerCost, int powerSupply, float buildSeconds, float maxHealth, Vector3 size, bool weapon, float range, float damage, float interval, float splash, float slow, Color color)
        {
            BuildableConfig config = CreateAsset<BuildableConfig>(Root + "/ScriptableObjects/Buildables/" + assetName + ".asset");
            config.id = id;
            config.displayName = displayName;
            config.hotkey = hotkey;
            config.cost = cost;
            config.powerCost = powerCost;
            config.powerSupply = powerSupply;
            config.buildSeconds = buildSeconds;
            config.maxHealth = maxHealth;
            config.size = size;
            config.isWeapon = weapon;
            config.range = range;
            config.damagePerShot = damage;
            config.fireInterval = interval;
            config.splashRadius = splash;
            config.slowMultiplier = slow;
            config.color = color;
            EditorUtility.SetDirty(config);
            return config;
        }

        private static EnemyConfig[] CreateEnemies(out EnemyConfig boss)
        {
            EnemyConfig[] result = new[]
            {
                Enemy("Crawler", "crawler", "普通虫", 48, 3.5f, 7, 1.1f, 0.95f, false, new Color(0.55f, 0.23f, 0.16f, 1f)),
                Enemy("Bomber", "bomber", "自爆虫", 62, 4.7f, 55, 0.8f, 1.05f, true, new Color(0.77f, 0.33f, 0.16f, 1f)),
                Enemy("Burrower", "burrow", "挖掘虫", 100, 3.1f, 13, 1f, 1.15f, false, new Color(0.54f, 0.45f, 0.25f, 1f)),
                Enemy("SniperBug", "sniper", "狙击虫", 58, 2.7f, 15, 0.8f, 1f, false, new Color(0.24f, 0.42f, 0.28f, 1f)),
                Enemy("Jammer", "jammer", "干扰虫", 130, 3.3f, 6, 1f, 1.1f, false, new Color(0.42f, 0.25f, 0.48f, 1f)),
                Enemy("Thief", "thief", "搬运虫", 75, 4.9f, 4, 1.2f, 1f, false, new Color(0.48f, 0.48f, 0.18f, 1f))
            };

            ConfigureEnemyBehaviors(result);

            boss = Enemy("PlanetBeast", "boss", "行星巨兽", 12000, 2f, 42, 1f, 3.4f, false, new Color(0.3f, 0.22f, 0.28f, 1f));
            boss.isBoss = true;
            boss.attackRange = 3.5f;
            EditorUtility.SetDirty(boss);
            return result;
        }

        /// <summary>
        /// 把策划案里每种虫的独立威胁落到配置上。
        /// 数值语义随行为变化，具体含义见 EnemyConfig 上三个行为字段的 Tooltip。
        /// </summary>
        private static void ConfigureEnemyBehaviors(EnemyConfig[] builds)
        {
            // 普通虫：保持直线推进，作为压力基线
            SetBehavior(builds[0], EnemyBehaviorType.Charger);

            // 自爆虫：接触即引爆，爆炸半径沿用 explosionRadius
            SetBehavior(builds[1], EnemyBehaviorType.Bomber);

            // 挖掘虫：地下穿行，接近核心 14 米钻出，地下速度 1.7 倍
            SetBehavior(builds[2], EnemyBehaviorType.Burrower, 14f, 0f, 1.7f);

            // 狙击虫：保持 13 米距离，射程拉到 18 米，逼玩家主动出门清点
            SetBehavior(builds[3], EnemyBehaviorType.Sniper, 13f, 0f, 0f);
            builds[3].attackRange = 18f;

            // 干扰虫：每 4.5 秒瘫痪 12 米内的建筑，持续 3 秒
            SetBehavior(builds[4], EnemyBehaviorType.Jammer, 12f, 4.5f, 3f);

            // 搬运虫：预留，等"地面掉落资源"落地后再启用
            SetBehavior(builds[5], EnemyBehaviorType.Thief);

            for (int i = 0; i < builds.Length; i++)
            {
                if (builds[i] != null)
                    EditorUtility.SetDirty(builds[i]);
            }
        }

        private static void SetBehavior(EnemyConfig config, EnemyBehaviorType behavior, float range = 0f, float interval = 0f, float value = 0f)
        {
            config.behavior = behavior;
            config.behaviorRange = range;
            config.behaviorInterval = interval;
            config.behaviorValue = value;
        }

        private static EnemyConfig Enemy(string assetName, string id, string displayName, float hp, float speed, float damage, float interval, float radius, bool boom, Color color)
        {
            EnemyConfig config = CreateAsset<EnemyConfig>(Root + "/ScriptableObjects/Enemies/" + assetName + ".asset");
            config.id = id;
            config.displayName = displayName;
            config.maxHealth = hp;
            config.moveSpeed = speed;
            config.damage = damage;
            config.attackInterval = interval;
            config.attackRange = radius + 1.2f;
            config.radius = radius;
            config.explodesOnContact = boom;
            config.explosionRadius = boom ? 6f : 0f;
            config.color = color;
            EditorUtility.SetDirty(config);
            return config;
        }

        private static void CreateSun()
        {
            GameObject sun = new GameObject("Sun_DirectionalLight");
            Light light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.35f;
            light.color = new Color(1f, 0.92f, 0.78f, 1f);
            sun.transform.rotation = Quaternion.Euler(50f, -35f, 0f);

            GameObject fill = new GameObject("BlueSky_FillLight");
            Light fillLight = fill.AddComponent<Light>();
            fillLight.type = LightType.Directional;
            fillLight.intensity = 0.35f;
            fillLight.color = new Color(0.55f, 0.7f, 1f, 1f);
            fill.transform.rotation = Quaternion.Euler(25f, 135f, 0f);
        }

        /// <summary>
        /// 沙漠星球地面。高度场在 <see cref="PlanetTerrain"/>，网格由 <see cref="DesertTerrainBuilder"/> 烘。
        ///
        /// 换掉了原先的 Plane 图元（180×160 一块绝对水平板）。地面一旦有起伏，
        /// 下面所有摆放都得改成"按地形高度落" —— 见各自的 SampleHeight 调用。
        /// </summary>
        private static void CreateGround(Material sand)
        {
            DesertTerrainBuilder.CreateGround(sand);
        }

        /// <summary>把 XZ 坐标抬到地面上；<paramref name="lift"/> 是相对地面的追加高度（米）。</summary>
        private static Vector3 OnGround(float x, float z, float lift = 0f)
        {
            return new Vector3(x, PlanetTerrain.SampleHeight(x, z) + lift, z);
        }

        private static void CreateRocks(Material rock)
        {
            Random.InitState(240924);
            string[] rockVariants = { ArtPrefabLibrary.RockA, ArtPrefabLibrary.RockB };
            int fromPrefab = 0;

            for (int i = 0; i < 42; i++)
            {
                Vector2 pos = Random.insideUnitCircle * 72f;
                if (pos.magnitude < 12f)
                    continue;

                // 随机序列刻意与图元路径保持一致（每次迭代 3 + 1 次取样），
                // 这样换用真资产不会让整张地图的布局重新洗牌。
                // **不要在这里插入 Random 调用**，包括地形 —— PlanetTerrain 用的是整数哈希，不碰这条序列。
                Vector3 jitter = new Vector3(Random.Range(1.2f, 4f), Random.Range(0.4f, 2.2f), Random.Range(1.2f, 4f));
                Quaternion yaw = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

                float groundY = PlanetTerrain.SampleHeight(pos.x, pos.y);

                // 42 块石头共用两个模型，必须留尺寸抖动，否则一眼看出是复制粘贴。
                GameObject stone = ArtPrefabLibrary.Instantiate(
                    rockVariants[i % rockVariants.Length],
                    null,
                    new Vector3(pos.x, groundY, pos.y),
                    yaw);

                if (stone != null)
                {
                    stone.name = "DesertRock";
                    stone.transform.localScale = Vector3.one * (jitter.x * 0.45f);
                    ArtPrefabLibrary.DropToGround(stone, groundY);
                    fromPrefab++;
                    continue;
                }

                stone = GameObject.CreatePrimitive(PrimitiveType.Cube);
                stone.name = "DesertRock";
                // 图元原点在几何中心，抬半个高度才坐在地面上（原先是写死 0.25，与 jitter 无关，
                // 高度抖动一旦大于 0.5 就有一半埋进沙里）。
                stone.transform.position = new Vector3(pos.x, groundY + jitter.y * 0.5f, pos.y);
                stone.transform.localScale = jitter;
                stone.transform.rotation = yaw;
                stone.GetComponent<Renderer>().sharedMaterial = rock;
            }

            Debug.Log($"[StarDefenseSceneBuilder] 岩石：{fromPrefab}/42 使用美术 Prefab，其余回退图元。");
        }

        /// <summary>
        /// 刷怪点的默认节奏。四个点共用同一套参数，各自独立计时。
        ///
        /// 只想让敌人从**一个方向**来时，把其余三个点上的 <c>EnemySpawnPoint.active</c>
        /// 勾掉即可 —— 在 Inspector 里改，不用重建场景也不用改代码。
        ///
        /// 数值取法：60 秒首批（需求原文「游戏开始 1 分钟后」），此后每点 90 秒一批。
        /// 首批每点 1 只 ⇒ 开局同时出现 4 只，之后每批递增、封顶 4 只 ⇒ 满编时每 90 秒
        /// 从四个方向各来 4 只。这是在"开局压力足够轻"和"后期不至于被淹没"之间取的中间值。
        /// </summary>
        private const float SpawnStartDelay = 60f;
        private const float SpawnInterval = 90f;
        private const int SpawnFirstBatch = 1;
        private const int SpawnBatchGrowth = 1;
        private const int SpawnMaxBatch = 4;
        private const float SpawnRadius = 5f;
        private const float SpawnStagger = 0.35f;

        private static Transform[] CreateSpawnPoints(Transform root)
        {
            Vector3[] positions =
            {
                new Vector3(0f, 0f, 72f),
                new Vector3(78f, 0f, 8f),
                new Vector3(-78f, 0f, -5f),
                new Vector3(12f, 0f, -72f)
            };

            // 方位名与 positions 一一对应，只用于 HUD 提示（"正北：2 只敌人进入战场"）。
            // 取不到时退化成"未知方向"而不是让索引越界 —— 场景重建失败比一句提示难看得多。
            string[] directions = { "正北", "正东", "正西", "正南" };

            Transform[] result = new Transform[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                GameObject go = new GameObject("Spawn_" + (i + 1));
                go.transform.SetParent(root, false);
                // 出生点自己带地形高度，刷怪时就不必再算一遍 —— 它是"敌人脚下的地面"这一事实的载体。
                go.transform.position = OnGround(positions[i].x, positions[i].z);

                // 节奏参数落在这里而不是留给人手填：场景是纯生成物，手填的改动下次重建就没了。
                // 出口（spawnerSource）与虫种要在 StarDefenseGame 建好之后回填，见 WireSpawnPoints。
                EnemySpawnPoint point = go.AddComponent<EnemySpawnPoint>();
                point.displayName = i < directions.Length ? directions[i] : "未知方向";
                point.startDelay = SpawnStartDelay;
                point.interval = SpawnInterval;
                point.firstBatchCount = SpawnFirstBatch;
                point.countGrowthPerBatch = SpawnBatchGrowth;
                point.maxBatchCount = SpawnMaxBatch;
                point.spawnRadius = SpawnRadius;
                point.stagger = SpawnStagger;

                result[i] = go.transform;
            }
            return result;
        }

        /// <summary>
        /// 给刷怪点接线：出口与虫种。
        ///
        /// 必须放在 <see cref="StarDefenseGame"/> 建好之后 —— CreateSpawnPoints 跑在它前面，
        /// 那时候连 game 对象都还没有。建造虚影的接线是同一个原因，见 BuildFirstVersionScene 里的注释。
        /// </summary>
        private static void WireSpawnPoints(Transform[] spawns, StarDefenseGame game, EnemyConfig[] enemies)
        {
            if (spawns == null || spawns.Length == 0 || game == null || enemies == null || enemies.Length == 0)
                return;

            int wired = 0;
            for (int i = 0; i < spawns.Length; i++)
            {
                if (spawns[i] == null)
                    continue;

                EnemySpawnPoint point = spawns[i].GetComponent<EnemySpawnPoint>();
                if (point == null)
                    continue;

                point.spawnerSource = game;
                point.enemyTypes = enemies;
                wired++;
            }

            if (wired == 0)
            {
                Debug.LogWarning(
                    "[StarDefenseSceneBuilder] 没有任何刷怪点被接线，场上不会再有敌人主动到来。"
                );
                return;
            }

            // 刷怪点与 game 自己的自动波是两套并行的节奏，同时开着压力凭空翻倍，
            // 而"刷怪节奏可配置"这个前提也就没了。打开这个开关后 game 不再自动开波，
            // 节奏完全由刷怪点决定（威胁值仍照常累积，HUD 上还看得到压力读数）。
            game.suppressAutoWaves = true;

            Debug.Log(
                $"[StarDefenseSceneBuilder] 刷怪点：{wired} 个已接线，"
                    + $"{SpawnStartDelay:F0} 秒后开始，之后每 {SpawnInterval:F0} 秒一批。"
            );
        }

        /// <summary>程序化占位件的配色，与三种资源的语义色一致（也用于图元回退路径）。</summary>
        private static readonly Color MetalNodeColor = new Color(0.55f, 0.58f, 0.62f, 1f);
        private static readonly Color EnergyNodeColor = new Color(0.20f, 0.80f, 1.00f, 1f);
        private static readonly Color CrystalNodeColor = new Color(0.70f, 0.35f, 1.00f, 1f);

        /// <summary>
        /// 资源点布局。三种资源的平面坐标在这里集中列出，落点高度一律交给 <see cref="PlanetTerrain"/>。
        ///
        /// 从 6 个扩到 16 个，是为了让"外出采集"这件事真的发生。只有 6 个矿点时，
        /// 玩家站在基地平台边缘就能把全图资源砍完 —— 狙击虫的点名、自爆虫的贴脸、
        /// 被击倒后 5 秒复起这一整套风险设计，压根没有触发的场合。
        ///
        /// 半径分三环：内环 20~30 m 维持开局节奏（单人跑一趟 15 秒内），
        /// 中环 30~45 m 是日常补给，外环 45~65 m 只留给愿意承担风险的时段。
        /// 方位刻意铺开，避免出现"某个方向一整片没有矿"的空白扇区。
        /// </summary>
        private static void CreateResources(Transform root)
        {
            // ── 金属 ×6：消耗量最大（建造主力成本），内环给得多一点
            CreateNode(root, "MetalNode", ResourceType.Metal, new Vector2(-18f, 18f), 380, 10, ArtPrefabLibrary.SupplyCrate, MetalNodeColor);
            CreateNode(root, "MetalNode", ResourceType.Metal, new Vector2(24f, 23f), 360, 10, ArtPrefabLibrary.SupplyCrate, MetalNodeColor);
            CreateNode(root, "MetalNode", ResourceType.Metal, new Vector2(-30f, -14f), 360, 10, ArtPrefabLibrary.SupplyCrate, MetalNodeColor);
            CreateNode(root, "MetalNode", ResourceType.Metal, new Vector2(36f, -8f), 340, 10, ArtPrefabLibrary.SupplyCrate, MetalNodeColor);
            CreateNode(root, "MetalNode", ResourceType.Metal, new Vector2(-8f, 40f), 320, 10, ArtPrefabLibrary.SupplyCrate, MetalNodeColor);
            CreateNode(root, "MetalNode", ResourceType.Metal, new Vector2(12f, -42f), 320, 10, ArtPrefabLibrary.SupplyCrate, MetalNodeColor);

            // ── 能源 ×6：中环为主，维修站与发电机持续吃它
            CreateNode(root, "EnergyGeyser", ResourceType.Energy, new Vector2(28f, -20f), 280, 8, null, EnergyNodeColor);
            CreateNode(root, "EnergyGeyser", ResourceType.Energy, new Vector2(-35f, -26f), 280, 8, null, EnergyNodeColor);
            CreateNode(root, "EnergyGeyser", ResourceType.Energy, new Vector2(46f, 18f), 260, 8, null, EnergyNodeColor);
            CreateNode(root, "EnergyGeyser", ResourceType.Energy, new Vector2(-46f, 30f), 260, 8, null, EnergyNodeColor);
            CreateNode(root, "EnergyGeyser", ResourceType.Energy, new Vector2(6f, 52f), 260, 8, null, EnergyNodeColor);
            CreateNode(root, "EnergyGeyser", ResourceType.Energy, new Vector2(-14f, -50f), 260, 8, null, EnergyNodeColor);

            // ── 晶体 ×4：只在最外环。炮塔与护盾发生器都吃晶体，
            //    把它放在最远处，等于把"想升火力就得往虫群来的方向走"写进了地图。
            CreateNode(root, "CrystalOutcrop", ResourceType.Crystal, new Vector2(42f, 18f), 150, 4, ArtPrefabLibrary.Crystal, CrystalNodeColor);
            CreateNode(root, "CrystalOutcrop", ResourceType.Crystal, new Vector2(-48f, 16f), 150, 4, ArtPrefabLibrary.Crystal, CrystalNodeColor);
            CreateNode(root, "CrystalOutcrop", ResourceType.Crystal, new Vector2(56f, -32f), 150, 4, ArtPrefabLibrary.Crystal, CrystalNodeColor);
            CreateNode(root, "CrystalOutcrop", ResourceType.Crystal, new Vector2(-58f, -24f), 150, 4, ArtPrefabLibrary.Crystal, CrystalNodeColor);
        }

        /// <param name="planar">
        /// 平面坐标。高度**刻意不在这里给** —— 矿点位置是策划给的平面布局，地形高度是另一回事，
        /// 混进同一个 Vector3 里，改地形参数时就很容易漏掉这一处（历史上就漏过一次）。
        /// </param>
        /// <param name="prefabName">美术资产名。为 null 或资产缺失时走程序化占位件。</param>
        /// <param name="fallbackColor">占位件的配色，只在没走美术资产时用到。</param>
        private static void CreateNode(
            Transform root,
            string name,
            ResourceType type,
            Vector2 planar,
            int amount,
            int perHit,
            string prefabName,
            Color fallbackColor)
        {
            float groundY = PlanetTerrain.SampleHeight(planar.x, planar.y);
            string nodeName = name + "_" + type;

            GameObject node = ArtPrefabLibrary.Instantiate(
                prefabName, root, new Vector3(planar.x, groundY, planar.y), Quaternion.identity);

            if (node != null)
            {
                // 资源矿要能被采矿射线命中，靠的是 Prefab 自带的 MeshCollider；
                // 有资产时按 authored 尺寸摆放，不再套用图元那套 2.2×1.8 的缩放。
                node.name = nodeName;
                ArtPrefabLibrary.DropToGround(node, groundY);
            }
            else
            {
                // 没有美术资产：用程序化占位件而不是裸图元。
                // 它和美术资产遵守同一套"原点在底面中心"的契约，所以落点直接就是地面高度。
                node = ProcPrototypes.CreateResourceNode(type, fallbackColor, root);
                node.name = nodeName;
                node.transform.position = new Vector3(planar.x, groundY, planar.y);
            }

            // 采矿靠 Physics.Raycast 命中，没有碰撞体的资源点等于"砍不动"，而且不报任何错。
            // 美术 Prefab 的碰撞体在子网格上（这里能直接找到），图元回退路径自带一个，
            // 两条都覆盖不到时才补一个 —— 补了要吭声，不然就成了隐形故障。
            if (!ArtPrefabLibrary.EnsureCollider(node))
            {
                Debug.LogWarning(
                    $"[StarDefenseSceneBuilder] {node.name} 没有可用碰撞体，采矿射线将无法命中它。"
                );
            }

            ResourceNode resource = node.AddComponent<ResourceNode>();
            if (resource == null)
            {
                // AddComponent 失败时 Unity 会自己打一条错误，但它返回 null，
                // 紧接着赋值就是空引用，栈里看不出真正原因。这里显式收口，让日志直接指向病根。
                Debug.LogError($"[StarDefenseSceneBuilder] {node.name} 无法挂上 ResourceNode，该资源点将不可采集。");
                return;
            }

            resource.resourceType = type;
            resource.amountRemaining = amount;
            resource.amountPerHit = perHit;
        }

        /// <summary>
        /// 复活点离基地核心的水平距离（米）。
        ///
        /// 3 m 是量出来的、不是随手取的：核心 <c>BLD_Core_A</c> 宽 2.68 m（半宽 1.34），
        /// 玩家 CharacterController 半径约 0.4 —— 两者相加 1.74，留近一倍余量才不会被挤进
        /// 核心的 MeshCollider 里。同时它仍在基地平台内（<c>FlatRadius</c> 是 14 m，
        /// 平台内部是**绝对水平**的），所以"复活在基地"在地形上是确定的，
        /// 不取决于采样落在哪块沙丘上。
        /// </summary>
        private const float PlayerRespawnDistance = 3f;

        /// <summary>
        /// 玩家复活点锚点。基地平台上的一个显式物体，位置就是"复活在基地的哪里"这件事的答案。
        ///
        /// 之所以做成场景里的物体而不是 <see cref="PlayerDownedState"/> 里的常量：
        /// 它属于"必须和基地对齐"的量 —— 基地挪了、平台半径改了，在 Inspector 里拖一下即可，
        /// 不必改代码再重建一次场景。
        ///
        /// 挂在 <c>Buildings</c> 下而不是挂到核心节点下：核心根节点带着 Blender 的 -90°X 校正，
        /// 一旦成为它的子物体，锚点的 local 坐标就进入"Z 是高度"的坐标系，看起来像坏了。
        /// 位置取 <see cref="OnGround"/> 采样，与核心用的是同一个地面基准。
        /// </summary>
        private static Transform CreatePlayerRespawnPoint(Transform root)
        {
            GameObject anchor = new GameObject("Respawn_Player_Base");
            anchor.transform.SetParent(root, true);
            anchor.transform.position = OnGround(0f, -PlayerRespawnDistance);
            return anchor.transform;
        }

        /// <summary>
        /// 基地核心。原来是一个 4.5 m 粗、4 m 高的图元圆柱（蓝色），
        /// 现在是已交付的 <c>BLD_Core_A</c> —— 一艘着陆在八边形平台上的紧凑运输舰。
        ///
        /// 换模型带来两处必须一起改的量，两处都不会报错、只会看着不对：
        ///   · **落点**。图元圆柱的原点在几何中心，所以要抬 2 m；资产的契约是原点在底面，
        ///     必须传 0 —— 沿用旧的 2 m 会让整艘船悬在半空。
        ///   · **尺寸**。图元的 localScale 是手工填的，资产是按米 authored 的（2.68 宽 ×
        ///     2.78 高）。宁可让它比图元小，也不要再套一次缩放。
        /// </summary>
        private static Transform CreateCore(Transform root)
        {
            // 基地平台半径内是绝对水平的，落点取地形采样而不是写死 0 —— 平台半径是可调的，
            // 写死就意味着"哪天把平台缩小一点，核心就埋进沙里"，而且不会有任何报错。
            GameObject core = ArtPrefabLibrary.Instantiate(
                ArtPrefabLibrary.Core,
                root,
                OnGround(0f, 0f),
                Quaternion.identity
            );

            if (core != null)
            {
                // 资产原点在底面，落点即地面，不需要 DropToGround 也不需要缩放。
                core.name = "BaseCore_ArkReactor";
            }
            else
            {
                // 走到这里说明 PFB_Core_A 没生成出来 —— 那是一次真正的资产事故，
                // 而不是"美术还没做完"。给一条显式警告，别让场景里的圆柱被当成设计如此。
                Debug.LogWarning(
                    $"[StarDefenseSceneBuilder] 找不到 {ArtPrefabLibrary.Core}，基地核心退回图元圆柱。"
                        + "请检查编辑器侧的资产构建流程（见 Logs/StarDefensePipeline.txt）。"
                );

                core = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                core.name = "BaseCore_ArkReactor";
                core.transform.SetParent(root, true);
                core.transform.position = OnGround(0f, 0f, 2f);
                core.transform.localScale = new Vector3(4.5f, 2f, 4.5f);
                core.GetComponent<Renderer>().sharedMaterial = CreateMaterial("Mat_Core", new Color(0.12f, 0.42f, 0.9f, 1f));
            }

            core.AddComponent<Health>().Configure(1800f);
            return core.transform;
        }

        private static Transform CreatePlayer(Material material, out PlayerToolController tools, out Health health, out BuildPreview buildPreview)
        {
            // 出生点 (0, -10) 在基地平台半径 14 内，地形高度是 0；仍然走采样，
            // 免得将来把出生点挪出平台后忘了这一处。
            Vector3 spawn = OnGround(0f, -10f);
            float groundY = spawn.y;

            GameObject player = ArtPrefabLibrary.Instantiate(
                ArtPrefabLibrary.Player, null, spawn, Quaternion.identity);
            bool hasArt = player != null;

            if (hasArt)
            {
                // 美术 Prefab 上自带 Animator / Avatar / 手持武器与 PlayerToolVisuals。
                // 它的碰撞体在导出时被清掉了（持械射线不该被自己吃掉），本体碰撞改由
                // 下面新建的 CharacterController 承担。这里再清一遍是防御性的：将来美术
                // 若给角色补回 MeshCollider，也不会和 CharacterController 互相打架。
                player.name = "Player_FirstPerson";
                ArtPrefabLibrary.StripColliders(player);
                ArtPrefabLibrary.DropToGround(player, groundY);
            }
            else
            {
                player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                player.name = "Player_FirstPerson";
                player.transform.position = spawn + Vector3.up;
                player.GetComponent<Renderer>().sharedMaterial = material;
                Object.DestroyImmediate(player.GetComponent<CapsuleCollider>());
            }

            CharacterController controller = player.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            health = player.AddComponent<Health>();
            health.Configure(120f);

            GameObject cameraObject = new GameObject("PlayerCamera");
            cameraObject.transform.SetParent(player.transform, false);
            cameraObject.transform.localPosition = ResolveEyeLocal(player.transform, out string eyeSource);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = PlayerFieldOfView;
            camera.nearClipPlane = PlayerNearClipPlane;
            cameraObject.AddComponent<AudioListener>();

            // 眼位是本场景里最容易"看着差不多其实错了"的数字：错了不会报错，
            // 只会表现为低头看见自己的头顶。打一行日志，让它在 Console 里可查。
            Debug.Log($"[StarDefenseSceneBuilder] 玩家眼位：{eyeSource}；近裁剪面 {PlayerNearClipPlane:F2} m");

            // 手里拿的东西：有美术资产时直接用 Prefab 自带的枪 —— 它挂在 Socket_RightHandTool
            // 下，由 PlayerToolVisuals 负责枪/镐切换；只有走图元回退时才叠一个方块当第一人称占位。
            Transform handSocket = hasArt ? FindDeep(player.transform, HandSocketName) : null;
            GameObject placeholderTool = null;
            if (handSocket == null)
            {
                placeholderTool = GameObject.CreatePrimitive(PrimitiveType.Cube);
                placeholderTool.name = "ViewTool";
                placeholderTool.transform.SetParent(cameraObject.transform, false);
                placeholderTool.transform.localPosition = new Vector3(0.36f, -0.28f, 0.72f);
                placeholderTool.transform.localRotation = Quaternion.Euler(-8f, -12f, 0f);
                placeholderTool.transform.localScale = new Vector3(0.18f, 0.18f, 0.75f);
                Object.DestroyImmediate(placeholderTool.GetComponent<Collider>());
                placeholderTool.GetComponent<Renderer>().sharedMaterial = CreateMaterial("Mat_ViewTool", new Color(0.12f, 0.12f, 0.12f, 1f));
            }

            FirstPersonController movement = player.AddComponent<FirstPersonController>();
            movement.playerCamera = camera;
            // 显式写进序列化字段，Inspector 里才看得见实际生效的数值；
            // 代码侧仍把 nearClipPlane<=0 当"未设置"处理，兼容更早生成的老场景。
            movement.nearClipPlane = PlayerNearClipPlane;
            movement.fallbackEyeHeight = FallbackEyeLocal.y;
            tools = player.AddComponent<PlayerToolController>();
            tools.playerCamera = camera;

            // 有美术资产时 weaponPivot 必须留空。它驱动的是程序化第一人称枪械摆动，
            // 而手臂此刻已由 Animator 接管 —— 两边同时写同一个骨骼会让枪来回打架。
            // 这条约定出自 PlayerToolVisuals 的类注释，是美术侧交接时明确要求的。
            tools.weaponPivot = hasArt ? null : placeholderTool.transform;

            // 建造虚影：创造模式下显示当前建筑的蓝色半透明预览。
            // 这里只接相机与控制器（都在本方法内可得）；game 引用要等 StarDefenseGame 创建后由调用方补线。
            buildPreview = player.AddComponent<BuildPreview>();
            buildPreview.playerCamera = camera;
            buildPreview.tools = tools;
            tools.preview = buildPreview;

            // 第一人称自身剔除。修的是"低头看见自己头顶/护目镜"的穿模：
            // 眼位与近裁剪面都已经是对的，剩下的唯一原因是角色自己那一整块蒙皮网格
            // 也在被这台相机渲染。详见组件注释。
            //
            // 挂在**玩家实例**上而不是 Prefab 里 —— 队友是同一个 Prefab 的另一个实例，
            // 它们需要照常渲染自己的身体。
            FirstPersonBodyCuller bodyCuller = player.AddComponent<FirstPersonBodyCuller>();
            bodyCuller.playerCamera = camera;

            // Prefab 上的 PlayerToolVisuals 只认识自己的枪和镐，不认识玩法控制器。
            // 把两边接起来，第一人称切换工具时才会真的换手上的模型。
            PlayerToolVisuals visuals = player.GetComponent<PlayerToolVisuals>();
            if (visuals != null)
                visuals.tools = tools;

            PlayerDownedState downed = player.AddComponent<PlayerDownedState>();
            downed.movement = movement;
            downed.tools = tools;
            downed.playerCamera = camera;

            return player.transform;
        }

        /// <summary>
        /// 建造栏（创造模式下屏幕底部那条）。
        ///
        /// 界面本身走 IMGUI，所以这里**只需要一个挂组件的空对象** ——
        /// 没有 Canvas、没有 Image、没有 EventSystem、没有 CanvasScaler。
        /// 这是刻意选的：整条 UI 都是代码产物，就不存在"场景重建后 UI 引用断掉"
        /// "精灵在序列化里变成 None"这类既不报错又看不出原因的故障。
        /// 完整理由见 <see cref="BuildBarHud"/> 的类注释。
        ///
        /// 这里唯一的实体工作是把美术切图引用灌进去。缺哪张就报哪张 ——
        /// 缺一张切图的失败形态是"那块安静地不画"（GUIStyle 背景为 null 时什么都不画），
        /// 光看画面只能说"这里好像少了点什么"，说不清是漏画了还是本来就该空着。
        /// </summary>
        private static BuildBarHud CreateBuildBar(StarDefenseGame game, PlayerToolController tools)
        {
            // 对象名用 "BuildBar" 而不是 "BuildBarUI"：后者是常量类的类名，
            // 场景里出现一个同名对象会让"这个名字指的是哪个"变成每次都要想一下的问题。
            GameObject root = new GameObject("BuildBar");
            BuildBarHud bar = root.AddComponent<BuildBarHud>();
            bar.game = game;
            bar.tools = tools;

            bar.panel = BuildBarIconLibrary.LoadSlice(BuildBarUI.PanelName);
            bar.panelAccent = BuildBarIconLibrary.LoadSlice(BuildBarUI.PanelAccentName);
            bar.slotNormal = BuildBarIconLibrary.LoadSlice(BuildBarUI.SlotName);
            bar.slotHover = BuildBarIconLibrary.LoadSlice(BuildBarUI.SlotHoverName);
            bar.slotSelected = BuildBarIconLibrary.LoadSlice(BuildBarUI.SlotSelectedName);
            bar.slotLocked = BuildBarIconLibrary.LoadSlice(BuildBarUI.SlotLockedName);

            var missing = new List<string>();
            foreach (string slice in BuildBarUI.RequiredSlices)
            {
                if (BuildBarIconLibrary.LoadSlice(slice) == null)
                    missing.Add(slice);
            }

            if (missing.Count > 0)
            {
                Debug.LogWarning(
                    "[StarDefenseSceneBuilder] 建造栏切图缺失，对应部件将不绘制："
                        + string.Join("、", missing.ToArray())
                        + "　（删掉 Assets/StarDefense/Art/.build_bar_ui_imported 后重开工程可重新导入）");
            }

            // 美术交了 10 张切图，本栏只用 7 张。把没用的连同原因报出来，
            // 免得下次有人看到那几张图，以为是导入漏了或者哪里没接上。
            Debug.Log("[StarDefenseSceneBuilder] 建造栏未使用的切图：\n  · "
                      + string.Join("\n  · ", BuildBarHud.UnusedSlices));

            return bar;
        }

        private static void CreateAllies(Material material, out AllyAgent[] allies)
        {
            allies = new AllyAgent[3];
            // 只给平面坐标，高度一律按地形采样。三名队友都在基地平台半径内（r≈6~8），
            // 但依然不走写死的 0：他们会在半径 18 m 内游走，走到平台外就该踩在坡上。
            Vector2[] positions = { new Vector2(-4f, -6f), new Vector2(4f, -7f), new Vector2(6f, 2f) };
            string[] roles = { "采集员", "工程师", "火力手" };
            int fromPrefab = 0;

            for (int i = 0; i < allies.Length; i++)
            {
                float x = positions[i].x;
                float z = positions[i].y;
                float groundY = PlanetTerrain.SampleHeight(x, z);

                // 队友直接复用玩家模型与动画控制器 —— 同是宇航员，共用一套资产。
                // 碰撞体同样清掉：队友不该挡住玩家的采矿与射击射线。
                GameObject ally = ArtPrefabLibrary.Instantiate(
                    ArtPrefabLibrary.Player, null, new Vector3(x, groundY, z), Quaternion.identity);

                // 逐帧贴地时的抬升：资产原点在脚底，图元胶囊原点在几何中心。
                float groundOffset;

                if (ally != null)
                {
                    ally.name = "AI_Ally_" + roles[i];
                    ArtPrefabLibrary.StripColliders(ally);
                    ArtPrefabLibrary.DropToGround(ally, groundY);
                    fromPrefab++;
                    groundOffset = 0f;
                }
                else
                {
                    ally = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    ally.name = "AI_Ally_" + roles[i];
                    ally.transform.position = new Vector3(x, groundY + 1f, z);
                    ally.GetComponent<Renderer>().sharedMaterial = material;
                    groundOffset = 1f;
                }

                allies[i] = ally.AddComponent<AllyAgent>();
                allies[i].roleName = roles[i];
                allies[i].groundOffset = groundOffset;
            }

            Debug.Log($"[StarDefenseSceneBuilder] 队友：{fromPrefab}/3 使用美术 Prefab，其余回退图元。");
        }

        private static void CreateStartingDefenses(Transform root, StarDefenseGame game, BuildableConfig[] buildables)
        {
            // 建筑原点在底面中心，落点就是地面高度 —— 与玩家用建造射线摆出来的那套完全一致
            // （那条路径的 y 来自 RaycastHit.point，天然贴地）。三座都在平台半径内。
            Buildable turretA = game.CreateBuilding(buildables[0], OnGround(-8f, 5f), true);
            Buildable turretB = game.CreateBuilding(buildables[0], OnGround(8f, 5f), true);
            Buildable generator = game.CreateBuilding(buildables[7], OnGround(0f, 8f), true);
            turretA.transform.SetParent(root, true);
            turretB.transform.SetParent(root, true);
            generator.transform.SetParent(root, true);
        }
    }
}
