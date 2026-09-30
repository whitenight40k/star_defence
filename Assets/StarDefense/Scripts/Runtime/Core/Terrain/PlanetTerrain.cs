using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 沙漠星球地形的高度场 —— **全工程唯一的高度来源**。
    ///
    /// 为什么要有这个类：地面一旦不平，"谁该跟着地面走"立刻变成一张必须逐项核对的清单 ——
    /// 摆放（岩石 / 矿点 / 建筑 / 出生点 / 基地核心）与移动（玩家 / 队友 / 敌人）任何一处漏掉，
    /// 表现都是物体悬空或半截埋进沙里，而且**不会报任何错**。所以高度公式只在这里定义一遍，
    /// 摆放与移动都调它，编辑器构建期与运行时也保证是同一个函数、同一个结果。
    ///
    /// 两条实现约束：
    ///
    /// 1. **纯函数，不含 <see cref="Random"/>。** 场景构建器里 <c>CreateRocks</c> 依赖
    ///    <c>Random.InitState</c> 之后的调用序列稳定（换资产不该重排整张地图），
    ///    地形绝不能插进那条序列里。噪声用的是整数哈希，与 Unity 的随机数无关。
    ///
    /// 2. **常量即契约。** 网格顶点由构建器按这里的常量生成，运行时又按同一组常量采样。
    ///    把参数拆成两份（比如一半塞进 ScriptableObject）会让"网格和逻辑对不上"变成
    ///    静默偏差，所以宁可写死常量：改参数就得重建场景，这是有意的。
    /// </summary>
    public static class PlanetTerrain
    {
        /// <summary>地面网格边长（米），以原点为中心。</summary>
        public const float Size = 200f;

        /// <summary>网格分辨率（米/格）。起伏是 40 m 级的沙丘，2 m 一格足够平滑。</summary>
        public const float CellSize = 2f;

        /// <summary>
        /// 基地平台半径。这个圈内**绝对水平**：建筑原点在底面中心、炮塔偏航轴垂直，
        /// 压在斜坡上会一眼看出歪。玩家出生点（r=10）与初始防线（r≤9.4）都落在圈内。
        /// </summary>
        public const float FlatRadius = 14f;

        /// <summary>过渡带外沿：到这里起伏完全恢复自然。</summary>
        public const float BlendRadius = 46f;

        /// <summary>沙丘主起伏振幅（米）。</summary>
        public const float DuneHeight = 2.2f;

        /// <summary>
        /// 外围沙丘脊高度。抬起来把地图边缘藏到地平线以下 ——
        /// 否则从基地往外看就是"一块平板飘在天上"，边缘直接切到天空盒。
        /// </summary>
        public const float RimHeight = 6f;

        /// <summary>沙丘脊起始半径。要大于出生点最远处（约 82 m），否则敌人会刷在坡上。</summary>
        public const float RimStartRadius = 84f;

        /// <summary>沙丘脊封顶半径，与网格边缘一致。</summary>
        public const float RimEndRadius = 100f;

        /// <summary>沙丘主波长（米）。</summary>
        private const float DuneWavelength = 46f;

        /// <summary>八度之间的频率倍率。刻意用非整数，避免各层噪声对齐出方格纹。</summary>
        private const float OctaveLacunarity = 2.7f;

        /// <summary>八度之间的振幅衰减。</summary>
        private const float OctaveGain = 0.5f;

        private const int OctaveCount = 3;

        /// <summary>世界坐标 (x, z) 处的地面高度（米）。</summary>
        public static float SampleHeight(float x, float z)
        {
            float radius = Mathf.Sqrt(x * x + z * z);

            // 基地平台：中心绝对平，向外平滑过渡到自然起伏。
            // 用 smoothstep 而不是线性插值，是为了让"平"和"坡"的交界没有折线——
            // 折线在低角度光照下会显出一条硬边。
            float relief = SmoothStep(FlatRadius, BlendRadius, radius);
            float height = Fbm(x, z) * DuneHeight * relief;

            height += RimHeight * SmoothStep(RimStartRadius, RimEndRadius, radius);
            return height;
        }

        /// <summary>世界坐标处的地面高度（米）。</summary>
        public static float SampleHeight(Vector3 position)
        {
            return SampleHeight(position.x, position.z);
        }

        /// <summary>
        /// 把位置贴到地形表面，只改 y。
        ///
        /// <paramref name="groundOffset"/> 是"原点相对模型底面"的抬升：
        /// 原点在底面的资产传 0，原点在几何中心的图元（球/胶囊）传它的半径或半高。
        /// 单位移动每帧都该走这里 —— 敌人和队友都只推 XZ（<c>direction.y = 0</c>），
        /// 没有人负责 y，斜坡上就会一路悬空或陷进去。
        /// </summary>
        public static Vector3 SnapToGround(Vector3 position, float groundOffset = 0f)
        {
            position.y = SampleHeight(position.x, position.z) + groundOffset;
            return position;
        }

        /// <summary>分形噪声，返回 [-1, 1]。</summary>
        private static float Fbm(float x, float z)
        {
            float sum = 0f;
            float amplitude = 1f;
            float frequency = 1f / DuneWavelength;
            float normalisation = 0f;

            for (int i = 0; i < OctaveCount; i++)
            {
                sum += ValueNoise(x * frequency, z * frequency) * amplitude;
                normalisation += amplitude;
                amplitude *= OctaveGain;
                frequency *= OctaveLacunarity;
            }

            return normalisation > 0f ? sum / normalisation : 0f;
        }

        /// <summary>格点值噪声，返回 [-1, 1]。双线性 + smoothstep 插值。</summary>
        private static float ValueNoise(float x, float z)
        {
            int x0 = Mathf.FloorToInt(x);
            int z0 = Mathf.FloorToInt(z);
            float fx = x - x0;
            float fz = z - z0;

            float sx = fx * fx * (3f - 2f * fx);
            float sz = fz * fz * (3f - 2f * fz);

            float n00 = Hash01(x0, z0);
            float n10 = Hash01(x0 + 1, z0);
            float n01 = Hash01(x0, z0 + 1);
            float n11 = Hash01(x0 + 1, z0 + 1);

            float near = Mathf.Lerp(n00, n10, sx);
            float far = Mathf.Lerp(n01, n11, sx);
            return Mathf.Lerp(near, far, sz) * 2f - 1f;
        }

        /// <summary>
        /// 整数格点哈希，返回 [0, 1]。整数运算 + 位移，跨平台与跨运行稳定 ——
        /// 用 <c>Mathf.Sin</c> 那一类浮点哈希在坐标变大时会掉精度，网格与运行时对不上。
        /// </summary>
        private static float Hash01(int x, int z)
        {
            unchecked
            {
                int h = x * 374761393 + z * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= h >> 16;
                return (h & 0x7fffffff) / (float)0x7fffffff;
            }
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            if (Mathf.Approximately(edge0, edge1))
                return value < edge0 ? 0f : 1f;

            float t = Mathf.Clamp01((value - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
