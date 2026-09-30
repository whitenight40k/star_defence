using System.Collections.Generic;
using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// 场景中活跃实体的集合，以资产形式存在。
    ///
    /// 用它替代"某个管理器里的 List&lt;T&gt;"：注册方只认识资产，查询方也只认识资产，
    /// 两边不需要互相持有引用。也让"谁在场上"这件事不再绑定在某一个 Manager 上。
    /// </summary>
    public abstract class RuntimeSet<T> : ScriptableObject where T : Object
    {
        /// <summary>
        /// 刻意用 NonSerialized：这是运行时状态，不应写进 .asset 文件。
        /// 否则场景引用会被序列化进资产，造成跨局残留和资产脏写。
        /// 域重载后列表自然清空，正是我们要的语义。
        /// </summary>
        [System.NonSerialized] public List<T> items = new List<T>();

        public IReadOnlyList<T> Items => items;
        public int Count => items.Count;

        public void Add(T item)
        {
            if (IsNull(item) || items.Contains(item))
                return;

            items.Add(item);
        }

        public void Remove(T item)
        {
            if (IsNull(item))
                return;

            items.Remove(item);
        }

        public void Clear() => items.Clear();

        /// <summary>
        /// 清掉已被销毁的条目。Unity 的"假空"对象（Destroy 之后引用仍在）需要
        /// 遍历时才能识别，所以读列表前扫一遍。返回清掉的个数。
        /// </summary>
        public int Prune()
        {
            int removed = 0;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (!IsNull(items[i]))
                    continue;

                items.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// T 被约束为 UnityEngine.Object，这里才能用 Unity 重载过的 == 判断已销毁对象。
        /// 泛型直接写 item == null 走的是纯引用比较，抓不到假空。
        /// </summary>
        private static bool IsNull(T item) => (Object)item == null;

        private void OnEnable() => items.Clear();
    }

    [CreateAssetMenu(menuName = "Star Defense/Runtime Sets/Enemy Units", fileName = "Set_EnemyUnits")]
    public class EnemyUnitRuntimeSet : RuntimeSet<EnemyUnit> { }

    [CreateAssetMenu(menuName = "Star Defense/Runtime Sets/Buildables", fileName = "Set_Buildables")]
    public class BuildableRuntimeSet : RuntimeSet<Buildable> { }
}
