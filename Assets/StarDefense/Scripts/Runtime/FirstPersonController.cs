using UnityEngine;

namespace StarDefense
{
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        /// <summary>
        /// 美术契约里的眼位插槽，挂在 <c>Head</c> 骨骼下，锚点是造型侧量好的瞳孔位置。
        ///
        /// 相机摆到这个插槽上，而不是写死一个高度：角色的头身比例是造型说了算的，
        /// 代码这边写死 1.62 就会和资产实际尺寸脱钩（本项目的角色净高只有 1.60 m，
        /// 头在 1.20~1.55 m，1.62 直接飞到头顶外面去了）。
        /// </summary>
        public const string EyeSocketName = "Socket_Camera";

        public Camera playerCamera;
        public float walkSpeed = 6f;
        public float runSpeed = 9f;
        public float mouseSensitivity = 2.2f;
        public float gravity = -18f;

        /// <summary>起跳初速度（米/秒）。0 表示未设置（老场景反序列化的结果），走 <see cref="DefaultJumpSpeed"/>。</summary>
        public float jumpSpeed;

        /// <summary>
        /// 勾上则保留场景里手调的相机位置，不做眼位对齐。
        ///
        /// 这里刻意用**否定式开关**：本脚本新增字段时，老场景里反序列化出来是 false，
        /// 而 false 恰好等于"照常对齐"。若写成 alignToEyeSocket=true，老场景会退化成 false，
        /// 新逻辑在旧场景上永远不会执行 —— 这类 bug 只在真机上暴露，很难查。
        /// </summary>
        public bool keepAuthoredEyePosition;

        /// <summary>近裁剪面。0 表示未设置（老场景反序列化的结果），走 <see cref="DefaultNearClipPlane"/>。</summary>
        public float nearClipPlane;

        /// <summary>没有 Socket_Camera（图元回退玩家）且量不到模型净高时使用的眼高，米。</summary>
        public float fallbackEyeHeight;

        /// <summary>
        /// 相机建议的近裁剪面。
        ///
        /// 角色是**一整块蒙皮网格**，眼位落在头部内部，而头骨前脸离眼位只有十几厘米。
        /// 近裁剪面留在 0.05，这些近处面片会留在画面里（护目镜镜片、下巴、脖子）；
        /// 抬到 0.10 直接把 10 cm 以内的自身几何全部裁掉，画面立刻干净，
        /// 同时远不影响采矿镐（挥击最低点离眼 0.5 m 开外）。
        /// </summary>
        public const float DefaultNearClipPlane = 0.1f;

        /// <summary>默认眼高：老场景里 fallbackEyeHeight 是 0 时用它。</summary>
        public const float DefaultEyeHeight = 1.62f;

        /// <summary>
        /// 起跳初速度。重力 −18 时约跳起 1.0 m、滞空 0.67 s，
        /// 与美术给的三段跳跃动画（起跳 0.46 s / 滞空 0.67 s / 落地 0.54 s）时长相符。
        /// </summary>
        public const float DefaultJumpSpeed = 6f;

        /// <summary>
        /// 落地宽限（秒）。<c>CharacterController.isGrounded</c> 在下坡时会在真假之间逐帧跳，
        /// 直接把它喂给 Animator 就是"走两步抽一次跳跃动画"。真正离地要持续超过这个时长才算数。
        /// </summary>
        private const float GroundedGraceTime = 0.12f;

        /// <summary>量不到净高时，眼高按模型净高的这个比例取（1.60 m 的角色 → 1.34 m，与 Socket_Camera 吻合）。</summary>
        private const float EyeHeightRatioOfModel = 0.84f;

        /// <summary>低于这个净高就不当"人形"看：量到的是零件或塌掉的包围盒。</summary>
        private const float MinPlausibleModelHeight = 0.5f;

        /// <summary>可接受的眼位纵向区间，占模型净高的比例。</summary>
        private const float EyeLowerRatioOfModel = 0.45f;
        private const float EyeUpperRatioOfModel = 1.05f;

        private CharacterController controller;
        private float pitch;
        private float verticalVelocity;

        private Animator animator;
        private bool drivesSpeed;
        private bool drivesAirborne;

        /// <summary>落地宽限余量，初始给满 —— 否则出生时若有一帧没贴地，开场就播一次跳跃。</summary>
        private float groundedGrace = GroundedGraceTime;

        /// <summary>当前是否腾空。只在值真的变化时才写 Animator。</summary>
        private bool airborne;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            if (playerCamera == null)
                playerCamera = GetComponentInChildren<Camera>();

            if (playerCamera == null)
            {
                Debug.LogError("[FirstPersonController] 缺少玩家相机。", this);
                enabled = false;
                return;
            }

            playerCamera.nearClipPlane = nearClipPlane > 0f ? nearClipPlane : DefaultNearClipPlane;

            if (!keepAuthoredEyePosition)
                AlignCameraToEyeSocket();
        }

        private void Start()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            // 第一人称看不到自己的大部分身体，但队友和旁观视角看得到 —— 不驱动动画
            // 角色会以绑定姿势滑行。图元回退（没有 Animator）时两个开关都是 false。
            //
            // 能力探测只做一次：Animator.parameters 每次访问都会拷一份数组出来。
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
        }

        /// <summary>
        /// 把相机搬到美术给的眼位上。
        ///
        /// 两条硬约束：
        ///
        /// 1. **只取位置，不挂骨骼。** 相机保持玩家的直接子节点 —— 眼位插槽挂在 Head 骨骼下，
        ///    真父过去的话 Idle 的呼吸摆动、Mine 的弯腰都会拖着整个视角晃，而 <see cref="Look"/>
        ///    又每帧绝对覆写相机的 localRotation，头和相机会互相打架。取一次坐标，两边互不干扰。
        ///
        /// 2. **只在 Awake 采一次。** 此刻 Animator 还没求值，骨骼停在导入时的站姿上，
        ///    量到的正是造型侧校验过的那一帧，与动画当前播到第几帧无关。
        /// </summary>
        private void AlignCameraToEyeSocket()
        {
            Transform cameraTransform = playerCamera.transform;
            Transform parent = cameraTransform.parent;
            float modelHeight = MeasureModelHeight();

            if (parent != null)
            {
                Transform socket = FindDeep(transform, EyeSocketName);
                if (socket != null)
                {
                    Vector3 localEye = parent.InverseTransformPoint(socket.position);
                    if (IsPlausibleEye(localEye, modelHeight))
                    {
                        cameraTransform.localPosition = localEye;
                        Debug.Log(
                            $"[FirstPersonController] 眼位取自 {EyeSocketName}："
                                + $"local={localEye:F3}（模型净高 {modelHeight:F2} m）",
                            this);
                        return;
                    }

                    Debug.LogWarning(
                        $"[FirstPersonController] {EyeSocketName} 坐标不可信"
                            + $"（local={localEye:F3}，模型净高 {modelHeight:F2} m），改用回退眼高。",
                        this);
                }
                else
                {
                    Debug.LogWarning(
                        $"[FirstPersonController] 玩家层级里没有 {EyeSocketName}（图元回退玩家？），改用回退眼高。",
                        this);
                }
            }

            // 回退：优先按模型净高取比例，量不到净高才用绝对高度。
            float eyeHeight = modelHeight >= MinPlausibleModelHeight
                ? modelHeight * EyeHeightRatioOfModel
                : ResolveFallbackEyeHeight();
            cameraTransform.localPosition = new Vector3(0f, eyeHeight, 0f);
            Debug.Log(
                $"[FirstPersonController] 眼位回退为 y={eyeHeight:F3}（模型净高 {modelHeight:F2} m）",
                this);
        }

        private float ResolveFallbackEyeHeight()
        {
            return fallbackEyeHeight > 0f ? fallbackEyeHeight : DefaultEyeHeight;
        }

        /// <summary>
        /// 模型净高（米）。用渲染包围盒现量，而不是写死 1.8 ——
        /// 造型换个头身比例时，眼位和回退高度都不用跟着改。
        /// </summary>
        private float MeasureModelHeight()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return 0f;

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            Vector3 min = transform.InverseTransformPoint(bounds.min);
            Vector3 max = transform.InverseTransformPoint(bounds.max);
            return Mathf.Abs(max.y - min.y);
        }

        /// <summary>
        /// 眼位量级校验。插槽被改坏（NaN、甩到天上、被塞进脚底）时宁可退回固定眼高，
        /// 也不要让整局视角报废 —— 那种故障在真机上表现为"镜头失联"，比崩溃还难定位。
        /// </summary>
        private static bool IsPlausibleEye(Vector3 localEye, float modelHeight)
        {
            if (float.IsNaN(localEye.x) || float.IsNaN(localEye.y) || float.IsNaN(localEye.z))
                return false;

            if (modelHeight < MinPlausibleModelHeight)
                return false;

            if (localEye.y < modelHeight * EyeLowerRatioOfModel
                || localEye.y > modelHeight * EyeUpperRatioOfModel)
                return false;

            float lateral = modelHeight * 0.5f;
            return Mathf.Abs(localEye.x) <= lateral && Mathf.Abs(localEye.z) <= lateral;
        }

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

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Cursor.lockState = Cursor.lockState == CursorLockMode.Locked ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = Cursor.lockState != CursorLockMode.Locked;
            }

            Look();
            Move();
        }

        private void Look()
        {
            if (Cursor.lockState != CursorLockMode.Locked)
                return;

            float yaw = Input.GetAxis("Mouse X") * mouseSensitivity;
            pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;
            pitch = Mathf.Clamp(pitch, -78f, 78f);

            transform.Rotate(Vector3.up * yaw);
            playerCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        private void Move()
        {
            float speed = Input.GetKey(KeyCode.LeftShift) ? runSpeed : walkSpeed;
            Vector3 input = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
            input = Vector3.ClampMagnitude(input, 1f);
            Vector3 motion = transform.TransformDirection(input) * speed;

            if (controller.isGrounded)
            {
                groundedGrace = GroundedGraceTime;

                if (verticalVelocity < 0f)
                    verticalVelocity = -2f;

                // 空格起跳。美术给的三段跳跃动画（起跳 / 滞空 / 落地）就是为这条路径准备的 ——
                // 在此之前没有任何输入能把角色送离地面，那三段剪辑永远播不到，
                // 而控制器里的跳跃链又恰好在报 bug，所以它一直是个"看得见却摸不着"的分支。
                // 用 GetKey 而不是 GetKeyDown：按住空格落地即再起跳，落地那几帧不会漏输入。
                if (Input.GetKey(KeyCode.Space))
                {
                    verticalVelocity = ResolveJumpSpeed();
                    groundedGrace = 0f; // 这一帧之后就算离地，别让宽限期把起跳吞掉
                }
            }
            else
            {
                groundedGrace -= Time.deltaTime;
            }

            verticalVelocity += gravity * Time.deltaTime;
            motion.y = verticalVelocity;
            controller.Move(motion * Time.deltaTime);

            if (drivesSpeed)
            {
                // 用 CharacterController 的实际速度而非输入量：撞墙或滑行时输入速度
                // 仍是非零，动画却该切回站立，只有实际位移才反映真实移动。
                Vector3 flat = controller.velocity;
                flat.y = 0f;
                animator.SetFloat(AnimatorParameters.Speed, flat.magnitude);
            }

            // "腾空"要同时满足：这一帧真的没贴地，且已经持续超过宽限期。
            // 只用 isGrounded 的话，走下沙丘时它逐帧在真假之间跳，
            // 表现是走两步就抽一次起跳动作 —— 那是肉眼可见的抽搐，不是抖动。
            SetAirborne(!controller.isGrounded && groundedGrace <= 0f);
        }

        /// <summary>
        /// 写"是否腾空"—— 跳跃动画唯一的输入。只在值真的变化时才过 Animator，
        /// 免得每帧无谓地打一次参数（同时也让日志里每次起跳只留一行）。
        /// </summary>
        private void SetAirborne(bool value)
        {
            if (!drivesAirborne || airborne == value)
                return;

            airborne = value;
            animator.SetBool(AnimatorParameters.Airborne, value);

            if (value)
                Debug.Log("[FirstPersonController] 离地，跳跃动画链开始。", this);
        }

        private float ResolveJumpSpeed()
        {
            // 0 当"未设置"：本字段是后加的，老场景反序列化出来是 0，
            // 而 0 恰好等于"完全跳不起来"—— 会表现成"空格没反应"且不报错。
            return jumpSpeed > 0f ? jumpSpeed : DefaultJumpSpeed;
        }
    }
}
