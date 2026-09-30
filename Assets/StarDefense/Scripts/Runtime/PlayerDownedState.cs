using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 玩家倒地与复起。
    ///
    /// 此前 Health.Died 在玩家身上没有任何订阅者——血量归零后玩家照常跑动开枪，
    /// 等于整局不存在失败风险。这里补上最短闭环：倒地期间完全失去操作，倒计时结束后在基地复起。
    ///
    /// 联机版应替换为策划案 21.2 的"队友 3 秒救援"；单机 + AI 队友版先用自动复起兜底，
    /// 目的是让"被打倒"这件事产生代价，而不是为了表现救援本身。
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class PlayerDownedState : MonoBehaviour
    {
        private const float DownSeconds = 5f;
        private const float ReviveHealthRatio = 0.5f;

        /// <summary>老场景没有复活点锚点时的兜底落点：核心旁的对角偏移（米）。</summary>
        private const float ReviveOffsetFromCore = 6f;

        private const float DownedCameraDrop = 0.95f;
        private const float DownedCameraRoll = 42f;

        [Header("Wiring")]
        public StarDefenseGame game;
        public FirstPersonController movement;
        public PlayerToolController tools;
        public Camera playerCamera;

        /// <summary>
        /// 复起落点。场景里的 <c>Respawn_Player_Base</c> 锚点接在这里 —— 也就是**基地**。
        ///
        /// 留空时退回 <see cref="Revive"/> 里的"核心旁对角偏移"兜底，老场景不会原地复活。
        /// 做成一个可拖动的物体而不是代码里的常量，是因为"复活点在基地的哪个位置"属于
        /// 玩法要对齐的量：基地挪了、平台扩了，在 Inspector 里拖一下就行。
        /// </summary>
        public Transform respawnPoint;

        private Health health;
        private Vector3 cameraStandLocalPosition;
        private float downTimer;
        private bool downed;

        public bool IsDowned => downed;
        public float DownRemaining => Mathf.Max(0f, downTimer);
        public float TotalDownSeconds => DownSeconds;

        private void Awake()
        {
            health = GetComponent<Health>();
            health.Died += OnDied;
        }

        private void Start()
        {
            // 站姿相机位置在 Start 才采，不在 Awake：
            // FirstPersonController 会在自己的 Awake 里把相机搬到 Socket_Camera 眼位上，
            // 谁先谁后只由组件顺序决定、没有硬保证。Start 晚于所有 Awake，这里量到的
            // 一定是对齐后的眼位 —— 否则"倒地 −0.95 m"会以旧眼高为基准，站起来回不到原位。
            if (playerCamera != null)
                cameraStandLocalPosition = playerCamera.transform.localPosition;
        }

        private void OnDestroy()
        {
            if (health != null)
                health.Died -= OnDied;
        }

        private void Update()
        {
            if (!downed)
                return;

            downTimer -= Time.deltaTime;
            if (downTimer <= 0f)
                Revive();
        }

        private void OnDied(Health deadHealth)
        {
            if (downed)
                return;

            downed = true;
            downTimer = DownSeconds;

            SetControlEnabled(false);
            ApplyDownedCamera(true);

            if (game != null)
                game.ShowHint($"你被击倒了！{Mathf.RoundToInt(DownSeconds)} 秒后复起");
        }

        private void Revive()
        {
            downed = false;

            // 先回满再扣掉一半，避免直接 Configure 半血时被其他系统读成"刚出生"
            health.Configure(health.MaxHealth);
            health.TakeDamage(health.MaxHealth * (1f - ReviveHealthRatio));

            if (TryResolveReviveSpot(out Vector3 spot))
                transform.position = spot;

            ApplyDownedCamera(false);
            SetControlEnabled(true);

            if (game != null)
                game.ShowHint("已被队友拉回，注意别再倒下");
        }

        /// <summary>
        /// 复起落点。两级优先：**基地上的复活点锚点** → 核心旁的对角偏移兜底。
        ///
        /// 锚点这条是主路径：复活点在基地上是一个可见、可拖动的物体，而不是写在代码里的偏移量。
        /// 旧版是 <c>core + (6,0,6)</c>，落点落在核心斜前方 8.5 m —— 既说不上"在基地"，
        /// 改一次核心尺寸或平台半径就可能掉到平台外，而且不会有任何报错，
        /// 表现只是"复活的地方怪怪的"。
        ///
        /// 高度一律走 <see cref="PlanetTerrain.SnapToGround"/>，不信任锚点自己的 y：
        /// 把锚点抬到半空是一次很自然的误操作，照抄就会变成"从空中掉下来"。
        /// </summary>
        private bool TryResolveReviveSpot(out Vector3 spot)
        {
            spot = transform.position;

            if (respawnPoint != null)
            {
                spot = PlanetTerrain.SnapToGround(respawnPoint.position);
                return true;
            }

            Transform core = game != null ? game.core : null;
            if (core == null)
                return false;

            // 兜底路径（老场景没有锚点）。核心的原点在底面（资产契约），所以 core.position.y
            // 就是地面高度，这里不再加固定的 +1.2 m —— 那是图元圆柱时代的补偿，
            // 留着只会把玩家复活到半空再摔下来。
            spot = PlanetTerrain.SnapToGround(
                core.position + new Vector3(ReviveOffsetFromCore, 0f, ReviveOffsetFromCore));
            return true;
        }

        private void SetControlEnabled(bool isEnabled)
        {
            if (movement != null)
                movement.enabled = isEnabled;

            if (tools != null)
                tools.enabled = isEnabled;
        }

        private void ApplyDownedCamera(bool isDowned)
        {
            if (playerCamera == null)
                return;

            Transform cameraTransform = playerCamera.transform;
            if (isDowned)
            {
                cameraTransform.localPosition = cameraStandLocalPosition + new Vector3(0f, -DownedCameraDrop, 0.15f);
                cameraTransform.localRotation = Quaternion.Euler(0f, 0f, DownedCameraRoll);
                return;
            }

            // 只还原站位高度；俯仰角在下一帧由 FirstPersonController 重新接管
            cameraTransform.localPosition = cameraStandLocalPosition;
        }
    }
}
