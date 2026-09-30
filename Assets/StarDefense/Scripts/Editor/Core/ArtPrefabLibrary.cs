using UnityEditor;
using UnityEngine;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// 美术 Prefab 的统一查找与实例化入口。
    ///
    /// 存在的理由：场景构建器必须在"美术已就绪"和"美术还没做完"两种情况下都能跑通。
    /// 把「找 Prefab → 找不到就返回 null」收敛到一处，好处是命名规则只定义一遍，
    /// 且所有回退路径的口径一致：有真资产就用真的，没有就退回图元，绝不抛异常打断整条构建。
    ///
    /// 建筑 Prefab 不走这里 —— 它们由 <see cref="StarDefenseAssetBinder"/> 绑到 BuildableConfig 上，
    /// 运行时由 StarDefenseGame.CreateBuilding 读取，本类只管场景构建期要直接摆放的资产。
    /// </summary>
    internal static class ArtPrefabLibrary
    {
        internal const string PrefabDir = "Assets/StarDefense/Art/Prefabs";

        // 角色与装备（PlayerAssetBuilder 产出）
        internal const string Player = "PFB_Player_Astronaut_A";
        internal const string Gun = "PFB_WPN_Gun_A";
        internal const string Pickaxe = "PFB_WPN_Pickaxe_A";

        // 环境与道具（StructureAssetBuilder 按「去掉 BLD_/ENV_/PRP_ 前缀」规则命名）
        internal const string RockA = "PFB_Rock_A";
        internal const string RockB = "PFB_Rock_B";
        internal const string Crystal = "PFB_Crystal_A";
        internal const string SupplyCrate = "PFB_SupplyCrate_A";
        internal const string Barricade = "PFB_Barricade_A";

        /// <summary>
        /// 基地核心（BLD_Core_A）。它是场景构建期就直接摆的一座建筑，不是运行时造的，
        /// 所以走本类而不是 StarDefenseAssetBinder 的 BuildableConfig 绑定。
        /// </summary>
        internal const string Core = "PFB_Core_A";

        // 敌人与 Boss（EnemyAssetBuilder 产出，同样按「去掉 ENM_/BOS_ 前缀」规则命名）。
        // 它们不在场景里摆 —— 敌人由 StarDefenseGame.SpawnEnemy 运行时刷出，
        // 这份清单是给核验与日志用的，让"哪几只虫子有真模型"在一处可查。
        internal const string Charger = "PFB_Charger_A";
        internal const string Bomber = "PFB_Bomber_A";
        internal const string Burrower = "PFB_Burrower_A";
        internal const string Sniper = "PFB_Sniper_A";
        internal const string Jammer = "PFB_Jammer_A";
        internal const string PlanetBeast = "PFB_PlanetBeast_A";

        internal static readonly string[] EnemyPrefabs =
        {
            Charger,
            Bomber,
            Burrower,
            Sniper,
            Jammer,
            PlanetBeast,
        };

        /// <summary>按名取 Prefab。资产尚未产出时返回 null，由调用方决定回退方式。</summary>
        internal static GameObject Find(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return null;

            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDir + "/" + prefabName + ".prefab");
        }

        /// <summary>
        /// 实例化为场景对象（保持 Prefab 连接，便于以后批量改资产）。
        /// 资产缺失时返回 null，调用方需自行回退到图元路径。
        ///
        /// <paramref name="rotation"/> 是**世界空间水平偏航**：它叠在资产根节点自带的校正之上，
        /// 而不是替换掉后者。原因见下面的注释与 <see cref="BuildingPose"/>。
        /// </summary>
        internal static GameObject Instantiate(string prefabName, Transform parent, Vector3 position, Quaternion rotation)
        {
            GameObject prefab = Find(prefabName);
            if (prefab == null)
                return null;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);

            // 姿态交给 BuildingPose。这里曾经直接写 transform.SetPositionAndRotation(position, rotation)，
            // 而那句设的是**世界**旋转 —— 会把资产根节点自带的 -90° X 轴向校正整个抹掉。
            // 受害的是全部 Z-up 批次资产（建筑、Core、Rock、Crystal、SupplyCrate、WPN_*），
            // 它们会**横躺**，并且"原点在底面中心"随之失效（落点也偏，看着像两个 bug）；
            // Y-up 批次（PFB_Player_Astronaut_A、各敌人）的根是 identity，所以那边一直看不出异常。
            //
            // 基准（CaptureBase）必须在任何赋值**之前**读，两句要紧挨着，中间不要插别的东西。
            Quaternion baseRotation = BuildingPose.CaptureBase(instance);
            BuildingPose.Apply(instance, position, rotation, baseRotation);
            return instance;
        }

        /// <summary>
        /// 把整个实例挪到指定地面高度：读它的世界包围盒，让最低点正好落在 groundY。
        ///
        /// 建筑契约保证「原点在底面中心」，直接摆到地面坐标即可；但角色、岩石、道具这些
        /// 资产没有同样的保证，硬编码 y 会穿地或悬空。用包围盒对齐，无论美术把原点放在
        /// 脚底、模型中心还是别处，结果都一致。
        /// </summary>
        internal static void DropToGround(GameObject instance, float groundY)
        {
            if (instance == null)
                return;

            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return;

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            Vector3 p = instance.transform.position;
            instance.transform.position = new Vector3(p.x, p.y + (groundY - bounds.min.y), p.z);
        }

        /// <summary>
        /// 清掉实例上的所有碰撞体。用于"角色本体不该参与射线命中"这类场景 ——
        /// 玩家由 CharacterController 负责碰撞，模型自带的 MeshCollider 反而会吃掉自己的射击射线。
        /// </summary>
        internal static void StripColliders(GameObject instance)
        {
            if (instance == null)
                return;

            Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                Object.DestroyImmediate(colliders[i]);
        }

        /// <summary>
        /// 保证实例的层级里至少有一个碰撞体；没有就按渲染包围盒补一个 BoxCollider。
        ///
        /// 为什么需要：采集与射击靠 Physics.Raycast 命中，缺碰撞体的资源点等于"打不到"，
        /// 而这不会报任何错 —— 表现是玩家对着矿石砍半天没反应，比崩溃难查得多。
        /// 补出来的碰撞体挂在根节点，命中后 ResourceNode 用 GetComponentInParent 一样能拿到，
        /// 所以和"碰撞体在子网格上"的美术资产是同一套判定路径。
        /// </summary>
        /// <returns>保证完成后是否可用。渲染器也没有时返回 false。</returns>
        internal static bool EnsureCollider(GameObject instance)
        {
            if (instance == null)
                return false;

            if (instance.GetComponentInChildren<Collider>(true) != null)
                return true;

            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return false;

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            BoxCollider box = instance.AddComponent<BoxCollider>();

            // 包围盒是世界坐标，BoxCollider 的尺寸是本地的：中心用 InverseTransformPoint 换算，
            // 尺寸要除掉父级缩放，否则父级一旦有缩放，碰撞体会和模型对不上。
            box.center = instance.transform.InverseTransformPoint(bounds.center);
            Vector3 scale = instance.transform.lossyScale;
            box.size = new Vector3(
                DivideByScale(bounds.size.x, scale.x),
                DivideByScale(bounds.size.y, scale.y),
                DivideByScale(bounds.size.z, scale.z)
            );
            return true;
        }

        private static float DivideByScale(float value, float scale)
        {
            return Mathf.Approximately(scale, 0f) ? value : value / Mathf.Abs(scale);
        }
    }
}
