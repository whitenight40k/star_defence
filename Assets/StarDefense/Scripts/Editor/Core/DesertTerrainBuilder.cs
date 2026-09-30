using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace StarDefense.EditorTools
{
    /// <summary>
    /// 沙漠星球地面的网格生成：按 <see cref="PlanetTerrain"/> 的高度场烘一张高度图网格。
    ///
    /// 为什么是"代码生成的网格"而不是 Unity <c>Terrain</c> 组件：
    ///   · 全套建筑/岩石用的是 Standard 材质的纯色 <c>MAT_DesertSand</c>，Terrain 得配
    ///     自己的地形着色器与 splat 贴图，会和场景里其它硬表面出现材质断层；
    ///   · <c>Terrain.SampleHeight</c> 只在 Terrain 已加载时可用，编辑器摆放阶段拿不到，
    ///     而这里需要构建期与运行期**同一个**高度函数（见 <see cref="PlanetTerrain"/> 的类注释）。
    /// 造型侧将来若要真 Terrain / 沙地贴图，替换点就是本类与 <see cref="PlanetTerrain.SampleHeight"/> 一处。
    /// </summary>
    internal static class DesertTerrainBuilder
    {
        internal const string GroundName = "DesertPlanet_Ground";
        internal const string MeshFolder = "Assets/StarDefense/Meshes";
        internal const string MeshAssetPath = MeshFolder + "/Terrain_DesertPlanet.asset";

        /// <summary>
        /// UV 的世界尺寸：多少米铺一张贴图。
        /// 现在 <c>MAT_DesertSand</c> 是纯色材质，UV 看不出效果；按世界尺寸铺是为了
        /// 将来换成沙地贴图时不用重新生成网格（0~1 拉伸会让近处糊成一片）。
        /// </summary>
        private const float UvWorldScale = 6f;

        /// <summary>
        /// 生成（或原地更新）地形网格资产。
        ///
        /// 原地更新而不是每次新建：v0 的 GUID 会被场景引用，重建时换 GUID 会让场景里
        /// 所有指向它的引用变成 "missing"。网格数据本身就够重建，没必要换身份。
        /// </summary>
        internal static Mesh EnsureTerrainMesh()
        {
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshAssetPath);
            bool created = mesh == null;
            if (created)
            {
                mesh = new Mesh { name = "Terrain_DesertPlanet" };
                AssetDatabase.CreateAsset(mesh, MeshAssetPath);
            }

            Fill(mesh);
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        /// <summary>按地形高度场生成地面对象：网格 + 碰撞体 + 沙地材质。</summary>
        internal static GameObject CreateGround(Material sand)
        {
            GameObject ground = new GameObject(GroundName);

            Mesh mesh = EnsureTerrainMesh();

            MeshFilter filter = ground.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            MeshRenderer renderer = ground.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = sand;

            // 玩家靠 CharacterController 落在它上面，建造射线也打它。非凸静态网格碰撞体，
            // 顶点数约 1 万、三角面 2 万，一次性烘焙，没有运行时代价。
            MeshCollider collider = ground.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;

            float min = float.MaxValue;
            float max = float.MinValue;
            int samples = 24;
            for (int i = 0; i <= samples; i++)
            {
                for (int j = 0; j <= samples; j++)
                {
                    float x = Mathf.Lerp(-PlanetTerrain.Size * 0.5f, PlanetTerrain.Size * 0.5f, i / (float)samples);
                    float z = Mathf.Lerp(-PlanetTerrain.Size * 0.5f, PlanetTerrain.Size * 0.5f, j / (float)samples);
                    float h = PlanetTerrain.SampleHeight(x, z);
                    min = Mathf.Min(min, h);
                    max = Mathf.Max(max, h);
                }
            }

            Debug.Log(
                $"[DesertTerrainBuilder] 地面生成：{PlanetTerrain.Size:F0}×{PlanetTerrain.Size:F0} m，"
                    + $"网格 {PlanetTerrain.CellSize:F1} m（{mesh.vertexCount} 顶点 / {mesh.triangles.Length / 3} 三角面），"
                    + $"高度 {min:F2}~{max:F2} m，基地平台半径 {PlanetTerrain.FlatRadius:F0} m。");

            return ground;
        }

        private static void Fill(Mesh mesh)
        {
            int cells = Mathf.Max(1, Mathf.RoundToInt(PlanetTerrain.Size / PlanetTerrain.CellSize));
            int side = cells + 1;
            float half = PlanetTerrain.Size * 0.5f;
            float step = PlanetTerrain.Size / cells;

            var vertices = new Vector3[side * side];
            var uvs = new Vector2[side * side];

            for (int z = 0; z < side; z++)
            {
                for (int x = 0; x < side; x++)
                {
                    float worldX = -half + x * step;
                    float worldZ = -half + z * step;
                    int index = z * side + x;

                    // 顶点高度一律走 PlanetTerrain —— 网格和运行时逻辑同源，不能再算一遍。
                    vertices[index] = new Vector3(worldX, PlanetTerrain.SampleHeight(worldX, worldZ), worldZ);
                    uvs[index] = new Vector2(worldX / UvWorldScale, worldZ / UvWorldScale);
                }
            }

            // 绕序：三角面 (i, i+side, i+1) 的法线朝 +Y（已按 Unity 的叉乘方向核过；
            // 与 Unity 官方 Procedural Grid 示例的绕序一致）。
            // 绕反了的后果很容易误判：Back 面剔除让地面从上方**完全看不见**，
            // 视野里直接是天空盒 —— 看着像"材质丢了/网格没生成"，其实是绕序。
            var triangles = new int[cells * cells * 6];
            int t = 0;
            for (int z = 0; z < cells; z++)
            {
                for (int x = 0; x < cells; x++)
                {
                    int i = z * side + x;
                    triangles[t++] = i;
                    triangles[t++] = i + side;
                    triangles[t++] = i + 1;

                    triangles[t++] = i + 1;
                    triangles[t++] = i + side;
                    triangles[t++] = i + side + 1;
                }
            }

            mesh.Clear();
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }
    }
}
