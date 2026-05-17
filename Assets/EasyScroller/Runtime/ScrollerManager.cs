using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public enum ScrollerItemSourceMode
{
    PrefabList,
    SinglePrefabWithCount
}

public class ScrollerManager : MonoBehaviour
{
    [Header("Item Source")]
    [SerializeField, Tooltip("Initialization mode: use prefab_list, or duplicate one prefab for a fixed item count.")]
    private ScrollerItemSourceMode item_source_mode = ScrollerItemSourceMode.PrefabList;
    [Tooltip("Prefabs used by the scroller in their logical repeating order.")]
    public List<GameObject> prefab_list = new List<GameObject>();
    [SerializeField, Tooltip("Prefab used when item_source_mode is SinglePrefabWithCount.")]
    private GameObject single_prefab;
    [SerializeField, Min(0), Tooltip("Number of logical items to create from single_prefab when single-prefab mode is active.")]
    private int single_prefab_count = 0;

    [Header("Spacing + Visibility")]
    [SerializeField, Tooltip("Primary axis used for scrolling and layout.")]
    private ScrollerAxis scroll_axis = ScrollerAxis.Vertical;
    [SerializeField, Tooltip("Additional edge-to-edge gap between neighboring items on the primary axis.")]
    private float item_gap = 100f;
    [SerializeField, Tooltip("Extra offscreen items kept active before pooling to reduce pop-in.")]
    private int buffer_item_count = 2;
    [SerializeField, Tooltip("Fallback half-extent used when the scroll rect size is not resolved yet.")]
    private float fallback_visible_half_extent = 250f;

    [Header("Drag + Inertia")]
    [SerializeField, Tooltip("How quickly inertial velocity decays toward zero.")]
    private float inertia_damping = 8f;

    [Header("Snap Spring")]
    [SerializeField, Tooltip("Enable or disable automatic snapping to the nearest item.")]
    private bool enable_snapping = true;
    [SerializeField, Tooltip("When speed falls below this, spring snapping to nearest center starts.")]
    private float snap_velocity_threshold = 40f;
    [SerializeField, Tooltip("Seconds to smoothly settle to the snap target.")]
    private float snap_smooth_time = 0.14f;
    [SerializeField, Tooltip("Maximum snap speed in anchored units/sec.")]
    private float snap_max_speed = 5000f;
    [SerializeField, Tooltip("Distance threshold where we hard-set to target and stop snap.")]
    private float snap_position_epsilon = 0.25f;
    [SerializeField, Tooltip("Dead-zone around last settled item to prevent target flip-flopping.")]
    private float snap_switch_dead_zone = 20f;

    [Header("Visual Scaling")]
    [SerializeField, Tooltip("Enable or disable distance-based scaling (edge_scale to center_scale).")]
    private bool enable_distance_scaling = true;
    [SerializeField, Tooltip("Apply additional scale to the currently centered item (independent of distance scaling).")]
    private bool enable_center_highlight_scaling = true;
    [SerializeField, Tooltip("Base scale used for items near the center of the scroller.")]
    private float center_scale = 1.15f;
    [SerializeField, Tooltip("Base scale used for items near the edge of the visible window.")]
    private float edge_scale = 0.85f;
    [SerializeField, Tooltip("Extra scale added to the currently centered/highlighted item.")]
    private float highlight_scale_boost = 0.1f;
    [SerializeField, Tooltip("Lerp speed used when transitioning item scale values.")]
    private float scale_lerp_speed = 12f;

    [Header("Runtime Size Refresh")]
    [SerializeField, Tooltip("If false, skip runtime size re-measurement (use when item sizes are static).")]
    private bool enable_runtime_size_checks = true;
    [SerializeField, Tooltip("Re-measure item sizes every N Update ticks. Use 1 for every frame.")]
    private int size_refresh_tick_interval = 10;
    [SerializeField, Tooltip("Minimum absolute size change required before spacing is rebuilt.")]
    private float size_refresh_epsilon = 0.25f;
    [SerializeField, Tooltip("How quickly measured item sizes are allowed to change (units/sec).")]
    private float spacing_size_response_speed = 1200f;

    [Header("Relayout")]
    [SerializeField, Tooltip("Smoothly resolve layout after structural changes (delete/enable/disable).")]
    private bool smooth_relayout_on_structure_change = true;
    [SerializeField, Tooltip("How quickly items ease toward new solved positions during relayout.")]
    private float relayout_lerp_speed = 10f;
    [SerializeField, Tooltip("Distance considered settled during relayout smoothing.")]
    private float relayout_settle_epsilon = 0.5f;
    [SerializeField, Tooltip("Hide items on initial build until layout and spacing have stabilized.")]
    private bool hide_items_until_initial_settle = true;

    private class ItemState
    {
        public readonly GameObject Prefab;
        public readonly int SourcePrefabIndex;
        public readonly int OriginalItemIndex;
        public float Height;
        public bool Enabled = true;

        public ItemState(GameObject prefab, float height, int sourcePrefabIndex, int originalItemIndex)
        {
            Prefab = prefab;
            Height = height;
            SourcePrefabIndex = sourcePrefabIndex;
            OriginalItemIndex = originalItemIndex;
        }
    }

    private class VisualItem
    {
        public int AbsoluteOrderIndex;
        public int LogicalIndex;
        public RectTransform RectTransform;
        public CanvasGroup CanvasGroup;
        public ScrollerItemRuntimeInfo RuntimeInfo;
        public Vector3 BaseLocalScale;
        public bool SnapToTargetOnPrepare;
    }

    private readonly List<ItemState> _items = new List<ItemState>();
    private readonly Dictionary<int, VisualItem> _activeVisualsByOrder = new Dictionary<int, VisualItem>();
    private readonly Dictionary<int, Stack<VisualItem>> _pooledVisualsByLogicalIndex = new Dictionary<int, Stack<VisualItem>>();
    private readonly List<int> _enabledIndices = new List<int>();
    private readonly List<float> _enabledPrefixPositions = new List<float>();
    private readonly List<int> _cleanupKeys = new List<int>();
    private readonly List<int> _desiredOrders = new List<int>();
    private readonly HashSet<int> _desiredOrderSet = new HashSet<int>();
    private readonly Dictionary<int, float> _desiredOrderCenterPositions = new Dictionary<int, float>();
    private readonly Dictionary<int, float> _measuredOrderSizes = new Dictionary<int, float>();
    private readonly Vector3[] _worldCornersBuffer = new Vector3[4];

    private float _scrollOffset;
    private float _scrollVelocity;
    private bool _isDragging;
    private int _centeredLogicalIndex = -1;
    private RectTransform _containerRect;
    private float _enabledCycleLength;
    private float _enabledAverageSpan = 1f;
    private int _sizeRefreshTickCounter;
    private float _snapVelocity;
    private bool _hasSnapTarget;
    private int _snapTargetOrder;
    private float _snapTargetOffset;
    private bool _snapTargetLockedUntilUserInput;
    private bool _hasSettledOrder;
    private int _settledOrder;
    private bool _relayoutSmoothingActive;
    private bool _hasProgrammaticStepAnchor;
    private int _programmaticStepOrder;
    private bool _pendingInitialReveal;
    private int _currentCenterOrder;

    public ScrollerAxis ScrollAxis => scroll_axis;

    void Start()
    {
        _containerRect = transform as RectTransform;
        if (_containerRect == null)
        {
            Debug.LogError("ScrollerManager requires a RectTransform on the same GameObject.");
            enabled = false;
            return;
        }

        BuildItemState();
        if (_items.Count == 0)
        {
            Debug.LogWarning("ScrollerManager has no prefabs configured. Call SetPrefabs(...) during debug mode.");
        }
        RefreshEnabledIndices();
        BeginRelayoutSmoothing();
        _pendingInitialReveal = hide_items_until_initial_settle;
        SyncVisibleWindow();
    }

    public void SetPrefabs(IList<GameObject> prefabs)
    {
        prefab_list.Clear();
        if (prefabs != null)
        {
            for (int i = 0; i < prefabs.Count; i++)
            {
                if (prefabs[i] != null)
                {
                    prefab_list.Add(prefabs[i]);
                }
            }
        }

        BuildItemState();
        RefreshEnabledIndices();
        BeginRelayoutSmoothing();
        _pendingInitialReveal = hide_items_until_initial_settle;
        SyncVisibleWindow();
    }

    void Update()
    {
        if (_enabledIndices.Count == 0)
        {
            DeactivateAllActiveVisuals();
            return;
        }

        float dt = Time.deltaTime;

        bool hasVelocity = Mathf.Abs(_scrollVelocity) > 0.0001f;
        if (!_isDragging && hasVelocity)
        {
            _scrollOffset += _scrollVelocity * dt;
            _scrollVelocity = Mathf.Lerp(_scrollVelocity, 0f, inertia_damping * dt);
        }

        if (enable_snapping && !_isDragging && Mathf.Abs(_scrollVelocity) < snap_velocity_threshold)
        {
            if (!_hasSnapTarget)
            {
                int candidateOrder;
                if (_snapTargetLockedUntilUserInput)
                {
                    candidateOrder = _snapTargetOrder;
                }
                else
                {
                    candidateOrder = GetNearestOrderToOffset(_scrollOffset);
                    if (_hasSettledOrder)
                    {
                        float settledOffset = GetOrderCenterPosition(_settledOrder);
                        if (Mathf.Abs(_scrollOffset - settledOffset) <= snap_switch_dead_zone)
                        {
                            candidateOrder = _settledOrder;
                        }
                    }
                }

                _snapTargetOrder = candidateOrder;
                _snapTargetOffset = GetOrderCenterPosition(_snapTargetOrder);
                _hasSnapTarget = true;
                _snapTargetLockedUntilUserInput = true;
            }

            _scrollOffset = Mathf.SmoothDamp(_scrollOffset, _snapTargetOffset, ref _snapVelocity, snap_smooth_time, snap_max_speed, dt);

            // Prevent inertia from fighting snap while in settle mode.
            _scrollVelocity = 0f;

            if (Mathf.Abs(_snapTargetOffset - _scrollOffset) <= snap_position_epsilon && Mathf.Abs(_snapVelocity) < snap_velocity_threshold)
            {
                // Soft settle: stop snapping when close enough, without forcing a final position jump.
                _snapVelocity = 0f;
                _hasSettledOrder = true;
                _settledOrder = _snapTargetOrder;
                _hasSnapTarget = false;
            }
        }
        else
        {
            _hasSnapTarget = false;
            _snapVelocity = 0f;
        }

        bool shouldUpdateWindow = _isDragging || Mathf.Abs(_scrollVelocity) > 0.0001f;
        if (!shouldUpdateWindow && Mathf.Abs(_scrollVelocity) < 0.005f)
        {
            _scrollVelocity = 0f;
        }

        // First pass: apply movement + scaling to current visuals.
        SyncVisibleWindow();

        // Second step: measure after scaling, then rebuild spacing if needed.
        bool spacingChanged = RefreshItemSizesOnTick();
        if (spacingChanged)
        {
            // Second pass in same frame so updated spacing is applied immediately.
            SyncVisibleWindow();
        }

        if (_pendingInitialReveal && !_relayoutSmoothingActive && !spacingChanged)
        {
            SetVisibilityForVisibleVisuals(true);
            _pendingInitialReveal = false;
            BroadcastCenteredStateForCurrentOrder();
        }
    }

    public void SetItemEnabled(int itemIndex, bool enabled)
    {
        if (itemIndex < 0 || itemIndex >= _items.Count)
        {
            return;
        }

        if (_items[itemIndex].Enabled == enabled)
        {
            return;
        }

        _items[itemIndex].Enabled = enabled;
        RefreshEnabledIndices();
        BeginRelayoutSmoothing();

        if (_enabledIndices.Count == 0)
        {
            DeactivateAllActiveVisuals();
            _centeredLogicalIndex = -1;
            return;
        }

        SyncVisibleWindow();
    }

    public int GetCenteredLogicalIndex()
    {
        return _centeredLogicalIndex;
    }

    // Kept as void wrapper so it can be easily wired to Unity Events/UI Buttons.
    public void AddItemNoRet()
    {
        AddItemAtRuntime();
    }

    // Kept as void wrapper so it can be easily wired to Unity Events/UI Buttons.
    public void AddItemWithPrefabNoRet(GameObject prefab)
    {
        AddItemAtRuntime(prefab);
    }

    public bool AddItemAtRuntime(GameObject prefab)
    {
        if (item_source_mode == ScrollerItemSourceMode.SinglePrefabWithCount)
        {
            return AddItemAtRuntime();
        }

        if (prefab == null)
        {
            Debug.LogWarning("AddItemAtRuntime(prefab) requires a non-null prefab in PrefabList mode.");
            return false;
        }

        int sourcePrefabIndex = prefab_list.Count;
        prefab_list.Add(prefab);
        _items.Add(new ItemState(prefab, ResolvePrefabPrimarySize(prefab), sourcePrefabIndex, sourcePrefabIndex));
        RefreshEnabledIndices();
        BeginRelayoutSmoothing();
        SyncVisibleWindow();
        return true;
    }

    public bool AddItemAtRuntime()
    {
        if (item_source_mode == ScrollerItemSourceMode.PrefabList)
        {
            Debug.LogWarning("AddItemAtRuntime() requires a prefab argument in PrefabList mode.");
            return false;
        }

        if (single_prefab == null)
        {
            Debug.LogWarning("single_prefab is null; cannot add item in SinglePrefabWithCount mode.");
            return false;
        }

        int nextOriginalIndex = GetNextOriginalItemIndex();
        _items.Add(new ItemState(single_prefab, ResolvePrefabPrimarySize(single_prefab), -1, nextOriginalIndex));
        single_prefab_count = Mathf.Max(0, single_prefab_count) + 1;
        RefreshEnabledIndices();
        BeginRelayoutSmoothing();
        SyncVisibleWindow();
        return true;
    }

    public bool RemoveItemAtRuntime(int itemIndex)
    {
        if (itemIndex < 0 || itemIndex >= _items.Count)
        {
            return false;
        }

        if (!_items[itemIndex].Enabled)
        {
            return false;
        }

        _items[itemIndex].Enabled = false;
        PurgeVisualsForLogicalIndex(itemIndex);
        RefreshEnabledIndices();
        BeginRelayoutSmoothing();

        if (_enabledIndices.Count == 0)
        {
            DeactivateAllActiveVisuals();
            _centeredLogicalIndex = -1;
            return true;
        }

        SyncVisibleWindow();
        return true;
    }

    public bool RemoveItemByPrefabListIndex(int prefabListIndex)
    {
        int runtimeIndex = -1;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].SourcePrefabIndex == prefabListIndex)
            {
                runtimeIndex = i;
                break;
            }
        }

        if (runtimeIndex < 0)
        {
            return false;
        }

        return RemoveItemAtRuntime(runtimeIndex);
    }

    public bool RemoveItemByRuntimeInfo(ScrollerItemRuntimeInfo runtimeInfo)
    {
        if (runtimeInfo == null)
        {
            return false;
        }

        return RemoveItemAtRuntime(runtimeInfo.LogicalIndex);
    }

    public bool MoveScrollerByDirection(int direction, int steps = 1)
    {
        if (_enabledIndices.Count == 0 || direction == 0 || steps <= 0)
        {
            return false;
        }

        int sign = direction > 0 ? 1 : -1;
        int baseOrder;
        bool userDrivenMotion = _isDragging || Mathf.Abs(_scrollVelocity) > 0.001f;
        if (userDrivenMotion)
        {
            // Re-anchor to what is currently closest when motion comes from drag/inertia.
            baseOrder = GetNearestOrderToOffset(_scrollOffset);
            _hasProgrammaticStepAnchor = false;
        }
        else if (_hasProgrammaticStepAnchor)
        {
            baseOrder = _programmaticStepOrder;
        }
        else if (_hasSnapTarget)
        {
            baseOrder = _snapTargetOrder;
        }
        else if (_hasSettledOrder)
        {
            baseOrder = _settledOrder;
        }
        else
        {
            baseOrder = GetNearestOrderToOffset(_scrollOffset);
        }

        int targetOrder = baseOrder + (sign * steps);
        float targetOffset = GetOrderCenterPosition(targetOrder);
        _programmaticStepOrder = targetOrder;
        _hasProgrammaticStepAnchor = true;

        _scrollVelocity = 0f;
        _snapVelocity = 0f;

        if (enable_snapping)
        {
            _snapTargetOrder = targetOrder;
            _snapTargetOffset = targetOffset;
            _hasSnapTarget = true;
            _snapTargetLockedUntilUserInput = true;
        }
        else
        {
            _scrollOffset = targetOffset;
            _hasSettledOrder = true;
            _settledOrder = targetOrder;
        }

        return true;
    }

    // Axis-neutral direction helper: positive along the configured primary axis.
    public bool MovePositiveDirection(int steps = 1)
    {
        return MoveScrollerByDirection(1, steps);
    }

    // Axis-neutral direction helper: negative along the configured primary axis.
    public bool MoveNegativeDirection(int steps = 1)
    {
        return MoveScrollerByDirection(-1, steps);
    }

    // Kept as void wrappers so they can be easily wired to Unity Events/UI Buttons.
    public void MoveUpNoRet(int steps = 1)
    {
        MoveUp(steps);
    }

    // Kept as void wrappers so they can be easily wired to Unity Events/UI Buttons.
    public void MoveDownNoRet(int steps = 1)
    {
        MoveDown(steps);
    }

    public bool MoveUp(int steps = 1)
    {
        return MovePositiveDirection(steps);
    }

    public bool MoveDown(int steps = 1)
    {
        return MoveNegativeDirection(steps);
    }

    private void BuildItemState()
    {
        _items.Clear();

        if (item_source_mode == ScrollerItemSourceMode.SinglePrefabWithCount)
        {
            if (single_prefab == null)
            {
                return;
            }

            int count = Mathf.Max(0, single_prefab_count);
            float prefabSize = ResolvePrefabPrimarySize(single_prefab);
            for (int i = 0; i < count; i++)
            {
                _items.Add(new ItemState(single_prefab, prefabSize, -1, i));
            }

            return;
        }

        for (int i = 0; i < prefab_list.Count; i++)
        {
            GameObject prefab = prefab_list[i];
            if (prefab != null)
            {
                _items.Add(new ItemState(prefab, ResolvePrefabPrimarySize(prefab), i, i));
            }
        }
    }

    private int GetNextOriginalItemIndex()
    {
        int next = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            next = Mathf.Max(next, _items[i].OriginalItemIndex + 1);
        }

        return next;
    }

    private void RefreshEnabledIndices()
    {
        _enabledIndices.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Enabled)
            {
                _enabledIndices.Add(i);
            }
        }

        RebuildEnabledSpacingData();
    }

    private float ResolvePrefabPrimarySize(GameObject prefab)
    {
        float intrinsic = ResolvePrefabIntrinsicPrimarySize(prefab);
        float baseScale = ScrollerAxisAdapter.GetPrimaryScale(prefab.transform.localScale, scroll_axis);
        if (baseScale < 0.0001f)
        {
            baseScale = 1f;
        }

        return intrinsic * baseScale;
    }

    private float ResolvePrefabIntrinsicPrimarySize(GameObject prefab)
    {
        LayoutElement layout = prefab.GetComponent<LayoutElement>();
        if (layout != null)
        {
            float preferred = scroll_axis == ScrollerAxis.Vertical
                ? layout.preferredHeight
                : layout.preferredWidth;
            if (preferred > 0.01f)
            {
                return preferred;
            }
        }

        RectTransform prefabRect = prefab.GetComponent<RectTransform>();
        float sizeDeltaPrimary = ScrollerAxisAdapter.GetSizeDeltaPrimary(prefabRect, scroll_axis);
        if (sizeDeltaPrimary > 0.01f)
        {
            return sizeDeltaPrimary;
        }

        return 100f;
    }

    private bool RefreshItemSizesOnTick()
    {
        if (!enable_runtime_size_checks)
        {
            return false;
        }

        if (_items.Count == 0)
        {
            return false;
        }

        bool isMoving = _isDragging || Mathf.Abs(_scrollVelocity) > 0.001f;
        int tickInterval = Mathf.Max(1, size_refresh_tick_interval);
        if (!isMoving)
        {
            _sizeRefreshTickCounter++;
            if (_sizeRefreshTickCounter < tickInterval)
            {
                return false;
            }
        }

        _sizeRefreshTickCounter = 0;
        bool anyChanged = false;
        float maxStep = Mathf.Max(1f, spacing_size_response_speed) * Time.deltaTime;
        foreach (KeyValuePair<int, VisualItem> kvp in _activeVisualsByOrder)
        {
            int order = kvp.Key;
            VisualItem visual = kvp.Value;
            if (visual == null || visual.RectTransform == null)
            {
                continue;
            }

            int logicalIndex = ResolveLogicalIndexFromOrder(order);
            if (logicalIndex < 0 || logicalIndex >= _items.Count)
            {
                continue;
            }

            float fallback = ResolvePrefabPrimarySize(_items[logicalIndex].Prefab);
            float latestHeight = MeasureVisualPrimarySizeInContainer(visual);
            if (latestHeight <= 0.01f)
            {
                latestHeight = fallback;
            }

            if (!_measuredOrderSizes.TryGetValue(order, out float current))
            {
                _measuredOrderSizes[order] = latestHeight;
                anyChanged = true;
                continue;
            }

            float next = Mathf.MoveTowards(current, latestHeight, maxStep);
            if (Mathf.Abs(next - current) > size_refresh_epsilon)
            {
                _measuredOrderSizes[order] = next;
                anyChanged = true;
            }
            else if (Mathf.Abs(latestHeight - current) <= size_refresh_epsilon)
            {
                // Snap tiny drift in cache to avoid endless micro-updates.
                _measuredOrderSizes[order] = latestHeight;
            }
        }

        if (anyChanged)
        {
            RebuildEnabledSpacingData();
            return true;
        }

        return false;
    }

    private float GetVisibleHalfExtent()
    {
        return ScrollerAxisAdapter.GetRectHalfSize(_containerRect, scroll_axis, fallback_visible_half_extent);
    }

    private void RebuildEnabledSpacingData()
    {
        _enabledPrefixPositions.Clear();
        _enabledCycleLength = 0f;
        _enabledAverageSpan = 1f;

        int count = _enabledIndices.Count;
        if (count == 0)
        {
            return;
        }

        _enabledPrefixPositions.Add(0f);

        for (int i = 0; i < count; i++)
        {
            int currentLogical = _enabledIndices[i];
            int nextLogical = _enabledIndices[(i + 1) % count];

            float currentHeight = _items[currentLogical].Height;
            float nextHeight = _items[nextLogical].Height;
            float span = (0.5f * currentHeight) + (0.5f * nextHeight) + item_gap;
            span = Mathf.Max(1f, span);

            _enabledCycleLength += span;

            if (i < count - 1)
            {
                _enabledPrefixPositions.Add(_enabledPrefixPositions[i] + span);
            }
        }

        _enabledAverageSpan = Mathf.Max(1f, _enabledCycleLength / count);
    }

    private void PurgeVisualsForLogicalIndex(int logicalIndex)
    {
        _cleanupKeys.Clear();
        foreach (KeyValuePair<int, VisualItem> kvp in _activeVisualsByOrder)
        {
            if (kvp.Value != null && kvp.Value.LogicalIndex == logicalIndex)
            {
                _cleanupKeys.Add(kvp.Key);
            }
        }

        for (int i = 0; i < _cleanupKeys.Count; i++)
        {
            int order = _cleanupKeys[i];
            if (_activeVisualsByOrder.TryGetValue(order, out VisualItem visual) &&
                visual != null &&
                visual.RectTransform != null)
            {
                Destroy(visual.RectTransform.gameObject);
            }
            _activeVisualsByOrder.Remove(order);
            _measuredOrderSizes.Remove(order);
        }

        if (_pooledVisualsByLogicalIndex.TryGetValue(logicalIndex, out Stack<VisualItem> pool))
        {
            foreach (VisualItem visual in pool)
            {
                if (visual != null && visual.RectTransform != null)
                {
                    Destroy(visual.RectTransform.gameObject);
                }
            }

            _pooledVisualsByLogicalIndex.Remove(logicalIndex);
        }
    }

    private void BeginRelayoutSmoothing()
    {
        _relayoutSmoothingActive = smooth_relayout_on_structure_change;
    }

    private void SetVisibilityForVisibleVisuals(bool isVisible)
    {
        foreach (KeyValuePair<int, VisualItem> kvp in _activeVisualsByOrder)
        {
            VisualItem visual = kvp.Value;
            if (visual != null && visual.CanvasGroup != null)
            {
                visual.CanvasGroup.alpha = isVisible ? 1f : 0f;
                visual.CanvasGroup.interactable = isVisible;
                visual.CanvasGroup.blocksRaycasts = isVisible;
            }
        }
    }

    private void BroadcastCenteredStateForCurrentOrder()
    {
        foreach (KeyValuePair<int, VisualItem> kvp in _activeVisualsByOrder)
        {
            VisualItem visual = kvp.Value;
            if (visual != null && visual.RuntimeInfo != null)
            {
                bool isCentered = kvp.Key == _currentCenterOrder;
                visual.RuntimeInfo.SetCentered(isCentered);
            }
        }
    }

    public void NotifyPointerDown()
    {
        _scrollVelocity = 0f;
    }

    public void BeginUserDrag()
    {
        _isDragging = true;
        _scrollVelocity = 0f;
        _snapVelocity = 0f;
        _hasSnapTarget = false;
        _snapTargetLockedUntilUserInput = false;
        _hasProgrammaticStepAnchor = false;
    }

    public void ApplyUserDragDelta(float deltaUnits, float deltaTime)
    {
        _scrollOffset += deltaUnits;
        _scrollVelocity = deltaTime > 0f ? (deltaUnits / deltaTime) : 0f;
    }

    public void EndUserDrag()
    {
        _isDragging = false;
    }

    private float GetOrderCenterPosition(int absoluteOrder)
    {
        int count = _enabledIndices.Count;
        if (count <= 0)
        {
            return 0f;
        }

        if (count == 1)
        {
            float singleSpan = (_items[_enabledIndices[0]].Height + item_gap);
            singleSpan = Mathf.Max(1f, singleSpan);
            return absoluteOrder * singleSpan;
        }

        int cycleIndex = FloorDiv(absoluteOrder, count);
        int inCycle = Mod(absoluteOrder, count);
        float prefix = _enabledPrefixPositions[inCycle];
        return (cycleIndex * _enabledCycleLength) + prefix;
    }

    private int GetNearestOrderToOffset(float offset)
    {
        int count = _enabledIndices.Count;
        if (count <= 0)
        {
            return 0;
        }

        if (count == 1)
        {
            float singleSpan = Mathf.Max(1f, _items[_enabledIndices[0]].Height + item_gap);
            return Mathf.RoundToInt(offset / singleSpan);
        }

        float bestDistance = float.MaxValue;
        int bestOrder = 0;

        for (int i = 0; i < count; i++)
        {
            float basePos = _enabledPrefixPositions[i];
            int cycle = Mathf.RoundToInt((offset - basePos) / _enabledCycleLength);
            int candidate = (cycle * count) + i;
            float candidatePos = (cycle * _enabledCycleLength) + basePos;
            float distance = Mathf.Abs(candidatePos - offset);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestOrder = candidate;
            }
        }

        return bestOrder;
    }

    private float GetOrderPrimarySize(int order)
    {
        if (_measuredOrderSizes.TryGetValue(order, out float measuredHeight) && measuredHeight > 0.01f)
        {
            return measuredHeight;
        }

        int logicalIndex = ResolveLogicalIndexFromOrder(order);
        if (logicalIndex >= 0 && logicalIndex < _items.Count)
        {
            return _items[logicalIndex].Height;
        }

        return 100f;
    }

    private float GetSpanToNextOrder(int order)
    {
        float currentHeight = GetOrderPrimarySize(order);
        float nextHeight = GetOrderPrimarySize(order + 1);
        // item_gap is the explicit edge-to-edge gap between neighbors.
        float span = (0.5f * currentHeight) + (0.5f * nextHeight) + item_gap;
        return Mathf.Max(1f, span);
    }

    private float MeasureVisualPrimarySizeInContainer(VisualItem visual)
    {
        if (visual == null || _containerRect == null)
        {
            return 0f;
        }

        RectTransform targetRect = visual.RuntimeInfo != null && visual.RuntimeInfo.ContentRect != null
            ? visual.RuntimeInfo.ContentRect
            : visual.RectTransform;
        if (targetRect == null)
        {
            return 0f;
        }

        // Ensure ContentSizeFitter/LayoutGroup changes are reflected before measuring.
        if (visual.RectTransform != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(visual.RectTransform);
        }

        return ScrollerAxisAdapter.MeasureRectInContainer(_containerRect, targetRect, _worldCornersBuffer, scroll_axis);
    }

    private void AddVisibleOrderRange(
        List<int> orders,
        Dictionary<int, float> orderCenters,
        int centerOrder,
        float centerPosition,
        int direction,
        float minY,
        float maxY)
    {
        int stepLimit = Mathf.Max(4, _enabledIndices.Count + (buffer_item_count * 4));
        float cursorCenterPos = centerPosition;
        for (int i = 1; i <= stepLimit; i++)
        {
            int candidate = centerOrder + (i * direction);
            if (direction > 0)
            {
                cursorCenterPos += GetSpanToNextOrder(candidate - 1);
            }
            else
            {
                cursorCenterPos -= GetSpanToNextOrder(candidate);
            }

            float y = cursorCenterPos - _scrollOffset;

            if (y < minY || y > maxY)
            {
                float edgePad = _enabledAverageSpan * 1.2f;
                if (y < (minY - edgePad) || y > (maxY + edgePad))
                {
                    break;
                }
            }

            if (y >= minY && y <= maxY)
            {
                orders.Add(candidate);
                orderCenters[candidate] = cursorCenterPos;
            }
        }
    }

    private void SyncVisibleWindow()
    {
        if (_enabledIndices.Count == 0)
        {
            DeactivateAllActiveVisuals();
            return;
        }

        float visibleHalfHeight = GetVisibleHalfExtent();
        float windowPadding = buffer_item_count * _enabledAverageSpan;
        float minY = -visibleHalfHeight - windowPadding;
        float maxY = visibleHalfHeight + windowPadding;

        int centerOrder = _hasSnapTarget ? _snapTargetOrder : GetNearestOrderToOffset(_scrollOffset);
        if (enable_snapping && !_hasSnapTarget && !_isDragging && Mathf.Abs(_scrollVelocity) < snap_velocity_threshold && _hasSettledOrder)
        {
            centerOrder = _settledOrder;
        }
        _currentCenterOrder = centerOrder;
        _centeredLogicalIndex = ResolveLogicalIndexFromOrder(centerOrder);
        float centerPosition = GetOrderCenterPosition(centerOrder);
        float centerY = centerPosition - _scrollOffset;
        _desiredOrders.Clear();
        _desiredOrderSet.Clear();
        _desiredOrderCenterPositions.Clear();
        if (centerY >= minY && centerY <= maxY)
        {
            _desiredOrders.Add(centerOrder);
            _desiredOrderSet.Add(centerOrder);
            _desiredOrderCenterPositions[centerOrder] = centerPosition;
        }
        AddVisibleOrderRange(_desiredOrders, _desiredOrderCenterPositions, centerOrder, centerPosition, 1, minY, maxY);
        AddVisibleOrderRange(_desiredOrders, _desiredOrderCenterPositions, centerOrder, centerPosition, -1, minY, maxY);

        for (int i = 0; i < _desiredOrders.Count; i++)
        {
            _desiredOrderSet.Add(_desiredOrders[i]);
        }

        _cleanupKeys.Clear();
        foreach (KeyValuePair<int, VisualItem> kvp in _activeVisualsByOrder)
        {
            if (!_desiredOrderSet.Contains(kvp.Key) || kvp.Value == null || kvp.Value.RectTransform == null)
            {
                _cleanupKeys.Add(kvp.Key);
            }
        }

        for (int i = 0; i < _cleanupKeys.Count; i++)
        {
            int key = _cleanupKeys[i];
            if (_activeVisualsByOrder.TryGetValue(key, out VisualItem visual) && visual != null)
            {
                ReleaseVisual(visual);
            }
            _activeVisualsByOrder.Remove(key);
            _measuredOrderSizes.Remove(key);
        }

        bool allRelayoutSettled = true;
        for (int i = 0; i < _desiredOrders.Count; i++)
        {
            int order = _desiredOrders[i];
            int logicalIndex = ResolveLogicalIndexFromOrder(order);
            if (logicalIndex < 0)
            {
                continue;
            }

            if (!_desiredOrderCenterPositions.TryGetValue(order, out float targetCenterPosition))
            {
                targetCenterPosition = GetOrderCenterPosition(order);
            }

            bool isCenteredOrder = order == centerOrder;
            VisualItem visual = EnsureVisualForOrder(order, logicalIndex, isCenteredOrder);
            bool settled = UpdateVisual(visual, targetCenterPosition, isCenteredOrder);
            if (!_pendingInitialReveal && visual.RuntimeInfo != null)
            {
                visual.RuntimeInfo.SetCentered(isCenteredOrder);
            }
            if (!settled)
            {
                allRelayoutSettled = false;
            }
        }

        if (_relayoutSmoothingActive && allRelayoutSettled)
        {
            _relayoutSmoothingActive = false;
        }
    }

    private int ResolveLogicalIndexFromOrder(int absoluteOrder)
    {
        if (_enabledIndices.Count == 0)
        {
            return -1;
        }

        int wrapped = Mod(absoluteOrder, _enabledIndices.Count);
        return _enabledIndices[wrapped];
    }

    private VisualItem EnsureVisualForOrder(int absoluteOrder, int logicalIndex, bool isCenteredOrder)
    {
        bool isNewOrderVisual = false;
        if (_activeVisualsByOrder.TryGetValue(absoluteOrder, out VisualItem visual) && visual != null && visual.RectTransform != null)
        {
            if (visual.LogicalIndex == logicalIndex)
            {
                visual.AbsoluteOrderIndex = absoluteOrder;
                if (visual.RuntimeInfo != null)
                {
                    visual.RuntimeInfo.SetLogicalIndex(logicalIndex);
                    visual.RuntimeInfo.SetOriginalIndex(_items[logicalIndex].OriginalItemIndex);
                    visual.RuntimeInfo.SetManager(this);
                }
                return visual;
            }

            ReleaseVisual(visual);
            _activeVisualsByOrder.Remove(absoluteOrder);
            isNewOrderVisual = true;
        }
        else
        {
            isNewOrderVisual = true;
        }

        visual = AcquireVisual(logicalIndex);
        visual.AbsoluteOrderIndex = absoluteOrder;
        visual.LogicalIndex = logicalIndex;
        visual.SnapToTargetOnPrepare = isNewOrderVisual;
        if (visual.RuntimeInfo != null)
        {
            visual.RuntimeInfo.SetLogicalIndex(logicalIndex);
            visual.RuntimeInfo.SetOriginalIndex(_items[logicalIndex].OriginalItemIndex);
            visual.RuntimeInfo.SetManager(this);
        }
        if (!_desiredOrderCenterPositions.TryGetValue(absoluteOrder, out float targetCenterPosition))
        {
            targetCenterPosition = GetOrderCenterPosition(absoluteOrder);
        }
        PrepareVisualForAppearance(visual, targetCenterPosition, isCenteredOrder);
        _activeVisualsByOrder[absoluteOrder] = visual;
        return visual;
    }

    private VisualItem AcquireVisual(int logicalIndex)
    {
        if (!_pooledVisualsByLogicalIndex.TryGetValue(logicalIndex, out Stack<VisualItem> pool))
        {
            pool = new Stack<VisualItem>();
            _pooledVisualsByLogicalIndex[logicalIndex] = pool;
        }

        while (pool.Count > 0)
        {
            VisualItem pooledVisual = pool.Pop();
            if (pooledVisual != null && pooledVisual.RectTransform != null)
            {
                pooledVisual.RectTransform.SetParent(_containerRect, false);
                return pooledVisual;
            }
        }

        GameObject wrapper = new GameObject(_items[logicalIndex].Prefab.name + "_ScrollerSlot", typeof(RectTransform), typeof(ScrollerItemRuntimeInfo), typeof(ContentSizeFitter), typeof(CanvasGroup));
        RectTransform wrapperRect = wrapper.GetComponent<RectTransform>();
        wrapperRect.SetParent(_containerRect, false);
        wrapperRect.gameObject.SetActive(false);

        ContentSizeFitter sizeFitter = wrapper.GetComponent<ContentSizeFitter>();
        sizeFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        sizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        GameObject content = Instantiate(_items[logicalIndex].Prefab, wrapperRect);
        content.name = _items[logicalIndex].Prefab.name + "_ScrollerItem";
        RectTransform contentRect = content.GetComponent<RectTransform>();
        if (contentRect == null)
        {
            contentRect = content.AddComponent<RectTransform>();
        }

        ScrollerItemRuntimeInfo runtimeInfo = wrapper.GetComponent<ScrollerItemRuntimeInfo>();
        runtimeInfo.Initialize(logicalIndex, _items[logicalIndex].OriginalItemIndex, wrapperRect, contentRect);
        runtimeInfo.SetManager(this);
        CanvasGroup canvasGroup = wrapper.GetComponent<CanvasGroup>();
        canvasGroup.alpha = 1f;
        canvasGroup.interactable = true;
        canvasGroup.blocksRaycasts = true;

        return new VisualItem
        {
            LogicalIndex = logicalIndex,
            RectTransform = wrapperRect,
            CanvasGroup = canvasGroup,
            RuntimeInfo = runtimeInfo,
            BaseLocalScale = wrapperRect.localScale,
            SnapToTargetOnPrepare = true
        };
    }

    private void PrepareVisualForAppearance(VisualItem visual, float targetCenterPosition, bool isCenteredOrder)
    {
        if (visual == null || visual.RectTransform == null)
        {
            return;
        }

        float targetPrimary = targetCenterPosition - _scrollOffset;
        Vector2 anchored = visual.RectTransform.anchoredPosition;
        bool allowPrepareSnap = visual.SnapToTargetOnPrepare && !_pendingInitialReveal;
        if (allowPrepareSnap || !_relayoutSmoothingActive)
        {
            visual.RectTransform.anchoredPosition = ScrollerAxisAdapter.WithPrimary(anchored, targetPrimary, scroll_axis);
        }
        visual.SnapToTargetOnPrepare = false;

        float targetScale = 1f;
        if (enable_distance_scaling)
        {
            float t = Mathf.InverseLerp(GetVisibleHalfExtent(), 0f, Mathf.Abs(targetPrimary));
            targetScale = Mathf.Lerp(edge_scale, center_scale, t);
        }
        if (enable_center_highlight_scaling && isCenteredOrder)
        {
            targetScale += highlight_scale_boost;
        }
        visual.RectTransform.localScale = visual.BaseLocalScale * targetScale;

        visual.RectTransform.gameObject.SetActive(true);
        if (visual.CanvasGroup != null)
        {
            bool isVisible = !_pendingInitialReveal;
            visual.CanvasGroup.alpha = isVisible ? 1f : 0f;
            visual.CanvasGroup.interactable = isVisible;
            visual.CanvasGroup.blocksRaycasts = isVisible;
        }
    }

    private void ReleaseVisual(VisualItem visual)
    {
        if (visual == null || visual.RectTransform == null)
        {
            return;
        }

        if (visual.RuntimeInfo != null)
        {
            visual.RuntimeInfo.SetCentered(false);
        }

        if (!_pooledVisualsByLogicalIndex.TryGetValue(visual.LogicalIndex, out Stack<VisualItem> pool))
        {
            pool = new Stack<VisualItem>();
            _pooledVisualsByLogicalIndex[visual.LogicalIndex] = pool;
        }

        visual.RectTransform.gameObject.SetActive(false);
        pool.Push(visual);
    }

    private bool UpdateVisual(VisualItem visual, float targetCenterPosition, bool isCenteredOrder)
    {
        float targetPrimary = targetCenterPosition - _scrollOffset;
        Vector2 anchored = visual.RectTransform.anchoredPosition;
        float currentPrimary = ScrollerAxisAdapter.GetPrimary(anchored, scroll_axis);
        float y;
        if (_relayoutSmoothingActive)
        {
            float lerpT = 1f - Mathf.Exp(-Mathf.Max(0.01f, relayout_lerp_speed) * Time.deltaTime);
            y = Mathf.Lerp(currentPrimary, targetPrimary, lerpT);
        }
        else
        {
            // Keep spacing math authoritative during normal updates.
            y = targetPrimary;
        }
        visual.RectTransform.anchoredPosition = ScrollerAxisAdapter.WithPrimary(anchored, y, scroll_axis);

        float targetScale = 1f;
        if (enable_distance_scaling)
        {
            float t = Mathf.InverseLerp(GetVisibleHalfExtent(), 0f, Mathf.Abs(y));
            targetScale = Mathf.Lerp(edge_scale, center_scale, t);
        }
        if (enable_center_highlight_scaling && isCenteredOrder)
        {
            targetScale += highlight_scale_boost;
        }
        Vector3 desiredScale = visual.BaseLocalScale * targetScale;
        visual.RectTransform.localScale = Vector3.Lerp(visual.RectTransform.localScale, desiredScale, scale_lerp_speed * Time.deltaTime);

        return Mathf.Abs(y - targetPrimary) <= relayout_settle_epsilon;
    }

    private void DeactivateAllActiveVisuals()
    {
        _cleanupKeys.Clear();
        foreach (KeyValuePair<int, VisualItem> kvp in _activeVisualsByOrder)
        {
            _cleanupKeys.Add(kvp.Key);
        }

        for (int i = 0; i < _cleanupKeys.Count; i++)
        {
            int key = _cleanupKeys[i];
            if (_activeVisualsByOrder.TryGetValue(key, out VisualItem visual))
            {
                ReleaseVisual(visual);
            }
            _activeVisualsByOrder.Remove(key);
        }
    }

    private static int Mod(int value, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        int m = value % count;
        return m < 0 ? m + count : m;
    }

    private static int FloorDiv(int value, int divisor)
    {
        if (divisor == 0)
        {
            return 0;
        }

        int q = value / divisor;
        int r = value % divisor;
        if (r != 0 && ((r < 0) != (divisor < 0)))
        {
            q--;
        }
        return q;
    }
}
