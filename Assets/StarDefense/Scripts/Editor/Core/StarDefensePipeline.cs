using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// 把「生成美术资产 → 绑到配置 → 重建场景」串成一条链。
    ///
    /// 为什么需要它：这几步有严格的先后依赖 —— 场景构建器要拿到 Prefab 才能摆真模型，
    /// 而 Prefab 得先由各自的 Builder 生成、再绑到 ScriptableObject 上。分几次手动点，
    /// 顺序一错就会静默退回图元，表现是"场景里还是一堆胶囊体和方块"，且不报任何错。
    /// 三套资产（建筑与环境 / 角色与装备 / 敌人与 Boss）互相独立，可各自跳过。
    ///
    /// 首次打开工程后自动跑一次。**标记里带构建器版本**（见 <see cref="BuilderRevision"/>）：
    /// 版本一变就意味着"场景这份产物已经和生成它的代码对不上了"，下次打开工程会自动重跑。
    /// 这样就不必靠谁记得手工删标记 —— 历史上"改了构建器、标记还在、场景纹丝不动"
    /// 正是这条链最难查的故障形态。
    ///
    /// 需要立即重跑：点菜单，或删掉 Assets/StarDefense/.art_pipeline_done。
    /// </summary>
    [InitializeOnLoad]
    public static class StarDefensePipeline
    {
        private const string MarkerPath = "Assets/StarDefense/.art_pipeline_done";
        private const string LogPath = "Logs/StarDefensePipeline.txt";
        private const string ScenePath = "Assets/StarDefense/Scenes/PlanetDefense_FirstVersion.unity";

        /// <summary>
        /// 构建器版本。**改了资产/场景构建逻辑（摆放规则、地形、材质、Prefab 结构…）就改这里。**
        ///
        /// 场景是纯生成物，判断"是否已是最新"就必须把生成器的版本算进去，
        /// 只看"标记存在 + 场景存在"会在构建器改动后给出错误的肯定答案。
        /// 刻意是手写常量而不是源码哈希：哈希会让"改一句注释"也触发整条重建，代价太大。
        ///
        /// 版本历史：
        ///   · <c>2026-09-30-build-bar</c> —— 建造栏接入美术切图（新增 3b/5 步）。
        ///   · <c>2026-09-30-pose-fix</c> —— **修复资产根节点朝向被抹掉**：走
        ///     <c>SetPositionAndRotation(pos, identity)</c> 摆放会把美术 Prefab 根节点自带的
        ///     -90°X（Blender Z-up → Y-up）校正抹掉，13 个 Z-up 批次资产（建筑 / Core / Rock /
        ///     Crystal / 道具 / 武器）因此**横躺**且落点偏移。场景是纯生成物，光改代码不改场景
        ///     等于没修 —— **这个版本戳就是为了逼出一次重建**，别把它省掉。
        ///   · <c>2026-09-30-gun-raised</c> —— **举枪姿态改造**：新增 `HoldingGun` 混合参数与
        ///     `GunIdle` / `GunWalk` / `GunRun` 三个剪辑，Idle / Walk / Run 的 motion 改为一棵
        ///     1D 混合树（状态数与转移数一个没变）。同一批还加上了资产批次的**源戳**判据 ——
        ///     见 <see cref="ComputeSourceStamp"/>：没有它，新导出的玩家 FBX 会被
        ///     "标记 + 产物都在"静默跳过，三个新剪辑压根进不了 Unity。
        ///   · <c>2026-09-30-enemy-spawn</c> —— 敌人刷怪点升级为 <c>EnemySpawnPoint</c> 组件
        ///     （四点可配置、与威胁值自动波互斥），敌人目标顺序改为"基地 → 玩家"。
        ///   · <c>2026-09-30-respawn-anchor</c> —— 复活点从代码里的 <c>core + (6,0,6)</c> 偏移
        ///     改成场景里可拖动的锚点 <c>Respawn_Player_Base</c>（挂在基地平台上，接给
        ///     <c>PlayerDownedState.respawnPoint</c>）。旧偏移保留为老场景兜底。
        ///   · <c>2026-09-30-build-settings-guid</c> —— **Build Settings 写早了一拍**。
        ///     原先在 <c>SaveScene</c> 之后、<c>AssetDatabase.Refresh()</c> 之前写
        ///     <c>EditorBuildSettings.scenes</c>：那一刻"路径 → GUID"映射里还留着**已被删掉的
        ///     旧场景**的 GUID，写进去的值与上一次相同 ⇒ ProjectSettings 不标脏 ⇒ 磁盘上就此
        ///     长期保存着一个失效 GUID（实测与场景真实 GUID 差了六天）。改为 Refresh 之后再写。
        ///   · <c>2026-09-30-construction-proxy</c> —— 施工状态不再把原碰撞体直接改成 Trigger：
        ///     凹面 MeshCollider 不支持 Trigger；旧逻辑还把条件写反，曾把初始发电机的 BoxCollider
        ///     序列化成 <c>m_IsTrigger: 1</c>。改为"关闭原实体 + 独立 BoxCollider 射线代理"并重建场景。
        /// </summary>
        private const string BuilderRevision = "2026-09-30-construction-proxy";


        private const string StructureMarker = "Assets/StarDefense/Art/.structure_assets_built";
        private const string PlayerMarker = "Assets/StarDefense/Art/.player_assets_built";

        /// <summary>代表性产物。用来分辨"真的建过"和"标记还在但资产被删了"。</summary>
        private const string StructureProbe = "Assets/StarDefense/Art/Prefabs/PFB_MachineGunTurret_A.prefab";
        private const string PlayerProbe = "Assets/StarDefense/Art/Prefabs/PFB_Player_Astronaut_A.prefab";

        /// <summary>
        /// 三个批次各自的「源戳」文件，内容是该批源模型的指纹（见 <see cref="ComputeSourceStamp"/>）。
        ///
        /// 与标记分开放：标记回答"建过没有"，源戳回答"产物是不是用**现在这份**源建的"。
        /// 两件事，两个文件，不混在同一个文件里互相污染。
        /// </summary>
        private const string StructureStamp = "Assets/StarDefense/Art/.structure_assets_sources";
        private const string PlayerStamp = "Assets/StarDefense/Art/.player_assets_sources";
        private const string EnemyStamp = "Assets/StarDefense/Art/.enemy_assets_sources";

        private const string ModelsRoot = "Assets/StarDefense/Art/Models";

        /// <summary>
        /// 批次 → 源目录。**按目录划分，而不是从各 Builder 取 id 列表反推路径**：
        /// 目录是文件的真实归属，新加一个 FBX 不需要改任何代码就会被源戳看见；
        /// 若从 id 列表反推，新资产漏登记时源戳也一起漏 —— 那正是要防的那种静默。
        ///
        /// 刻意的分工：这三行就是"哪些模型属于哪一批"的唯一表述，改资产归属只改这里。
        /// </summary>
        private static readonly string[] StructureSourceDirs =
        {
            ModelsRoot + "/Buildings",
            ModelsRoot + "/Environment",
        };

        private static readonly string[] PlayerSourceDirs =
        {
            ModelsRoot + "/Characters",
            ModelsRoot + "/Equipment",
        };

        private static readonly string[] EnemySourceDirs = { ModelsRoot + "/Enemies" };

        /// <summary>美术侧两个 AutoBuild 各自的标记，跑完本流程后补上以免它们再跑一遍。</summary>
        private static readonly string[] LegacyMarkers = { StructureMarker, PlayerMarker };

        static StarDefensePipeline()
        {
            EditorApplication.delayCall += TryRunOnce;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// 进入播放模式会触发一次域重载，[InitializeOnLoad] 的静态构造因此会跟着再跑一遍。
        /// 但重建场景用到的 EditorSceneManager.NewScene 在运行期被 Unity 禁止
        /// （抛 "This cannot be used during play mode"），所以播放期间整条链跳过，
        /// 等退出播放后在这里补跑一次 —— 否则就会出现"标记被清掉 → 进播放 → 跑到一半炸"
        /// 这种既没有场景、也没留下可用标记的中间状态。
        /// </summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += TryRunOnce;
        }

        [MenuItem("Star Defense/★ 全流程：生成资产 → 绑定 → 重建场景", false, 0)]
        public static void RunFromMenu()
        {
            Run();
        }

        /// <summary>
        /// 供自动钩子（<see cref="StarDefenseAutoBuildOnce"/>）调用的入口。
        /// 场景生成必须走这条链，因为它同时保证了"先绑 Prefab 再建场景"的顺序。
        /// </summary>
        internal static void RunPipeline()
        {
            // 已经有成功产物就不再重复：本类与 StarDefenseAutoBuildOnce 都挂在 delayCall 上，
            // 若各自跑一遍，整条链（含耗时的资产重建）会被做两次。
            if (MarkerIsCurrent())
                return;

            Run();
        }

        /// <summary>
        /// 「标记是当前的」= 场景存在 + 标记里版本一致 + 标记状态是 completed。
        ///
        /// 后两个条件都不能省：
        ///   · 场景是构建器的产物，构建器改了而标记没变，手上这份场景就已经过期，
        ///     只判"文件都在"会把它当成最新的静默跳过；
        ///   · 标记是构建开始时先写成 started 占位的（防域重载死循环），
        ///     所以必须区分 started / completed —— 否则中途被打断的那次
        ///     会因为"版本对得上、场景文件也在（还是旧的那份）"而被判成完成。
        /// </summary>
        private static bool MarkerIsCurrent()
        {
            if (!File.Exists(MarkerPath) || !File.Exists(ScenePath))
                return false;

            try
            {
                string[] lines = File.ReadAllLines(MarkerPath);
                bool revisionMatches = lines.Length > 0 && lines[0].Trim() == "revision: " + BuilderRevision;
                bool completed = lines.Length > 1 && lines[1].StartsWith("completed", StringComparison.Ordinal);

                if (revisionMatches && completed)
                    return true;

                // 重建是可回滚的（旧场景先备份到 Logs）；漏掉一次重建的表现是
                // "改了代码却像没生效"，排查成本高得多 —— 所以存疑一律按过期处理。
                Debug.Log(
                    "[StarDefensePipeline] 场景需要重建：标记为「"
                        + (lines.Length > 0 ? lines[0].Trim() : "空")
                        + " / " + (lines.Length > 1 ? lines[1].Trim() : "无状态行")
                        + "」，当前构建器版本 " + BuilderRevision + "。");
                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[StarDefensePipeline] 无法读取标记，按过期处理并重建：" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 清掉「已完成」标记，让下一次自动触发真的会重跑。
        /// 供 <see cref="StarDefenseAutoBuildOnce"/> 在发现场景缺失时调用 —— 否则
        /// <see cref="RunPipeline"/> 里的"已有成功产物就跳过"会把这次重建静默吞掉。
        /// </summary>
        internal static void Invalidate()
        {
            TryDeleteMarker();
        }

        private static int waitTicks;

        private static void TryRunOnce()
        {
            if (Application.isBatchMode)
                return;

            // 只等编译结束，不等 isUpdating —— 另外两个美术侧钩子（Structure/PlayerAssetAutoBuild）
            // 也只判 isCompiling，这样三者能在同一批 delayCall 里连着跑完。
            // 若在这里多等 isUpdating，本流程会被后面那些钩子触发的资产导入一直推后，
            // 表现是"标记没写、日志没生成，像是压根没执行"。
            if (EditorApplication.isCompiling)
            {
                RequeueWhileCompiling();
                return;
            }

            // 播放模式下不写资产、不建场景。退出播放后由 OnPlayModeStateChanged 补跑。
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            // 「完成」的判据是标记与场景同时存在、且版本一致。只看标记是不够的：上次若在中途被
            // 域重载打断，标记会停在 "started" 而场景没生成，此后每次重载都以为已经跑过了。
            if (MarkerIsCurrent())
                return;

            Run();
        }

        private static void RequeueWhileCompiling()
        {
            if (++waitTicks < 60)
            {
                EditorApplication.delayCall += TryRunOnce;
                return;
            }

            Debug.LogWarning(
                "[StarDefensePipeline] 编辑器长时间处于编译状态，已放弃自动执行。"
                    + "请手动点击菜单 Star Defense/★ 全流程。"
            );
        }

        private static void Run()
        {
            var log = new StringBuilder();
            log.AppendLine("Star Defense 美术接入管线");
            log.AppendLine("when: " + DateTime.Now.ToString("u"));
            log.AppendLine();

            // 这个判断必须放在写标记之前：否则一进播放模式就留下一个"进行中"标记，
            // 而流程其实什么都没做。
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                log.AppendLine("跳过：编辑器处于播放模式。");
                log.AppendLine("重建场景用的 EditorSceneManager.NewScene 在运行期被 Unity 禁止，");
                log.AppendLine("整条链推迟到退出播放模式后自动重跑（也可以随时点菜单）。");
                WriteLog(log.ToString());
                Debug.LogWarning(
                    "[StarDefensePipeline] 处于播放模式，已跳过；退出播放后会自动重试。详见 " + LogPath
                );
                return;
            }

            // 先把标记写成"进行中"。构建过程里会调 AssetDatabase.Refresh()，
            // 一旦它引发域重载，本次调用栈会被销毁而流程中断 —— 若等到结尾才写标记，
            // 编辑器每次重载都会重新起一遍流程，陷入死循环。先占位可以彻底断掉这个可能。
            WriteMarker("started");

            // 0/5 工程层准备。
            //
            // 第一人称自身剔除把"自己的身体"放进一个专用层，靠它把角色网格从第一人称相机
            // 的可见集合里移出去。层名是硬引用，但层配置存在 ProjectSettings/TagManager.asset 里 ——
            // 换机器、回滚工程、别人 clone 一份，这个层就没了，而缺失的表现只是"穿模又回来了"。
            //
            // 这一步**不参与后面的 ok 判定**：层缺失时运行时有一层兜底（退化为逐渲染器关闭绘制，
            // 只是自身的影子会一起消失），不该因为它把整条资产管线拦下来。
            Step(log, "0/5 准备工程层", () =>
            {
                int layer = ProjectLayerSetup.EnsureLayer(FirstPersonBodyCuller.BodyLayerName);
                return layer >= 0
                    ? $"层 {FirstPersonBodyCuller.BodyLayerName} = {layer}"
                    : $"未能创建层 {FirstPersonBodyCuller.BodyLayerName}（运行时将退化为关闭自身绘制）";
            });

            // 前三步互相独立（建筑、角色、敌人是三套资产），都跑；后两步依赖前面全部成功。
            //
            // 已经建过的会跳过：资产构建含 FBX 重导入，是整条链最慢的一段，
            // 而"场景缺失要重建"这种常见情形其实不需要重新导一次资产。
            //
            // 判据是**标记 + 代表性产物 + 源戳**三项，见 BuildOrSkip。前两项只证明"建过"，
            // 第三项才证明"建的是现在这份源"—— 少一项都会在美术换资产后被静默跳过。
            bool structureOk = BuildOrSkip(
                log,
                "1/5 生成建筑与环境资产",
                StructureMarker,
                StructureProbe,
                StructureStamp,
                StructureSourceDirs,
                StructureAssetBuilder.BuildStructureAssets
            );

            bool playerOk = BuildOrSkip(
                log,
                "2/5 生成角色与装备资产",
                PlayerMarker,
                PlayerProbe,
                PlayerStamp,
                PlayerSourceDirs,
                PlayerAssetBuilder.BuildPlayerAssets
            );

            // 动画控制器是整个 2/5 步里唯一不依赖 FBX 的部分：状态、连线、参数全部由
            // PlayerAssetBuilder 里的代码决定。正因为它和 FBX 资产共用一个标记，
            // "改了连线但标记没变"时会被上面那行一起跳过 —— 表现是"代码改了、角色的动作
            // 纹丝不动"，而且不报任何错。所以它必须**无条件**重刷一遍。
            // 代价只是一次资产写入 + 一次 Prefab 引用回接，比重新导入角色便宜得多。
            if (playerOk)
                playerOk = Step(log, "2b/5 重建动画控制器", PlayerAssetBuilder.RebuildAnimatorController);

            bool enemyOk = BuildOrSkip(
                log,
                "3/5 生成敌人与 Boss 资产",
                EnemyAssetBuilder.MarkerPath,
                EnemyAssetBuilder.ProbePath,
                EnemyStamp,
                EnemySourceDirs,
                EnemyAssetBuilder.BuildEnemyAssets
            );

            // 建造栏切图的导入设置必须在场景构建之前落实。
            //
            // 不靠 BuildBarSliceImporter 自己的 [InitializeOnLoad] 钩子，是因为那个钩子与本流程
            // 都挂在启动的 delayCall 上，先后不确定 —— 而顺序反了的后果是隐性的：
            // 场景会把"按默认设置导入的贴图"引用进去，随后设置虽然补上了，引用也没断，
            // 但 maxTextureSize / 压缩方式已经不是构建场景时那一份了。
            Step(log, "3b/5 导入建造栏 UI 切图", BuildBarSliceImporter.EnsureImported);

            bool ok = structureOk && playerOk && enemyOk;

            if (ok)
                ok = Step(log, "4/5 把 Prefab 绑到配置资产", BindPrefabs);
            else
                log.AppendLine("4/5 把 Prefab 绑到配置资产：跳过（前置步骤未通过）");

            if (ok)
                ok = Step(log, "5/5 重建场景", () => StarDefenseSceneBuilder.BuildFirstVersionScene());
            else
                log.AppendLine("5/5 重建场景：跳过（前置步骤未通过）");

            if (ok)
                SuppressLegacyAutoBuild();

            log.AppendLine();
            log.AppendLine(ok ? "结果：全部完成" : "结果：中断，后续步骤已跳过");
            log.AppendLine("构建器版本：" + BuilderRevision);
            log.AppendLine("重跑：点菜单 Star Defense/★ 全流程，或改 BuilderRevision 让下次打开工程自动重跑");

            WriteLog(log.ToString());

            if (ok)
            {
                WriteMarker("completed");
                Debug.Log("[StarDefensePipeline] 全流程完成，场景已使用美术 Prefab。详见 " + LogPath);
            }
            else
            {
                // 失败时把开头那个占位标记删掉。
                // 不删的话这个标记会一直挂着，修好代码后流程也永远不会再自动跑一次，
                // 表现就是"改了代码却像没生效"，只能靠手工删文件才能恢复。
                // 删除只影响自动触发：失败本身不会引起域重载，所以不会陷入重跑循环；
                // 需要立即重试仍然可以点菜单（菜单不检查标记）。
                TryDeleteMarker();
                Debug.LogError("[StarDefensePipeline] 流程中断，Unity 工程未被破坏，详见 " + LogPath);
            }
        }

        private static string BindPrefabs()
        {
            StarDefenseAssetBinder.BindStats stats = StarDefenseAssetBinder.Bind();
            return $"新绑 {stats.Bound} / 已最新 {stats.Unchanged} / 缺美术资产 {stats.Missing}";
        }

        /// <summary>
        /// 某个资产批次：跳过还是重建。
        ///
        /// 判据是三项 —— 标记 + 代表性产物 + **源戳**。前两项是**产物侧**的证据，
        /// 它们证明"建过"，但回答不了"产物是不是用**现在这份**源做的"。
        /// 缺了第三项，美术重导一版 FBX 再拷进工程就会这样收场：标记没变、Prefab 也还在，
        /// 整步被静默跳过，Prefab 仍是旧的，新资产压根没进过 Unity ——
        /// 一直等到别处因为缺东西报错才暴露（实测踩到过：三个新动画剪辑从未被导入，
        /// 直到 2b/5 的混合树断言报"缺一半"）。
        ///
        /// 存疑一律重建：重建的代价只是慢，而在旧资产上继续调试的代价是几十分钟的错判。
        /// 但**重建必须留痕** —— 静默跳过难查，静默重建一样难查。
        /// </summary>
        private static bool BuildOrSkip(
            StringBuilder log,
            string title,
            string marker,
            string probe,
            string stampPath,
            string[] sourceDirs,
            Action build
        )
        {
            string reason =
                !File.Exists(marker) ? "无标记"
                : !File.Exists(probe) ? "代表性产物缺失"
                : !SourceUnchanged(stampPath, sourceDirs) ? "源模型与上次构建时不一致"
                : null;

            if (reason == null)
                return Skip(log, title, "标记、产物与源戳齐备");

            log.AppendLine("    └ " + reason + " ⇒ 重建");

            return Step(
                log,
                title,
                () =>
                {
                    build();
                    // 只在构建成功之后写源戳：失败时源戳不更新，
                    // 下次进来还会重建 —— 这正是我们要的重试语义。
                    WriteSourceStamp(stampPath, sourceDirs);
                }
            );
        }

        /// <summary>
        /// 该批源模型的指纹：逐文件「相对路径 + 字节数 + 修改时间」。
        ///
        /// 用元数据而不是内容哈希：这段代码每次域重载都要算一遍，而三套源合计 26 MB，
        /// 为了算哈希每次都读一遍不值得。代价是"内容变了但字节数与时标都没动"会漏检 ——
        /// 这种情形要靠手工文件操作才做得出来，实际不可达。
        ///
        /// 目录不存在、或单个文件取不到元数据，都要**如实写进戳**而不是跳过：
        /// 否则"读不到"会伪装成"没变"，那正是这类判据最容易出错的地方。
        ///
        /// 已知未覆盖：贴图（`Art/Textures`）不在戳里 —— 换贴图不会触发资产重建。
        /// 要覆盖就把对应贴图目录加进上面那三个 SourceDirs。
        /// </summary>
        private static string ComputeSourceStamp(string[] dirs)
        {
            var sb = new StringBuilder();
            var files = new List<string>();

            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir))
                {
                    sb.Append("missing|").Append(dir).Append('\n');
                    continue;
                }

                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".fbx" || ext == ".glb")
                        files.Add(file);
                }
            }

            // 排序后再拼：文件系统返回顺序不保证稳定，不排序会自己造成假差异。
            files.Sort(StringComparer.Ordinal);

            foreach (string file in files)
            {
                string shown = file.Replace('\\', '/');
                try
                {
                    var info = new FileInfo(file);
                    sb.Append(shown)
                        .Append('|').Append(info.Length)
                        .Append('|').Append(info.LastWriteTimeUtc.Ticks)
                        .Append('\n');
                }
                catch (Exception e)
                {
                    sb.Append(shown).Append("|?|").Append(e.GetType().Name).Append('\n');
                }
            }

            return sb.ToString();
        }

        private static bool SourceUnchanged(string stampPath, string[] dirs)
        {
            if (!File.Exists(stampPath))
                return false;

            try
            {
                return File.ReadAllText(stampPath) == ComputeSourceStamp(dirs);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[StarDefensePipeline] 读不到源戳 " + stampPath + "，按需重建：" + e.Message);
                return false;
            }
        }

        private static void WriteSourceStamp(string stampPath, string[] dirs)
        {
            try
            {
                // 与标记同样放在点开头的文件里，Unity 根本不导入它们，所以不必 ImportAsset。
                File.WriteAllText(stampPath, ComputeSourceStamp(dirs));
            }
            catch (Exception e)
            {
                // 写不进去只意味着下次会重建一遍，不影响本次结果 —— 但必须说出来。
                Debug.LogWarning("[StarDefensePipeline] 无法写源戳 " + stampPath + "：" + e.Message);
            }
        }

        /// <summary>记一句"跳过"，返回成功。跳过必须留痕 —— 静默跳过是这套流程最难查的故障形态。</summary>
        private static bool Skip(StringBuilder log, string title, string reason)
        {
            log.AppendLine(title + "：跳过（" + reason + "）");
            return true;
        }

        /// <summary>跑一步、记录结果。异常被吞掉并落盘，避免打断调用方（比如编辑器加载流程）。</summary>
        private static bool Step(StringBuilder log, string title, Action action)
        {
            try
            {
                action();
                log.AppendLine(title + "：完成");
                return true;
            }
            catch (Exception e)
            {
                log.AppendLine(title + "：失败");
                log.AppendLine(e.ToString());
                return false;
            }
        }

        /// <summary>同上，但该步骤能产出一句摘要，写进日志便于事后核对。</summary>
        private static bool Step(StringBuilder log, string title, Func<string> action)
        {
            try
            {
                string detail = action();
                log.AppendLine(title + "：完成" + (string.IsNullOrEmpty(detail) ? string.Empty : " —— " + detail));
                return true;
            }
            catch (Exception e)
            {
                log.AppendLine(title + "：失败");
                log.AppendLine(e.ToString());
                return false;
            }
        }

        private static void SuppressLegacyAutoBuild()
        {
            foreach (string marker in LegacyMarkers)
            {
                try
                {
                    if (!File.Exists(marker))
                        File.WriteAllText(marker, "handled by StarDefensePipeline at " + DateTime.Now.ToString("u") + "\n");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[StarDefensePipeline] 无法写入 " + marker + "：" + e.Message);
                }
            }
        }

        private static void WriteMarker(string status)
        {
            try
            {
                // 首行必须是版本：MarkerIsCurrent 只读首行，格式变了就等于永远"过期"。
                string content = "revision: " + BuilderRevision + "\n"
                                 + status + " at " + DateTime.Now.ToString("u") + "\n";
                File.WriteAllText(MarkerPath, content);
                AssetDatabase.ImportAsset(MarkerPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[StarDefensePipeline] 无法写入标记：" + e.Message);
            }
        }

        /// <summary>
        /// 删掉标记文件，让下次自动触发可以重新跑一遍。
        /// 这里刻意不调 AssetDatabase.Refresh()：标记是点开头的隐藏文件，Unity 根本不导入它，
        /// 没有 meta 需要清理；而 Refresh 在编辑器启动阶段有可能引发额外的域重载。
        /// </summary>
        private static void TryDeleteMarker()
        {
            try
            {
                if (File.Exists(MarkerPath))
                    File.Delete(MarkerPath);
                if (File.Exists(MarkerPath + ".meta"))
                    File.Delete(MarkerPath + ".meta");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[StarDefensePipeline] 无法删除标记：" + e.Message);
            }
        }

        private static void WriteLog(string text)
        {
            try
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(LogPath, text);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[StarDefensePipeline] 无法写日志 " + LogPath + "：" + e.Message);
            }
        }
    }
}
