using UnityEngine;

namespace StarDefense
{
    [RequireComponent(typeof(Buildable))]
    public class TurretDefense : MonoBehaviour
    {
        public StarDefenseGame game;
        public Transform muzzle;

        /// <summary>
        /// 偏航轴。美术把炮塔拆成"静止底座 + 可旋转转塔"两个网格，
        /// 转塔原点落在偏航轴上，所以只旋转这个 transform。
        /// 留空则退回旋转自身——那是图元时代的做法。
        /// </summary>
        public Transform yawPivot;

        /// <summary>
        /// 偏航轴的**基准世界旋转** —— 即"尚未被本组件转过"时的朝向。
        ///
        /// 为什么需要它：下面转的是**世界**旋转，而美术炮塔的转塔网格属于 Z-up 批次
        /// （根节点带着 -90°X 的 Blender Z-up → Y-up 校正，转塔作为子节点必然继承它）。
        /// 直接写 <c>pivot.rotation = Quaternion.LookRotation(dir, up)</c> 会把那份校正
        /// 抹掉：转塔几何的"上"是 local Z，被送到水平方向 ⇒ **转塔躺下去**。
        /// 乘法顺序与 <see cref="BuildingPose"/> 一致 —— 把目标朝向当作**世界空间前置**乘上去。
        ///
        /// **惰性记录**（第一次真要转向时才读）而不是在 Awake 里读，原因是一个确定的时序：
        /// <see cref="StarDefenseGame.CreateBuilding"/> 是在 <c>AddComponent</c> **之后**才赋
        /// <see cref="yawPivot"/> 的，Awake 那一刻它还是 null；而第一次执行到这里时，
        /// 还没有任何代码碰过这个 transform，读到的就是干净的基准。
        /// </summary>
        private Quaternion yawBaseRotation;
        private bool yawBaseCaptured;

        private Buildable buildable;
        private float fireTimer;

        private void Awake()
        {
            buildable = GetComponent<Buildable>();
        }

        private void Update()
        {
            if (game == null || buildable == null || buildable.config == null || !buildable.config.isWeapon || !buildable.IsOperational)
                return;

            fireTimer -= Time.deltaTime;
            if (fireTimer > 0f)
                return;

            EnemyUnit target = game.GetNearestEnemy(transform.position, buildable.config.range);
            if (target == null)
                return;

            fireTimer = Mathf.Max(0.05f, buildable.config.fireInterval);

            // 只转转塔，不转整体。转整体会把静止底座一起扭掉——
            // 底座在视觉上应当纹丝不动，跟着转看起来像整座塔在原地打摆子。
            Transform pivot = yawPivot != null ? yawPivot : transform;

            // 基准必须在**第一次写 rotation 之前**取，之后就一直复用（写了就成了污染值）。
            if (!yawBaseCaptured)
            {
                yawBaseRotation = pivot.rotation;
                yawBaseCaptured = true;
            }

            Vector3 direction = target.transform.position - pivot.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.01f)
            {
                // 乘 yawBaseRotation，不要只写 LookRotation —— 见上方字段注释：
                // 少了它，转塔会丢掉资产自带的 -90°X 校正而躺倒。
                pivot.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up) * yawBaseRotation;
            }

            Fire(target);

            Vector3 tracerStart = muzzle != null ? muzzle.position : pivot.position + Vector3.up * 1.5f;
            game.SpawnTracer(tracerStart, target.transform.position + Vector3.up * 0.8f, Color.red);
        }

        /// <summary>
        /// 按 BuildableConfig 上配好的效果依次结算：直伤 → 减速 → 溅射。
        /// 这三个字段此前只在资产里配了值而没有代码读取（电磁塔不减速、炮塔无溅射），这里补上接线。
        /// </summary>
        private void Fire(EnemyUnit target)
        {
            BuildableConfig config = buildable.config;
            target.ApplyDamage(config.damagePerShot);

            if (config.slowMultiplier < 1f)
                target.ApplySlow(config.slowMultiplier, Mathf.Max(1f, config.fireInterval * 3f));

            if (config.splashRadius > 0f)
                game.Explode(target.transform.position, config.splashRadius, config.damagePerShot * 0.45f, Color.red, false);
        }
    }
}
