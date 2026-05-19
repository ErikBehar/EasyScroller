using UnityEngine;
using UnityEngine.Events;

namespace EasyScroller
{
    public class ScrollerItemRuntimeInfo : MonoBehaviour
    {
        [SerializeField, Tooltip("Logical item index used by ScrollerManager.")]
        private int logical_index;
        [SerializeField, Tooltip("Stable data index for this item's content (persists across enable/disable and pool reuse).")]
        private int data_index;
        [SerializeField, Tooltip("Wrapper RectTransform for this pooled runtime item.")]
        private RectTransform wrapper_rect;
        [SerializeField, Tooltip("Content RectTransform (instantiated prefab root).")]
        private RectTransform content_rect;
        [SerializeField, Tooltip("Owning scroller manager that created this runtime item.")]
        private ScrollerManager manager;
        [SerializeField, Tooltip("True when this item is currently the centered/highlighted item.")]
        private bool is_centered;
        [SerializeField, Tooltip("Invoked when centered state changes. Parameter is new centered state.")]
        private UnityEvent<bool> on_centered_state_changed = new UnityEvent<bool>();
        [SerializeField, Tooltip("Invoked when this visual slot is rebound to a different logical/data item.")]
        private UnityEvent on_content_refresh_requested = new UnityEvent();

        public int LogicalIndex => logical_index;
        /// <summary>Stable index into your data source; does not change when the runtime list is reordered.</summary>
        public int DataIndex => data_index;
        /// <summary>Alias for <see cref="DataIndex"/>.</summary>
        public int OriginalIndex => data_index;
        public RectTransform WrapperRect => wrapper_rect;
        public RectTransform ContentRect => content_rect;
        public ScrollerManager Manager => manager;
        public bool IsCentered => is_centered;
        public UnityEvent<bool> OnCenteredStateChanged => on_centered_state_changed;
        public UnityEvent OnContentRefreshRequested => on_content_refresh_requested;

        public void Initialize(int logicalIndex, int dataIndex, RectTransform wrapperRect, RectTransform contentRect)
        {
            logical_index = logicalIndex;
            data_index = dataIndex;
            wrapper_rect = wrapperRect;
            content_rect = contentRect;
        }

        public bool RequestRemoveSelf()
        {
            return manager != null && manager.RemoveItemByRuntimeInfo(this);
        }

        public void SetCentered(bool isCentered)
        {
            if (is_centered == isCentered)
            {
                return;
            }

            is_centered = isCentered;
            on_centered_state_changed.Invoke(is_centered);
        }

        public void SetLogicalIndex(int logicalIndex)
        {
            logical_index = logicalIndex;
        }

        public void SetDataIndex(int dataIndex)
        {
            data_index = dataIndex;
        }

        public void SetOriginalIndex(int dataIndex)
        {
            SetDataIndex(dataIndex);
        }

        public void SetManager(ScrollerManager scrollerManager)
        {
            manager = scrollerManager;
        }

        public void NotifyContentRefreshRequested()
        {
            on_content_refresh_requested.Invoke();
        }
    }
}
