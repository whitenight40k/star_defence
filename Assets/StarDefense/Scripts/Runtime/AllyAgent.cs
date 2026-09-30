using UnityEngine;

namespace StarDefense
{
    public class AllyAgent : MonoBehaviour
    {
        public StarDefenseGame game;
        public string roleName = "采集员";
        public float moveRadius = 18f;
        public float fireRange = 28f;
        public float fireDamage = 9f;
        public float fireInterval = 0.45f;

        /// <summary>
        /// 贴地抬升（米）：原点在底面的资产给 0，原点在几何中心的图元胶囊给 1。
        /// 由场景构建器在摆放时写入。
        /// </summary>
        public float groundOffset;

        /// <summary>与动画控制器约定的参数名，见 <see cref="AnimatorParameters"/>。</summary>
        private float resourceTimer;
        private float fireTimer;
        private Vector3 home;
        private Vector3 wanderTarget;

        private Animator animator;
        private bool drivesSpeed;
        private bool drivesAirborne;

        private void Start()
        {
            // 先把自身贴到地面上，再以这个点为"家"。否则游走半径是绕着一个浮空点算的，
            // 队友会整体飘在坡上方。
            transform.position = PlanetTerrain.SnapToGround(transform.position, groundOffset);
            home = transform.position;
            PickNewWanderTarget();
            BindAnimator();
        }

        /// <summary>
        /// 队友在场景里是有模型的，不驱动动画就会保持绑定姿势平移。这里只在控制器
        /// 确实声明了对应参数时才接管，避免对图元回退（没有 Animator）报空引用。
        ///
        /// 参数表见 <see cref="AnimatorParameters"/> —— 它和编辑器里的控制器是同一份约定，
        /// 名字写错不会报错，只会让某个动作永远不播。
        /// </summary>
        private void BindAnimator()
        {
            animator = GetComponentInChildren<Animator>();
            drivesSpeed = AnimatorParameters.Has(
                animator,
                AnimatorParameters.Speed,
                AnimatorControllerParameterType.Float
            );
            drivesAirborne = AnimatorParameters.Has(
                animator,
                AnimatorParameters.Airborne,
                AnimatorControllerParameterType.Bool
            );

            // 队友是逐帧贴地推进的，永远不会腾空。不写也不会误播跳跃
            // （Airborne 是肯定式设计，没人写时初值恰好等于"站在地上"），
            // 但显式声明能让"队友为什么从不播跳跃"在代码里直接读得出来。
            if (drivesAirborne)
                animator.SetBool(AnimatorParameters.Airborne, false);
        }

        private void Update()
        {
            if (game == null)
                return;

            resourceTimer -= Time.deltaTime;
            if (resourceTimer <= 0f)
            {
                resourceTimer = game.Balance.allyResourceInterval;
                game.AddResource(ResourceType.Metal, game.Balance.allyResourceAmount);
                game.AddResource(ResourceType.Energy, Mathf.Max(1, game.Balance.allyResourceAmount / 2));
            }

            fireTimer -= Time.deltaTime;
            EnemyUnit target = game.GetNearestEnemy(transform.position, fireRange);
            if (target != null && fireTimer <= 0f)
            {
                fireTimer = fireInterval;
                target.ApplyDamage(fireDamage);
                game.SpawnTracer(transform.position + Vector3.up * 1.3f, target.transform.position + Vector3.up * 0.7f, Color.cyan);
            }

            if (Vector3.Distance(transform.position, wanderTarget) < 1.5f)
                PickNewWanderTarget();

            Vector3 direction = wanderTarget - transform.position;
            direction.y = 0f;

            float speed = 0f;
            // 位置与朝向**一次**读、**一次**写（GetPositionAndRotation / SetPositionAndRotation）。
            // 分开读写会让 Unity 各发两次 transform 变更通知，而且中间那一刻是
            // "新位置配旧朝向"的混合态。这是每帧路径，不是初始化路径（UNT0022 / UNT0036）。
            transform.GetPositionAndRotation(out Vector3 nextPosition, out Quaternion nextRotation);

            if (direction.sqrMagnitude > 0.05f)
            {
                speed = 2.4f;
                Vector3 dir = direction.normalized;

                // 这里直接写**世界**旋转。对今天的友军资产是正确的 —— PFB_Player_Astronaut_A
                // 属于 Y-up 批次、根是 identity，LookRotation 就是它要的语义。
                //
                // 但若哪天把队友换成 Z-up 批次的资产（根带 -90°X 的 Blender Z-up → Y-up 校正，
                // 见 BuildingPose 的说明），这一句会把那份校正抹掉、队友**横躺**。
                // 那时要改成 `LookRotation(...) * 基准旋转`（基准在首次写入前取一次），
                // 做法与 TurretDefense.yawBaseRotation 相同。
                nextRotation = Quaternion.LookRotation(dir, Vector3.up);
                nextPosition += dir * (speed * Time.deltaTime);
            }

            // 位移只发生在 XZ 上，y 必须每帧按地形重算，否则队友会平着飘过沙丘。
            transform.SetPositionAndRotation(
                PlanetTerrain.SnapToGround(nextPosition, groundOffset), nextRotation);

            if (drivesSpeed)
                animator.SetFloat(AnimatorParameters.Speed, speed);
        }

        private void PickNewWanderTarget()
        {
            Vector2 offset = Random.insideUnitCircle * moveRadius;
            wanderTarget = home + new Vector3(offset.x, 0f, offset.y);
        }
    }
}
