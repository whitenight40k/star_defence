using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 可采集的资源点。
    ///
    /// 这里刻意**不加** [RequireComponent(typeof(Collider))]：Collider 是抽象类，
    /// Unity 无法自动补一个具体碰撞体，AddComponent 会直接失败并返回 null，
    /// 报 "Add required component of type 'BoxCollider' or 'CapsuleCollider' or ... first"。
    /// 更根本的问题是语义不对 —— 美术资产的碰撞体挂在子网格上，本体根节点本来就没有，
    /// 属性即使能生效也会强制多出一个重复碰撞体。
    ///
    /// 命中逻辑在 PlayerToolController 里走的是 hit.collider.GetComponentInParent&lt;ResourceNode&gt;，
    /// 所以碰撞体在子物体上完全够用。这里只在运行时从层级里解析出来备用。
    /// </summary>
    public class ResourceNode : MonoBehaviour
    {
        public ResourceType resourceType = ResourceType.Metal;
        public int amountRemaining = 240;
        public int amountPerHit = 8;
        public float respawnAfterSeconds = 45f;

        private float respawnTimer;
        private Renderer cachedRenderer;
        private Collider cachedCollider;

        public bool IsDepleted => amountRemaining <= 0;

        private void Awake()
        {
            cachedRenderer = GetComponentInChildren<Renderer>();

            // 碰撞体可能在本体（图元回退路径）或子网格上（美术 Prefab），两处都要找一遍。
            cachedCollider = GetComponent<Collider>();
            if (cachedCollider == null)
                cachedCollider = GetComponentInChildren<Collider>();
        }

        private void Update()
        {
            if (!IsDepleted)
                return;

            respawnTimer -= Time.deltaTime;
            if (respawnTimer <= 0f)
            {
                amountRemaining = 160;
                SetVisible(true);
            }
        }

        public int Gather(float multiplier = 1f)
        {
            if (IsDepleted)
                return 0;

            int amount = Mathf.Min(amountRemaining, Mathf.Max(1, Mathf.RoundToInt(amountPerHit * multiplier)));
            amountRemaining -= amount;

            if (IsDepleted)
            {
                respawnTimer = respawnAfterSeconds;
                SetVisible(false);
            }

            return amount;
        }

        private void SetVisible(bool visible)
        {
            if (cachedRenderer != null)
                cachedRenderer.enabled = visible;
            if (cachedCollider != null)
                cachedCollider.enabled = visible;
        }
    }
}
