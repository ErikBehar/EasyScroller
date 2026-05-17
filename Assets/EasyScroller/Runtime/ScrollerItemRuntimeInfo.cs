using UnityEngine;
using UnityEngine.Events;
public class ScrollerItemRuntimeInfo : MonoBehaviour
{
    [SerializeField, Tooltip("Logical item index used by ScrollerManager.")]
    private int logical_index;
    [SerializeField, Tooltip("Original source index configured at initialization for this logical item.")]
    private int original_index;
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

    public int LogicalIndex => logical_index;
    public int OriginalIndex => original_index;
    public RectTransform WrapperRect => wrapper_rect;
    public RectTransform ContentRect => content_rect;
    public ScrollerManager Manager => manager;
    public bool IsCentered => is_centered;
    public UnityEvent<bool> OnCenteredStateChanged => on_centered_state_changed;

    public void Initialize(int logicalIndex, int originalIndex, RectTransform wrapperRect, RectTransform contentRect)
    {
        logical_index = logicalIndex;
        original_index = originalIndex;
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

    public void SetOriginalIndex(int originalIndex)
    {
        original_index = originalIndex;
    }

    public void SetManager(ScrollerManager scrollerManager)
    {
        manager = scrollerManager;
    }
}
