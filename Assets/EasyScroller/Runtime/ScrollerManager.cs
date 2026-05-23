using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EasyScroller
{
    public enum ScrollerItemSourceMode
    {
        PrefabList,
        SinglePrefabWithCount
    }

    public enum ScrollerListMode
    {
        Infinite,
        Finite
    }

    public partial class ScrollerManager : MonoBehaviour
    {
        private const int SharedSinglePrefabPoolKey = -1;

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
        [SerializeField, Tooltip("Infinite wraps forever. Finite stops when first/last item reaches the viewport border.")]
        private ScrollerListMode list_mode = ScrollerListMode.Infinite;
        [SerializeField, Tooltip("Additional edge-to-edge gap between neighboring items on the primary axis.")]
        private float item_gap = 100f;
        [SerializeField, Min(0), Tooltip("How many extra item slots to keep active beyond the viewport edge on each side (counted per direction, not distance).")]
        private int buffer_item_count = 2;
        [SerializeField, Tooltip("Fallback half-extent used when the scroll rect size is not resolved yet.")]
        private float fallback_visible_half_extent = 250f;

        [Header("Drag + Inertia")]
        [SerializeField, Tooltip("How quickly inertial velocity decays toward zero.")]
        private float inertia_damping = 8f;

        [Header("Snap Spring")]
        [SerializeField, Tooltip("Enable or disable automatic snapping to the nearest item after drag/inertia stops.")]
        private bool enable_snapping = true;
        [SerializeField, Tooltip("Smoothly animate CenterNext/Previous and scroll-to-index when animated=true. Independent of auto-snap.")]
        private bool smooth_programmatic_scroll = true;
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
        [SerializeField, Tooltip("While the highlighted item stays within this distance of center, do not switch to another item (prevents flicker between close items).")]
        private float center_highlight_switch_dead_zone = 20f;
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

        [Header("Chain Springs")]
        [SerializeField, Tooltip("How quickly each item eases toward its gap distance from linked neighbors.")]
        private float chain_spring_strength = 18f;

        [Header("Relayout")]
        [SerializeField, Tooltip("Smoothly resolve layout after structural changes (delete/enable/disable).")]
        private bool smooth_relayout_on_structure_change = true;
        [SerializeField, Tooltip("How quickly items ease toward new solved positions during relayout.")]
        private float relayout_lerp_speed = 10f;
        [SerializeField, Tooltip("Distance considered settled during relayout smoothing.")]
        private float relayout_settle_epsilon = 0.5f;
        [SerializeField, Tooltip("After deleting a centered item, gently nudge the chain so the chosen neighbor wins highlight/snap without hard-centering it.")]
        private float relayout_winner_bias_force = 60f;
        [SerializeField, Tooltip("Hide items on initial build until layout and spacing have stabilized.")]
        private bool hide_items_until_initial_settle = true;

        [Header("Scrollbar (Optional)")]
        [SerializeField, Tooltip("Optional UGUI scrollbar used to control finite scroll position.")]
        private Scrollbar linked_scrollbar;
        [SerializeField, Tooltip("Invert normalized scrollbar direction. Useful when top should map to value=1.")]
        private bool invert_scrollbar_value = false;
        [SerializeField, Tooltip("How quickly linked-scrollbar drags ease the scroll offset toward the handle.")]
        private float scrollbar_scroll_smooth_time = 0.08f;
        [SerializeField, Tooltip("Max scroll speed when following the linked scrollbar (units/sec).")]
        private float scrollbar_scroll_max_speed = 12000f;
        [SerializeField, Tooltip("If the scrollbar target is farther than this from the current offset, jump instantly (track clicks). 0 = always smooth.")]
        private float scrollbar_jump_distance_threshold = 120f;

        private class ItemState
        {
            public readonly GameObject Prefab;
            public int SourcePrefabIndex;
            public readonly int DataIndex;
            public float Height;
            public bool Enabled = true;

            public ItemState(GameObject prefab, float height, int sourcePrefabIndex, int dataIndex)
            {
                Prefab = prefab;
                Height = height;
                SourcePrefabIndex = sourcePrefabIndex;
                DataIndex = dataIndex;
            }
        }

        // A VisualItem is a node in the live chain (Prev/Next). Positions are
        // driven by collective scroll motion and neighbor gap springs.
        private class VisualItem
        {
            public int AbsoluteOrderIndex;
            public int LogicalIndex;
            public RectTransform RectTransform;
            public CanvasGroup CanvasGroup;
            public ScrollerItemRuntimeInfo RuntimeInfo;
            public Vector3 BaseLocalScale;
            public float HalfSizeAxis;
            public bool HasMeasuredHalfSize;
            public bool SnapToTargetOnPrepare;
            public int HiddenFramesRemaining;
            public bool IsInChain;
            public VisualItem Prev;
            public VisualItem Next;
            public GameObject BoundContentPrefab;
        }

        private enum MutationKind
        {
            None,
            Insert,
            Remove,
            Rebuild
        }

        private readonly List<ItemState> _items = new List<ItemState>();
        private readonly Dictionary<int, Stack<VisualItem>> _pooledVisualsByLogicalIndex = new Dictionary<int, Stack<VisualItem>>();
        private readonly List<int> _enabledIndices = new List<int>();
        private readonly Dictionary<int, int> _enabledLogicalToSlot = new Dictionary<int, int>();
        private readonly List<float> _enabledPrefixPositions = new List<float>();
        private readonly Stack<VisualItem> _poolFilterScratch = new Stack<VisualItem>();
        private readonly Vector3[] _worldCornersBuffer = new Vector3[4];

        // Chain endpoints ordered by ascending primary-axis position.
        private VisualItem _chainHead;
        private VisualItem _chainTail;

        private float _scrollOffset;
        private float _scrollVelocity;
        private bool _isDragging;
        private int _centeredLogicalIndex = -1;
        private VisualItem _centeredChainVisual;
        private VisualItem _lastCenteredBroadcastVisual;
        private RectTransform _containerRect;
        private float _enabledCycleLength;
        private int _sizeRefreshTickCounter;
        private float _snapVelocity;
        private bool _hasSnapTarget;
        private int _snapTargetOrder;
        private float _snapTargetOffset;
        private bool _snapTargetLockedUntilUserInput;
        private bool _hasSettledOrder;
        private VisualItem _snapTargetChainVisual;
        private VisualItem _settledChainVisual;
        private bool _relayoutActive;
        private VisualItem _relayoutBiasChainVisual;
        private bool _hasProgrammaticStepAnchor;
        private int _programmaticStepOrder;
        private bool _pendingInitialReveal;
        private bool _suppressScrollbarCallback;
        private Scrollbar _registeredScrollbar;
        private LinkedScrollbarDragRelay _scrollbarDragRelay;
        private float _scrollbarTargetOffset;
        private float _scrollbarScrollVelocity;
        private bool _scrollbarOffsetLeadsChain;
        private bool _scrollbarPointerHeld;
        private MutationKind _pendingMutationKind = MutationKind.None;
        private int _pendingMutationLogicalIndex = -1;
        private float _lastScrollOffsetForChainMotion;
        private bool _skipCollectiveScrollThisFrame;
        private readonly List<int> _viewportOrdersScratch = new List<int>();
        private readonly HashSet<RectTransform> _reachableChainRectsScratch = new HashSet<RectTransform>();
        private bool _validateChainThisFrame;
        private bool _deferChainTrim;
        private float _frameVisibleHalfExtent;
        private bool _frameVisibleHalfExtentValid;
        private readonly List<ScrollerItemRuntimeInfo> _pendingContentRefreshes = new List<ScrollerItemRuntimeInfo>();

        void OnEnable()
        {
            RebindLinkedScrollbarCallback();
        }

        void OnDisable()
        {
            UnregisterLinkedScrollbarCallback();
        }

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
                Debug.LogWarning("ScrollerManager has no prefabs configured. Assign prefabs in the inspector or call SetPrefabs(...) at runtime.");
            }
            RefreshEnabledIndices();
            _relayoutActive = false;
            _pendingInitialReveal = hide_items_until_initial_settle;
            _lastScrollOffsetForChainMotion = _scrollOffset;
            SyncVisibleWindow();
            RefreshLinkedScrollbarState();
        }

        void LateUpdate()
        {
            for (int i = 0; i < _pendingContentRefreshes.Count; i++)
            {
                ScrollerItemRuntimeInfo runtimeInfo = _pendingContentRefreshes[i];
                if (runtimeInfo != null)
                {
                    runtimeInfo.NotifyContentRefreshRequested();
                }
            }

            _pendingContentRefreshes.Clear();
        }

        void Update()
        {
            if (_enabledIndices.Count == 0)
            {
                if (_chainHead != null)
                {
                    DeactivateAllActiveVisuals();
                }

                RefreshLinkedScrollbarState();
                _frameVisibleHalfExtentValid = false;
                return;
            }

            float dt = Time.deltaTime;

            bool hasVelocity = Mathf.Abs(_scrollVelocity) > 0.0001f;
            if (!_isDragging && hasVelocity)
            {
                _scrollOffset += _scrollVelocity * dt;
                float clampedOffset = ClampOffsetForMode(_scrollOffset);
                if (!Mathf.Approximately(clampedOffset, _scrollOffset))
                {
                    _scrollOffset = clampedOffset;
                    _scrollVelocity = 0f;
                }
                _scrollVelocity = Mathf.Lerp(_scrollVelocity, 0f, GetExponentialMoveT(inertia_damping));
            }

            // Spring toward an active snap target (programmatic next/prev or auto-snap).
            if (_hasSnapTarget && !_isDragging && Mathf.Abs(_scrollVelocity) < snap_velocity_threshold)
            {
                if (!IsVisualActiveInChain(_snapTargetChainVisual))
                {
                    VisualItem resolvedTarget = FindChainVisualByOrder(_snapTargetOrder);
                    if (IsVisualActiveInChain(resolvedTarget))
                    {
                        _snapTargetChainVisual = resolvedTarget;
                    }
                }

                if (IsVisualActiveInChain(_snapTargetChainVisual))
                {
                    _snapTargetOrder = _snapTargetChainVisual.AbsoluteOrderIndex;
                    _snapTargetOffset = ComputeScrollOffsetToCenterVisual(_snapTargetChainVisual);
                }

                _scrollOffset = Mathf.SmoothDamp(_scrollOffset, _snapTargetOffset, ref _snapVelocity, snap_smooth_time, snap_max_speed, dt);
                _scrollOffset = ClampOffsetForMode(_scrollOffset);
                _scrollVelocity = 0f;

                bool centeredOnTarget = IsVisualActiveInChain(_snapTargetChainVisual) &&
                                        Mathf.Abs(GetVisualAxis(_snapTargetChainVisual)) <= snap_position_epsilon;
                bool offsetSettled = Mathf.Abs(_snapTargetOffset - _scrollOffset) <= snap_position_epsilon;
                if ((centeredOnTarget || _snapTargetChainVisual == null) &&
                    offsetSettled &&
                    Mathf.Abs(_snapVelocity) < snap_velocity_threshold)
                {
                    _snapVelocity = 0f;
                    _hasSettledOrder = _snapTargetChainVisual != null;
                    _settledChainVisual = _snapTargetChainVisual;
                    _hasSnapTarget = false;
                }
            }
            else if (_hasSnapTarget && (_isDragging || Mathf.Abs(_scrollVelocity) >= snap_velocity_threshold))
            {
                ClearActiveSnapState();
            }

            // Auto-snap after release only when enabled and nothing else is driving a target.
            if (enable_snapping &&
                !_hasSnapTarget &&
                !_isDragging &&
                !_scrollbarOffsetLeadsChain &&
                Mathf.Abs(_scrollVelocity) < snap_velocity_threshold &&
                !IsIdleOnSettledSnapVisual())
            {
                BeginSnapToResolvedTargetVisual();
            }

            if (_scrollbarOffsetLeadsChain)
            {
                if (!TryJumpScrollbarOffsetToTarget())
                {
                    _scrollOffset = Mathf.SmoothDamp(
                        _scrollOffset,
                        _scrollbarTargetOffset,
                        ref _scrollbarScrollVelocity,
                        scrollbar_scroll_smooth_time,
                        scrollbar_scroll_max_speed,
                        dt);
                    _scrollOffset = ClampOffsetForMode(_scrollOffset);
                }

                _scrollVelocity = 0f;

                if (!_scrollbarPointerHeld &&
                    Mathf.Abs(_scrollOffset - _scrollbarTargetOffset) <= snap_position_epsilon)
                {
                    _scrollbarOffsetLeadsChain = false;
                    _scrollbarScrollVelocity = 0f;
                }
            }

            if (Mathf.Abs(_scrollVelocity) < 0.005f && !_isDragging)
            {
                _scrollVelocity = 0f;
            }

            // First pass: drive the chain forward and place visuals.
            SyncVisibleWindow();

            // Second step: measure after scaling. If primary sizes drifted, the
            // chain propagation will gradually absorb them next frame; for
            // larger jumps we re-sync the window in the same frame so users
            // see the updated layout immediately.
            bool spacingChanged = RefreshItemSizesOnTick();
            if (spacingChanged)
            {
                SyncVisibleWindow();
            }

            if (_pendingInitialReveal && !_relayoutActive && !spacingChanged)
            {
                SetVisibilityForChainVisuals(true);
                _pendingInitialReveal = false;
                BroadcastCenteredState();
            }

            RefreshLinkedScrollbarState();
            _frameVisibleHalfExtentValid = false;
        }

        // -----------------------------------------------------------------
        // Build / refresh
        // -----------------------------------------------------------------

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

        private int GetNextDataIndex()
        {
            int next = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                next = Mathf.Max(next, _items[i].DataIndex + 1);
            }

            return next;
        }

        private int FindLogicalIndexByDataIndex(int dataIndex)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].DataIndex == dataIndex && _items[i].Enabled)
                {
                    return i;
                }
            }

            return -1;
        }

        private void RefreshEnabledIndices()
        {
            _enabledIndices.Clear();
            _enabledLogicalToSlot.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Enabled)
                {
                    _enabledLogicalToSlot[i] = _enabledIndices.Count;
                    _enabledIndices.Add(i);
                }
            }

            RebuildEnabledSpacingData();
        }

        private int GetEnabledSlot(int logicalIndex)
        {
            return _enabledLogicalToSlot.TryGetValue(logicalIndex, out int slot) ? slot : -1;
        }

        private bool IsLogicalEnabled(int logicalIndex)
        {
            return GetEnabledSlot(logicalIndex) >= 0;
        }

        /// <summary>
        /// Maps an insert position in the enabled list (0 = before first visible item) to a
        /// logical index in <see cref="_items"/> for <see cref="List{T}.Insert(int, T)"/>.
        /// </summary>
        private int ResolveLogicalIndexForEnabledInsertSlot(int enabledSlot)
        {
            int enabledCount = _enabledIndices.Count;
            if (enabledCount == 0)
            {
                return Mathf.Clamp(enabledSlot, 0, _items.Count);
            }

            if (enabledSlot <= 0)
            {
                return _enabledIndices[0];
            }

            if (enabledSlot >= enabledCount)
            {
                return Mathf.Min(_enabledIndices[enabledCount - 1] + 1, _items.Count);
            }

            return _enabledIndices[enabledSlot];
        }

        private void ReindexSourcePrefabIndices()
        {
            if (item_source_mode != ScrollerItemSourceMode.PrefabList)
            {
                return;
            }

            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].SourcePrefabIndex = i;
            }
        }

        private void MarkPendingInsertMutation(int logicalIndex)
        {
            _pendingMutationKind = MutationKind.Insert;
            _pendingMutationLogicalIndex = logicalIndex;
        }

        private void MarkPendingRemoveMutation(int logicalIndex)
        {
            _pendingMutationKind = MutationKind.Remove;
            _pendingMutationLogicalIndex = logicalIndex;
        }

        private void MarkPendingRebuildMutation()
        {
            _pendingMutationKind = MutationKind.Rebuild;
            _pendingMutationLogicalIndex = -1;
        }

        private void ConsumePendingMutation(out MutationKind kind, out int logicalIndex)
        {
            kind = _pendingMutationKind;
            logicalIndex = _pendingMutationLogicalIndex;

            _pendingMutationKind = MutationKind.None;
            _pendingMutationLogicalIndex = -1;
        }

        private void RebuildEnabledSpacingData()
        {
            _enabledPrefixPositions.Clear();
            _enabledCycleLength = 0f;

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
            float sizeDeltaAxis = ScrollerAxisAdapter.GetSizeDeltaPrimary(prefabRect, scroll_axis);
            if (sizeDeltaAxis > 0.01f)
            {
                return sizeDeltaAxis;
            }

            return 100f;
        }

        // -----------------------------------------------------------------
        // Lattice helpers (kept for snap/jump/scrollbar APIs)
        // -----------------------------------------------------------------

        private float GetOrderCenterPosition(int absoluteOrder)
        {
            int count = _enabledIndices.Count;
            if (count <= 0)
            {
                return 0f;
            }

            if (count == 1)
            {
                float singleSpan = Mathf.Max(1f, _items[_enabledIndices[0]].Height + item_gap);
                int safeOrder = IsFiniteMode() ? Mathf.Clamp(absoluteOrder, 0, 0) : absoluteOrder;
                return safeOrder * singleSpan;
            }

            if (IsFiniteMode())
            {
                int finiteOrder = Mathf.Clamp(absoluteOrder, 0, count - 1);
                return _enabledPrefixPositions[finiteOrder];
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
                if (IsFiniteMode())
                {
                    return 0;
                }

                float singleSpan = Mathf.Max(1f, _items[_enabledIndices[0]].Height + item_gap);
                return Mathf.RoundToInt(offset / singleSpan);
            }

            if (IsFiniteMode())
            {
                float bestDistance = float.MaxValue;
                int bestOrder = 0;
                for (int order = 0; order < count; order++)
                {
                    float orderPos = GetOrderCenterPosition(order);
                    float distance = Mathf.Abs(orderPos - offset);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestOrder = order;
                    }
                }
                return bestOrder;
            }

            float infBestDistance = float.MaxValue;
            int infBestOrder = 0;
            for (int i = 0; i < count; i++)
            {
                float basePos = _enabledPrefixPositions[i];
                int cycle = Mathf.RoundToInt((offset - basePos) / _enabledCycleLength);
                int candidate = (cycle * count) + i;
                float candidatePos = (cycle * _enabledCycleLength) + basePos;
                float distance = Mathf.Abs(candidatePos - offset);
                if (distance < infBestDistance)
                {
                    infBestDistance = distance;
                    infBestOrder = candidate;
                }
            }
            return infBestOrder;
        }

        private int ResolveLogicalIndexFromOrder(int absoluteOrder)
        {
            if (_enabledIndices.Count == 0)
            {
                return -1;
            }

            if (IsFiniteMode())
            {
                if (absoluteOrder < 0 || absoluteOrder >= _enabledIndices.Count)
                {
                    return -1;
                }
                return _enabledIndices[absoluteOrder];
            }

            int wrapped = Mod(absoluteOrder, _enabledIndices.Count);
            return _enabledIndices[wrapped];
        }

        /// <summary>
        /// Picks the absolute order for <paramref name="logicalIndex"/> whose lattice center
        /// is closest to <paramref name="referenceOffset"/> (searches all wrap cycles).
        /// </summary>
        private int RefineOrderToNearestCycle(int candidate, float referenceOffset)
        {
            int count = _enabledIndices.Count;
            if (IsFiniteMode() || count <= 0)
            {
                return candidate;
            }

            int bestOrder = candidate;
            float bestDistance = Mathf.Abs(GetOrderCenterPosition(bestOrder) - referenceOffset);
            bool improved;
            do
            {
                improved = false;
                int plus = bestOrder + count;
                int minus = bestOrder - count;
                float plusDistance = Mathf.Abs(GetOrderCenterPosition(plus) - referenceOffset);
                if (plusDistance < bestDistance)
                {
                    bestDistance = plusDistance;
                    bestOrder = plus;
                    improved = true;
                }

                float minusDistance = Mathf.Abs(GetOrderCenterPosition(minus) - referenceOffset);
                if (minusDistance < bestDistance)
                {
                    bestDistance = minusDistance;
                    bestOrder = minus;
                    improved = true;
                }
            }
            while (improved);

            return bestOrder;
        }

        private VisualItem GetReferenceChainVisualForNavigation()
        {
            if (_hasProgrammaticStepAnchor)
            {
                VisualItem programmatic = FindChainVisualByOrder(_programmaticStepOrder);
                if (IsVisualActiveInChain(programmatic))
                {
                    return programmatic;
                }
            }

            if (_hasSnapTarget && IsVisualActiveInChain(_snapTargetChainVisual))
            {
                return _snapTargetChainVisual;
            }

            if (_hasSettledOrder && IsVisualActiveInChain(_settledChainVisual))
            {
                return _settledChainVisual;
            }

            if (_chainHead != null)
            {
                return FindChainVisualNearestToAxis(0f);
            }

            return null;
        }

        private VisualItem WalkChainAdjacent(VisualItem from, int sign, int steps)
        {
            if (!IsVisualActiveInChain(from) || steps <= 0)
            {
                return from;
            }

            VisualItem cursor = from;
            for (int i = 0; i < steps; i++)
            {
                VisualItem adjacent = sign > 0 ? cursor.Next : cursor.Prev;
                if (adjacent == null)
                {
                    return cursor;
                }

                cursor = adjacent;
            }

            return cursor;
        }

        private VisualItem ResolveAdjacentChainVisual(VisualItem from, int sign, int steps)
        {
            if (!IsVisualActiveInChain(from))
            {
                return null;
            }

            VisualItem walked = WalkChainAdjacent(from, sign, steps);
            if (walked != from)
            {
                return walked;
            }

            SyncVisibleWindow();
            walked = WalkChainAdjacent(from, sign, steps);
            if (walked != from)
            {
                return walked;
            }

            int targetOrder = ClampOrderForMode(from.AbsoluteOrderIndex + (sign * steps));
            return FindChainVisualByOrder(targetOrder);
        }

        private void StopUserMotionForProgrammaticScroll()
        {
            _isDragging = false;
            _scrollVelocity = 0f;
            _snapVelocity = 0f;
        }

        /// <summary>
        /// User-initiated navigation (next/prev, scroll-to-index). Ends post-delete relayout
        /// so input is not blocked until gap springs settle.
        /// </summary>
        private void BeginProgrammaticNavigation()
        {
            EndRelayout();
        }

        private bool IsNavigationBlockedByInitialReveal()
        {
            return _pendingInitialReveal;
        }

        private void BeginAnimatedSnapToVisual(VisualItem targetVisual)
        {
            _snapTargetChainVisual = targetVisual;
            _snapTargetOrder = targetVisual.AbsoluteOrderIndex;
            _snapTargetOffset = ComputeScrollOffsetToCenterVisual(targetVisual);
            _hasSnapTarget = true;
            _snapTargetLockedUntilUserInput = true;
        }

        private void ApplyScrollOffsetToChainImmediately()
        {
            if (_chainHead == null)
            {
                _lastScrollOffsetForChainMotion = _scrollOffset;
                return;
            }

            float scrollDelta = _scrollOffset - _lastScrollOffsetForChainMotion;
            if (Mathf.Abs(scrollDelta) > 0.0001f)
            {
                ApplyCollectiveMovementToChain(-scrollDelta);
            }

            _lastScrollOffsetForChainMotion = _scrollOffset;
            _skipCollectiveScrollThisFrame = true;
        }

        private void SettleScrollAtVisual(VisualItem targetVisual)
        {
            _scrollOffset = ClampOffsetForMode(ComputeScrollOffsetToCenterVisual(targetVisual));
            ClearActiveSnapState();
            _hasSettledOrder = true;
            _settledChainVisual = targetVisual;
            ApplyScrollOffsetToChainImmediately();
        }

        private bool ShouldSmoothProgrammaticScroll(bool animated)
        {
            return animated && smooth_programmatic_scroll;
        }

        private bool CenterChainVisual(VisualItem targetVisual, bool animated = true)
        {
            if (!IsVisualActiveInChain(targetVisual))
            {
                return false;
            }

            BeginProgrammaticNavigation();
            StopUserMotionForProgrammaticScroll();
            _hasProgrammaticStepAnchor = true;
            _programmaticStepOrder = targetVisual.AbsoluteOrderIndex;

            if (ShouldSmoothProgrammaticScroll(animated))
            {
                BeginAnimatedSnapToVisual(targetVisual);
            }
            else
            {
                SettleScrollAtVisual(targetVisual);
            }

            SyncVisibleWindow();
            RefreshLinkedScrollbarState();
            return true;
        }

        private bool CenterAdjacentChainItem(int direction, int steps = 1)
        {
            if (_enabledIndices.Count == 0 || direction == 0 || steps <= 0 || IsNavigationBlockedByInitialReveal())
            {
                return false;
            }

            int sign = direction > 0 ? 1 : -1;
            VisualItem current = GetReferenceChainVisualForNavigation();
            if (!IsVisualActiveInChain(current))
            {
                return false;
            }

            VisualItem target = ResolveAdjacentChainVisual(current, sign, steps);
            if (!IsVisualActiveInChain(target) || target == current)
            {
                int fromSlot = GetEnabledSlot(current.LogicalIndex);
                if (fromSlot < 0)
                {
                    return false;
                }

                int count = _enabledIndices.Count;
                int targetSlot = IsFiniteMode()
                    ? fromSlot + (sign * steps)
                    : Mod(fromSlot + (sign * steps), count);
                if (IsFiniteMode() && (targetSlot < 0 || targetSlot >= count))
                {
                    return false;
                }

                int targetLogical = _enabledIndices[targetSlot];
                int targetOrder = ComputeNearestOrderForLogical(targetLogical, _scrollOffset);
                float targetOffset = ClampOffsetForMode(GetOrderCenterPosition(targetOrder));
                return ScrollToOffsetAndOrder(targetOffset, targetOrder, targetLogical, animated: true);
            }

            return CenterChainVisual(target, animated: true);
        }

        private bool TryEnsureChainVisualForOrder(int logicalIndex, int targetOrder, float targetOffset, bool animated)
        {
            if (FindChainVisualByOrder(targetOrder) != null)
            {
                return true;
            }

            if (logicalIndex < 0 || logicalIndex >= _items.Count)
            {
                return false;
            }

            if (!IsFiniteMode() && TryInsertVisualForAbsoluteOrder(targetOrder, logicalIndex))
            {
                return true;
            }

            float savedOffset = _scrollOffset;
            float savedLastOffset = _lastScrollOffsetForChainMotion;
            bool savedSkipCollective = _skipCollectiveScrollThisFrame;

            _scrollOffset = ClampOffsetForMode(targetOffset);
            _lastScrollOffsetForChainMotion = _scrollOffset;
            _skipCollectiveScrollThisFrame = true;
            _deferChainTrim = true;
            SyncVisibleWindow();
            _deferChainTrim = false;

            if (FindChainVisualByOrder(targetOrder) == null && !IsFiniteMode())
            {
                TryInsertVisualForAbsoluteOrder(targetOrder, logicalIndex);
                if (FindChainVisualByOrder(targetOrder) == null)
                {
                    EnsureInfiniteViewportOrderCoverage(GetVisibleHalfExtent());
                }
            }

            bool found = FindChainVisualByOrder(targetOrder) != null;
            if (animated)
            {
                _scrollOffset = savedOffset;
                _lastScrollOffsetForChainMotion = savedLastOffset;
                _skipCollectiveScrollThisFrame = savedSkipCollective;
                if (found && _chainHead != null)
                {
                    for (VisualItem v = _chainHead; v != null; v = v.Next)
                    {
                        SetVisualAxisToLatticeOrder(v, v.AbsoluteOrderIndex);
                    }
                }
            }

            return found;
        }

        private bool ScrollToOffsetAndOrder(float targetOffset, int targetOrder, int targetLogical, bool animated)
        {
            if (_enabledIndices.Count == 0 || IsNavigationBlockedByInitialReveal())
            {
                return false;
            }

            VisualItem targetVisual = null;
            if (targetLogical >= 0)
            {
                TryResolveScrollTargetForLogical(
                    targetLogical,
                    out targetVisual,
                    out targetOrder,
                    out targetOffset);

                if (IsVisualAlreadyCenteredForScroll(targetVisual, targetLogical))
                {
                    BeginProgrammaticNavigation();
                    StopUserMotionForProgrammaticScroll();
                    SettleScrollAtVisual(targetVisual);
                    _hasProgrammaticStepAnchor = true;
                    _programmaticStepOrder = targetVisual.AbsoluteOrderIndex;
                    SyncVisibleWindow();
                    RefreshLinkedScrollbarState();
                    return true;
                }
            }
            else
            {
                targetVisual = FindChainVisualByOrder(targetOrder);
            }

            TryEnsureChainVisualForOrder(targetLogical, targetOrder, targetOffset, animated);

            BeginProgrammaticNavigation();
            StopUserMotionForProgrammaticScroll();
            _hasProgrammaticStepAnchor = true;
            _programmaticStepOrder = ClampOrderForMode(targetOrder);

            targetVisual = FindChainVisualByOrder(_programmaticStepOrder);
            if (ShouldSmoothProgrammaticScroll(animated))
            {
                if (IsVisualActiveInChain(targetVisual))
                {
                    BeginAnimatedSnapToVisual(targetVisual);
                }
                else
                {
                    _snapTargetChainVisual = null;
                    _snapTargetOrder = _programmaticStepOrder;
                    _snapTargetOffset = targetOffset;
                    _hasSnapTarget = true;
                    _snapTargetLockedUntilUserInput = true;
                }
            }
            else
            {
                if (IsVisualActiveInChain(targetVisual))
                {
                    SettleScrollAtVisual(targetVisual);
                }
                else
                {
                    _scrollOffset = ClampOffsetForMode(targetOffset);
                    _lastScrollOffsetForChainMotion = _scrollOffset;
                    ClearActiveSnapState();
                    _hasSettledOrder = true;
                    _settledChainVisual = null;
                    ApplyScrollOffsetToChainImmediately();
                }
            }

            SyncVisibleWindow();
            RefreshLinkedScrollbarState();
            return true;
        }

        // -----------------------------------------------------------------
        // Sync / chain propagation
        // -----------------------------------------------------------------

        private float GetVisibleHalfExtent()
        {
            return ScrollerAxisAdapter.GetRectHalfSize(_containerRect, scroll_axis, fallback_visible_half_extent);
        }

        private float GetVisualAxis(VisualItem visual)
        {
            if (visual == null || visual.RectTransform == null)
            {
                return 0f;
            }
            return ScrollerAxisAdapter.GetPrimary(visual.RectTransform.anchoredPosition, scroll_axis);
        }

        private void SetVisualAxis(VisualItem visual, float axis)
        {
            if (visual == null || visual.RectTransform == null)
            {
                return;
            }
            Vector2 anchored = visual.RectTransform.anchoredPosition;
            visual.RectTransform.anchoredPosition = ScrollerAxisAdapter.WithPrimary(anchored, axis, scroll_axis);
        }

        private float GetVisualHalfSize(VisualItem visual)
        {
            if (visual == null)
            {
                return 0f;
            }
            if (visual.HasMeasuredHalfSize && visual.HalfSizeAxis > 0f)
            {
                return visual.HalfSizeAxis;
            }
            if (visual.LogicalIndex >= 0 && visual.LogicalIndex < _items.Count)
            {
                return 0.5f * _items[visual.LogicalIndex].Height;
            }
            return 50f;
        }

        private void SyncVisibleWindow()
        {
            if (_enabledIndices.Count == 0)
            {
                DeactivateAllActiveVisuals();
                return;
            }

            _frameVisibleHalfExtent = GetVisibleHalfExtent();
            _frameVisibleHalfExtentValid = true;

            // Finite bounds: ease only during passive relayout. User scroll/drag
            // always gets immediate clamping so input is never damped away.
            float clampedOffset = ClampOffsetForMode(_scrollOffset);
            if (_relayoutActive && IsFiniteMode() && !IsUserActivelyScrolling())
            {
                _scrollOffset = Mathf.Lerp(_scrollOffset, clampedOffset, GetSpringMoveT(passiveRelayout: true));
            }
            else
            {
                _scrollOffset = clampedOffset;
            }

            // 1) If the chain has no visuals yet, build a fresh anchor at the
            //    current center order so subsequent passes have something to
            //    grow from.
            if (_chainHead == null)
            {
                BuildInitialAnchorAtOrder(GetFallbackCenterOrder());
                if (_chainHead == null)
                {
                    return;
                }
            }

            // Drop active visuals that are no longer reachable from the chain head
            // (can happen when a splice fallback replaced head without unlinking).
            ReleaseOrphanedChainIslands();

            // 2) Drop disabled visuals, prune invalid adjacent dupes, sync orders along links.
            ReconcileChainLogicalsAndOrders();
            SyncNavigationAnchorsAfterChainReconcile();

            if (_chainHead == null)
            {
                BuildInitialAnchorAtOrder(GetFallbackCenterOrder());
                if (_chainHead == null)
                {
                    return;
                }
            }

            // 3) Scroll / snap / inertia move the whole visible chain together.
            //    _scrollOffset is a derived metric for snap, scrollbar, and APIs;
            //    visuals follow via collective motion, not lattice re-anchoring.
            if (!_skipCollectiveScrollThisFrame && _chainHead != null)
            {
                float scrollDelta = _scrollOffset - _lastScrollOffsetForChainMotion;
                if (Mathf.Abs(scrollDelta) > 0.0001f)
                {
                    ApplyCollectiveMovementToChain(-scrollDelta);
                }
            }
            _skipCollectiveScrollThisFrame = false;
            _lastScrollOffsetForChainMotion = _scrollOffset;

            // 4) Neighbor springs maintain item_gap.
            SolveChainSprings();
            ApplyRelayoutWinnerBias();

            // Keep the abstract scroll metric aligned with the live chain when idle.
            // While dragging, coasting, or snapping, offset leads and visuals follow.
            bool offsetLeadsChain = DoesScrollOffsetLeadChain();
            if (!offsetLeadsChain)
            {
                SyncScrollOffsetFromChainCenter();
                _lastScrollOffsetForChainMotion = _scrollOffset;
            }

            // 5) Spawn missing catalog neighbors at chain edges, then trim.
            float halfExtent = GetVisibleHalfExtent();
            EnsureViewportCoverageFromCatalog(halfExtent);

            TrimChainCoverage(halfExtent);

            if (!IsFiniteMode() && _chainHead != null)
            {
                SyncOrdersAlongChainLinks();
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_validateChainThisFrame)
            {
                LogChainInvariantFailureIfNeeded("mutation");
                _validateChainThisFrame = false;
            }
#endif

            // Center highlight follows whichever chain visual is actually nearest
            // the viewport axis — not the abstract lattice order (which drifts
            // after deletes, relayout springs, or stale settled-order state).
            RefreshCenteredVisualFromChain();

            // 6) Apply scaling, alpha.
            UpdateVisualPresentation(halfExtent);

            // 7) Decide whether the relayout has settled.
            CheckRelayoutSettle();

            if (!_pendingInitialReveal)
            {
                BroadcastCenteredState();
            }
        }

        private int GetFallbackCenterOrder()
        {
            if (_hasSnapTarget)
            {
                return ClampOrderForMode(_snapTargetOrder);
            }

            return GetNearestOrderToOffset(_scrollOffset);
        }

        private void BuildInitialAnchorAtOrder(int centerOrder)
        {
            int centerLogical = ResolveLogicalIndexFromOrder(centerOrder);
            if (centerLogical < 0)
            {
                return;
            }
            VisualItem fresh = AcquireAndAttachFreshVisual(centerOrder, centerLogical);
            if (fresh == null)
            {
                return;
            }
            InsertAsOnlyChainNode(fresh);
            float axis = GetOrderCenterPosition(centerOrder) - _scrollOffset;
            SetVisualAxis(fresh, axis);
        }

        private void ApplyCollectiveMovementToChain(float deltaAxis)
        {
            if (_chainHead == null || Mathf.Abs(deltaAxis) < 0.0001f)
            {
                return;
            }

            VisualItem v = _chainHead;
            while (v != null)
            {
                if (v.RectTransform != null)
                {
                    SetVisualAxis(v, GetVisualAxis(v) + deltaAxis);
                }
                v = v.Next;
            }
        }

        private void SyncScrollOffsetFromChainCenter()
        {
            if (_chainHead == null || _enabledIndices.Count == 0)
            {
                return;
            }

            VisualItem centerVisual = FindChainVisualNearestToAxis(0f);
            if (centerVisual == null)
            {
                return;
            }

            int order = ComputeNearestOrderForLogical(centerVisual.LogicalIndex, _scrollOffset);
            _scrollOffset = GetOrderCenterPosition(order) - GetVisualAxis(centerVisual);
        }

        private float GetGapBetween(VisualItem a, VisualItem b)
        {
            return GetVisualHalfSize(a) + GetVisualHalfSize(b) + item_gap;
        }

        private float GetTargetAxisAfterPrev(VisualItem visual)
        {
            return GetVisualAxis(visual.Prev) + GetGapBetween(visual.Prev, visual);
        }

        private float GetTargetAxisBeforeNext(VisualItem visual)
        {
            return GetVisualAxis(visual.Next) - GetGapBetween(visual, visual.Next);
        }

        private void SolveChainSprings()
        {
            if (_chainHead == null)
            {
                return;
            }

            bool passiveRelayout = _relayoutActive && !IsUserActivelyScrolling();
            float moveT = GetSpringMoveT(passiveRelayout);

            // Forward pass: each item springs toward its prev neighbor gap.
            VisualItem v = _chainHead.Next;
            while (v != null)
            {
                if (v.Prev != null)
                {
                    float target = GetTargetAxisAfterPrev(v);
                    float resolved = ResolveSpringAxis(v, target, moveT);
                    SetVisualAxis(v, resolved);
                }
                v = v.Next;
            }

            // Backward pass: pull items toward their next neighbor gap.
            v = _chainTail != null ? _chainTail.Prev : null;
            while (v != null)
            {
                if (v.Next != null)
                {
                    float target = GetTargetAxisBeforeNext(v);
                    float resolved = ResolveSpringAxis(v, target, moveT);
                    SetVisualAxis(v, resolved);
                }
                v = v.Prev;
            }
        }

        private void ApplyRelayoutWinnerBias()
        {
            if (!_relayoutActive ||
                IsUserActivelyScrolling() ||
                _hasSnapTarget ||
                _hasProgrammaticStepAnchor ||
                !IsVisualActiveInChain(_relayoutBiasChainVisual))
            {
                return;
            }

            if (relayout_winner_bias_force <= 0f)
            {
                return;
            }

            float axis = GetVisualAxis(_relayoutBiasChainVisual);
            if (Mathf.Abs(axis) <= relayout_settle_epsilon)
            {
                return;
            }

            float push = Mathf.Sign(axis) * relayout_winner_bias_force * Time.deltaTime;
            ApplyCollectiveMovementToChain(-push);
            _scrollOffset += push;
            _lastScrollOffsetForChainMotion = _scrollOffset;
        }

        private float ResolveSpringAxis(VisualItem visual, float targetAxis, float moveT)
        {
            if (visual == null)
            {
                return targetAxis;
            }

            if (visual.SnapToTargetOnPrepare)
            {
                visual.SnapToTargetOnPrepare = false;
                return targetAxis;
            }

            if (_isDragging)
            {
                return GetVisualAxis(visual);
            }

            float current = GetVisualAxis(visual);
            return Mathf.Lerp(current, targetAxis, moveT);
        }

        /// <summary>
        /// Catalog is source of truth; chain is cleaned and orders follow link sequence.
        /// Does not truncate the chain on slot gaps — edge spawn fills missing catalog entries.
        /// </summary>
        private void ReconcileChainLogicalsAndOrders()
        {
            VisualItem v = _chainHead;
            while (v != null)
            {
                VisualItem next = v.Next;
                if (GetEnabledSlot(v.LogicalIndex) < 0)
                {
                    DetachFromChain(v);
                    ReleaseVisual(v);
                }

                v = next;
            }

            if (_chainHead == null)
            {
                return;
            }

            if (_enabledIndices.Count > 1)
            {
                PruneAdjacentDuplicateLogicalsOnChain();
                if (_chainHead == null)
                {
                    return;
                }

                PruneDuplicateLogicalInstancesOnChain();
                if (_chainHead == null)
                {
                    return;
                }
            }

            SyncOrdersAlongChainLinks();
        }

        private void SyncOrdersAlongChainLinks()
        {
            if (_chainHead == null)
            {
                return;
            }

            if (IsFiniteMode())
            {
                for (VisualItem visual = _chainHead; visual != null; visual = visual.Next)
                {
                    int slot = GetEnabledSlot(visual.LogicalIndex);
                    if (slot >= 0)
                    {
                        visual.AbsoluteOrderIndex = slot;
                    }
                }

                if (!_relayoutActive)
                {
                    AlignFiniteChainVisualAxesToLattice();
                }

                return;
            }

            int headOrder = _chainHead.AbsoluteOrderIndex;
            if (ResolveLogicalIndexFromOrder(headOrder) != _chainHead.LogicalIndex)
            {
                headOrder = ComputeNearestOrderForLogical(_chainHead.LogicalIndex, _scrollOffset);
            }

            _chainHead.AbsoluteOrderIndex = headOrder;
            int orderValue = headOrder;
            for (VisualItem cursor = _chainHead.Next; cursor != null; cursor = cursor.Next)
            {
                orderValue++;
                cursor.AbsoluteOrderIndex = orderValue;
            }
        }

        private bool IsLogicallySequentialForward(int slotA, int slotB, int count)
        {
            if (count <= 0) return false;
            if (IsFiniteMode())
            {
                return slotB == slotA + 1;
            }
            return slotB == Mod(slotA + 1, count);
        }

        /// <summary>
        /// Places finite chain visuals on the rebuilt lattice for the current
        /// <see cref="_scrollOffset"/> so scroll-to-index targets the correct item
        /// after deletes (not pre-delete screen positions).
        /// </summary>
        private void AlignFiniteChainVisualAxesToLattice()
        {
            if (!IsFiniteMode() || _chainHead == null)
            {
                return;
            }

            VisualItem v = _chainHead;
            while (v != null)
            {
                int slot = GetEnabledSlot(v.LogicalIndex);
                if (slot >= 0)
                {
                    float axis = GetOrderCenterPosition(slot) - _scrollOffset;
                    SetVisualAxis(v, axis);
                }

                v = v.Next;
            }
        }

        /// <summary>
        /// Keeps snap / step anchors aligned with post-reconcile enabled slots.
        /// </summary>
        private void SyncNavigationAnchorsAfterChainReconcile()
        {
            if (_hasSettledOrder && IsVisualActiveInChain(_settledChainVisual))
            {
                if (!IsLogicalEnabled(_settledChainVisual.LogicalIndex))
                {
                    _hasSettledOrder = false;
                    _settledChainVisual = null;
                }
            }

            if (_hasProgrammaticStepAnchor && IsFiniteMode())
            {
                int logical = ResolveLogicalIndexFromOrder(_programmaticStepOrder);
                if (logical < 0 || !IsLogicalEnabled(logical))
                {
                    _hasProgrammaticStepAnchor = false;
                }
            }
        }

        private bool IsVisualActiveInChain(VisualItem visual)
        {
            return visual != null && visual.IsInChain && visual.RectTransform != null;
        }

        /// <summary>
        /// Scroll offset that places the given visual's center on the viewport axis.
        /// </summary>
        private float ComputeScrollOffsetToCenterVisual(VisualItem visual)
        {
            if (!IsVisualActiveInChain(visual))
            {
                return _scrollOffset;
            }

            return _scrollOffset + GetVisualAxis(visual);
        }

        private VisualItem ResolveSnapTargetVisual()
        {
            if (_snapTargetLockedUntilUserInput && IsVisualActiveInChain(_snapTargetChainVisual))
            {
                return _snapTargetChainVisual;
            }

            if (_hasSettledOrder && IsVisualActiveInChain(_settledChainVisual))
            {
                float settledAxis = GetVisualAxis(_settledChainVisual);
                if (Mathf.Abs(settledAxis) <= snap_switch_dead_zone)
                {
                    return _settledChainVisual;
                }
            }

            if (_chainHead != null)
            {
                VisualItem prefer = GetActiveRelayoutBiasVisual() ?? _settledChainVisual;
                return FindChainVisualNearestToAxis(0f, prefer);
            }

            return null;
        }

        private void BeginSnapToResolvedTargetVisual()
        {
            VisualItem targetVisual = ResolveSnapTargetVisual();
            if (targetVisual != null)
            {
                float targetOffset = ComputeScrollOffsetToCenterVisual(targetVisual);
                float targetAxis = GetVisualAxis(targetVisual);
                if (Mathf.Abs(targetAxis) <= snap_position_epsilon &&
                    Mathf.Abs(targetOffset - _scrollOffset) <= snap_position_epsilon)
                {
                    _hasSnapTarget = false;
                    _snapVelocity = 0f;
                    _hasSettledOrder = true;
                    _settledChainVisual = targetVisual;
                    _snapTargetChainVisual = targetVisual;
                    return;
                }

                _snapTargetChainVisual = targetVisual;
                _snapTargetOrder = targetVisual.AbsoluteOrderIndex;
                _snapTargetOffset = targetOffset;
            }
            else
            {
                int candidateOrder = GetNearestOrderToOffset(_scrollOffset);
                _snapTargetChainVisual = null;
                _snapTargetOrder = ClampOrderForMode(candidateOrder);
                _snapTargetOffset = GetOrderCenterPosition(_snapTargetOrder);
            }

            _hasSnapTarget = true;
            _snapTargetLockedUntilUserInput = true;
        }

        private bool IsIdleOnSettledSnapVisual()
        {
            return _hasSettledOrder &&
                   IsVisualActiveInChain(_settledChainVisual) &&
                   Mathf.Abs(GetVisualAxis(_settledChainVisual)) <= snap_switch_dead_zone;
        }

        private void RefreshCenteredVisualFromChain()
        {
            if (_chainHead == null)
            {
                _centeredChainVisual = null;
                _centeredLogicalIndex = -1;
                return;
            }

            VisualItem preferHighlight = GetActiveRelayoutBiasVisual() ?? _centeredChainVisual;

            // Hysteresis: keep the current highlighted visual while it remains
            // close enough to center, even if another item is slightly nearer.
            if (IsVisualActiveInChain(preferHighlight))
            {
                float currentAxis = GetVisualAxis(preferHighlight);
                if (Mathf.Abs(currentAxis) <= center_highlight_switch_dead_zone)
                {
                    _centeredChainVisual = preferHighlight;
                    _centeredLogicalIndex = preferHighlight.LogicalIndex;
                    return;
                }
            }

            VisualItem centered = FindChainVisualNearestToAxis(0f, preferHighlight);
            if (centered == null)
            {
                _centeredChainVisual = null;
                _centeredLogicalIndex = -1;
                return;
            }

            _centeredChainVisual = centered;
            _centeredLogicalIndex = centered.LogicalIndex;
        }

        private VisualItem ChooseRelayoutBiasSuccessor(VisualItem removedAnchor)
        {
            if (!IsVisualActiveInChain(removedAnchor))
            {
                return null;
            }

            if (IsVisualActiveInChain(removedAnchor.Next))
            {
                return removedAnchor.Next;
            }

            if (IsVisualActiveInChain(removedAnchor.Prev))
            {
                return removedAnchor.Prev;
            }

            return null;
        }

        private VisualItem GetActiveRelayoutBiasVisual()
        {
            return IsVisualActiveInChain(_relayoutBiasChainVisual) ? _relayoutBiasChainVisual : null;
        }

        private void TryAssignRelayoutBiasBeforeRemoving(
            VisualItem removedFromChain,
            int removedLogicalIndex,
            bool forceFromDeleteRequester = false)
        {
            if (!IsVisualActiveInChain(removedFromChain))
            {
                return;
            }

            bool isCenteredRemoval = forceFromDeleteRequester ||
                                     removedFromChain == _centeredChainVisual ||
                                     Mathf.Abs(GetVisualAxis(removedFromChain)) <= center_highlight_switch_dead_zone;
            if (!isCenteredRemoval)
            {
                return;
            }

            VisualItem successor = ChooseRelayoutBiasSuccessor(removedFromChain);
            if (!IsVisualActiveInChain(successor) || successor.LogicalIndex == removedLogicalIndex)
            {
                return;
            }

            _relayoutBiasChainVisual = successor;
        }

        private void EndRelayout()
        {
            _relayoutActive = false;
            _relayoutBiasChainVisual = null;
        }

        private VisualItem FindChainVisualByOrder(int order)
        {
            VisualItem v = _chainHead;
            while (v != null)
            {
                if (v.AbsoluteOrderIndex == order)
                {
                    return v;
                }
                v = v.Next;
            }
            return null;
        }

        private VisualItem FindChainVisualByLogicalIndex(int logicalIndex)
        {
            VisualItem v = _chainHead;
            while (v != null)
            {
                if (v.LogicalIndex == logicalIndex)
                {
                    return v;
                }
                v = v.Next;
            }
            return null;
        }

        private void PruneAdjacentDuplicateLogicalsOnChain()
        {
            if (_enabledIndices.Count <= 1 || _chainHead == null)
            {
                return;
            }

            bool removed;
            do
            {
                removed = false;
                VisualItem v = _chainHead;
                while (v != null && v.Next != null)
                {
                    if (v.LogicalIndex == v.Next.LogicalIndex)
                    {
                        ReleaseChainFrom(v.Next, forward: true);
                        removed = true;
                        break;
                    }

                    v = v.Next;
                }
            }
            while (removed && _chainHead != null);
        }

        /// <summary>
        /// Keeps at most one chain node per logical index (used after insert and in finite mode).
        /// </summary>
        private void PruneToSingleVisualPerLogicalOnChain()
        {
            if (_chainHead == null)
            {
                return;
            }

            var seenLogicals = new HashSet<int>();
            VisualItem v = _chainHead;
            while (v != null)
            {
                VisualItem next = v.Next;
                if (!seenLogicals.Add(v.LogicalIndex))
                {
                    DetachFromChain(v);
                    ReleaseVisual(v);
                }

                v = next;
            }
        }

        /// <summary>
        /// Infinite scroll: allow the same logical only after a full catalog lap on the chain.
        /// </summary>
        private void PrunePrematureDuplicateLogicalInstancesOnChain()
        {
            int count = _enabledIndices.Count;
            if (_chainHead == null || count <= 1 || IsFiniteMode())
            {
                return;
            }

            var firstByLogical = new Dictionary<int, VisualItem>();
            VisualItem v = _chainHead;
            while (v != null)
            {
                VisualItem next = v.Next;
                int logical = v.LogicalIndex;
                if (firstByLogical.TryGetValue(logical, out VisualItem first))
                {
                    if (Mathf.Abs(v.AbsoluteOrderIndex - first.AbsoluteOrderIndex) < count)
                    {
                        DetachFromChain(v);
                        ReleaseVisual(v);
                    }
                    else
                    {
                        firstByLogical[logical] = v;
                    }
                }
                else
                {
                    firstByLogical[logical] = v;
                }

                v = next;
            }
        }

        private void PruneDuplicateLogicalInstancesOnChain()
        {
            if (IsFiniteMode())
            {
                PruneToSingleVisualPerLogicalOnChain();
                return;
            }

            PrunePrematureDuplicateLogicalInstancesOnChain();
        }

        private bool HasChainVisualForLogicalNearOrder(int logicalIndex, int order)
        {
            int count = _enabledIndices.Count;
            if (count <= 0)
            {
                return false;
            }

            for (VisualItem v = _chainHead; v != null; v = v.Next)
            {
                if (v.LogicalIndex == logicalIndex &&
                    Mathf.Abs(v.AbsoluteOrderIndex - order) < count)
                {
                    return true;
                }
            }

            return false;
        }

        private void ShiftChainLogicalIndicesFrom(int fromLogicalInclusive, int delta)
        {
            VisualItem v = _chainHead;
            while (v != null)
            {
                if (v.LogicalIndex >= fromLogicalInclusive)
                {
                    v.LogicalIndex += delta;
                    ApplyRuntimeInfoBinding(v, v.LogicalIndex);
                    SyncVisualContentToCatalog(v);
                }
                v = v.Next;
            }
        }

        private void InsertChainLinkAfter(VisualItem pred, VisualItem insert)
        {
            if (pred == null || insert == null)
            {
                return;
            }

            VisualItem oldNext = pred.Next;
            pred.Next = insert;
            insert.Prev = pred;
            insert.Next = oldNext;
            if (oldNext != null)
            {
                oldNext.Prev = insert;
            }
            else
            {
                _chainTail = insert;
            }

            insert.IsInChain = true;
        }

        private void InsertChainLinkBefore(VisualItem succ, VisualItem insert)
        {
            if (succ == null || insert == null)
            {
                return;
            }

            VisualItem oldPrev = succ.Prev;
            insert.Next = succ;
            insert.Prev = oldPrev;
            succ.Prev = insert;
            if (oldPrev != null)
            {
                oldPrev.Next = insert;
            }
            else
            {
                _chainHead = insert;
            }

            insert.IsInChain = true;
        }

        private bool TrySpliceAddedVisualIntoChain(int logicalIndex)
        {
            if (!smooth_relayout_on_structure_change)
            {
                return false;
            }

            if (logicalIndex < 0 || logicalIndex >= _items.Count || !_items[logicalIndex].Enabled)
            {
                return false;
            }

            // Finite: one active node per logical. Infinite tiling may already show
            // this logical elsewhere on the chain — still splice the new list entry.
            if (IsFiniteMode())
            {
                VisualItem existing = FindChainVisualByLogicalIndex(logicalIndex);
                if (existing != null)
                {
                    int slot = GetEnabledSlot(logicalIndex);
                    if (slot >= 0)
                    {
                        existing.AbsoluteOrderIndex = slot;
                        if (!_relayoutActive)
                        {
                            SetVisualAxisToLatticeOrder(existing, slot);
                        }
                    }

                    return true;
                }
            }

            int newSlot = GetEnabledSlot(logicalIndex);
            if (newSlot < 0)
            {
                return false;
            }

            int order = ComputeNearestOrderForLogical(logicalIndex, _scrollOffset);
            VisualItem newVisual = AcquireAndAttachFreshVisual(order, logicalIndex);
            if (newVisual == null)
            {
                return false;
            }

            if (_chainHead == null)
            {
                InsertAsOnlyChainNode(newVisual);
                SetVisualAxisToLatticeOrder(newVisual, order);
                return true;
            }

            bool attached = TryAttachSplicedVisualByEnabledSlot(newVisual, newSlot, order);
            if (!attached)
            {
                ReleaseVisual(newVisual);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Ensures a newly added/edited logical has at least one chain visual after splice.
        /// </summary>
        private void EnsureLogicalVisualOnChain(int logicalIndex)
        {
            if (logicalIndex < 0 || logicalIndex >= _items.Count || !_items[logicalIndex].Enabled)
            {
                return;
            }

            if (IsFiniteMode() && FindChainVisualByLogicalIndex(logicalIndex) != null)
            {
                return;
            }

            int newSlot = GetEnabledSlot(logicalIndex);
            if (newSlot < 0)
            {
                return;
            }

            int order = ComputeNearestOrderForLogical(logicalIndex, _scrollOffset);
            VisualItem newVisual = AcquireAndAttachFreshVisual(order, logicalIndex);
            if (newVisual == null)
            {
                return;
            }

            if (!TryAttachSplicedVisualByEnabledSlot(newVisual, newSlot, order))
            {
                ReleaseVisual(newVisual);
            }
        }

        private bool TryAttachSplicedVisualByEnabledSlot(VisualItem newVisual, int newSlot, int order)
        {
            if (newVisual == null)
            {
                return false;
            }

            if (_chainHead == null)
            {
                InsertAsOnlyChainNode(newVisual);
                SetVisualAxisToLatticeOrder(newVisual, order);
                newVisual.AbsoluteOrderIndex = order;
                return true;
            }

            if (!TrySelectBestSpliceAnchor(newSlot, order, out VisualItem pred, out VisualItem succ, out int attachOrder))
            {
                return false;
            }

            if (pred != null && succ != null)
            {
                InsertChainLinkAfter(pred, newVisual);
                PlaceSplicedVisualBetween(newVisual, pred, succ);
            }
            else if (pred != null)
            {
                InsertChainLinkAfter(pred, newVisual);
                PlaceSplicedVisualBetween(newVisual, pred, null);
            }
            else if (succ != null)
            {
                InsertChainLinkBefore(succ, newVisual);
                PlaceSplicedVisualBetween(newVisual, null, succ);
            }
            else
            {
                return false;
            }

            if (IsFiniteMode())
            {
                newVisual.AbsoluteOrderIndex = attachOrder;
            }
            else
            {
                newVisual.AbsoluteOrderIndex = ComputeInfiniteSpliceAttachOrder(
                    newSlot,
                    order,
                    pred,
                    succ);
            }

            return true;
        }

        /// <summary>
        /// Infinite scroll: absolute order must satisfy Mod(order, count) == newSlot.
        /// Blind pred±1 / head−1 is wrong across cycle boundaries (e.g. slot 0 before head−1 → slot 9).
        /// </summary>
        private int ComputeInfiniteSpliceAttachOrder(int newSlot, int targetOrder, VisualItem pred, VisualItem succ)
        {
            int count = _enabledIndices.Count;
            if (newSlot < 0 || newSlot >= count)
            {
                return targetOrder;
            }

            int logical = _enabledIndices[newSlot];

            if (pred != null && succ != null)
            {
                int lo = pred.AbsoluteOrderIndex + 1;
                int hi = succ.AbsoluteOrderIndex - 1;
                int bestOrder = lo;
                int bestDistance = int.MaxValue;
                bool found = false;
                for (int candidate = lo; candidate <= hi; candidate++)
                {
                    if (Mod(candidate, count) != newSlot)
                    {
                        continue;
                    }

                    found = true;
                    int distance = Mathf.Abs(candidate - targetOrder);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestOrder = candidate;
                    }
                }

                if (found)
                {
                    return bestOrder;
                }
            }

            int resolvedOrder = ComputeNearestOrderForLogical(logical, _scrollOffset);
            if (succ != null)
            {
                while (resolvedOrder >= succ.AbsoluteOrderIndex)
                {
                    resolvedOrder -= count;
                }
            }
            else if (pred != null)
            {
                while (resolvedOrder <= pred.AbsoluteOrderIndex)
                {
                    resolvedOrder += count;
                }
            }

            return resolvedOrder;
        }

        private bool TrySelectBestSpliceAnchor(int newSlot, int targetOrder, out VisualItem bestPred, out VisualItem bestSucc, out int bestAttachOrder)
        {
            VisualItem candidatePred = null;
            VisualItem candidateSucc = null;
            int candidateAttachOrder = 0;

            bool found = false;
            int bestDistance = int.MaxValue;

            for (VisualItem v = _chainHead; v != null && v.Next != null; v = v.Next)
            {
                VisualItem succ = v.Next;
                if (!AreSpliceNeighborsSequential(v, succ, newSlot))
                {
                    continue;
                }

                int attachOrder = IsFiniteMode()
                    ? v.AbsoluteOrderIndex + 1
                    : ComputeInfiniteSpliceAttachOrder(newSlot, targetOrder, v, succ);
                int distance = Mathf.Abs(attachOrder - targetOrder);
                if (attachOrder == targetOrder)
                {
                    distance = 0;
                }

                if (!found || distance < bestDistance)
                {
                    found = true;
                    bestDistance = distance;
                    candidatePred = v;
                    candidateSucc = succ;
                    candidateAttachOrder = attachOrder;
                }
            }

            if (CanSpliceBeforeSucc(_chainHead, newSlot))
            {
                int attachOrder = IsFiniteMode()
                    ? _chainHead.AbsoluteOrderIndex - 1
                    : ComputeInfiniteSpliceAttachOrder(newSlot, targetOrder, null, _chainHead);
                int distance = Mathf.Abs(attachOrder - targetOrder);
                if (attachOrder == targetOrder)
                {
                    distance = 0;
                }

                if (!found || distance < bestDistance)
                {
                    found = true;
                    bestDistance = distance;
                    candidatePred = null;
                    candidateSucc = _chainHead;
                    candidateAttachOrder = attachOrder;
                }
            }

            if (CanSpliceAfterPred(_chainTail, newSlot))
            {
                int attachOrder = IsFiniteMode()
                    ? _chainTail.AbsoluteOrderIndex + 1
                    : ComputeInfiniteSpliceAttachOrder(newSlot, targetOrder, _chainTail, null);
                int distance = Mathf.Abs(attachOrder - targetOrder);
                if (attachOrder == targetOrder)
                {
                    distance = 0;
                }

                if (!found || distance < bestDistance)
                {
                    found = true;
                    candidatePred = _chainTail;
                    candidateSucc = null;
                    candidateAttachOrder = attachOrder;
                }
            }

            bestPred = candidatePred;
            bestSucc = candidateSucc;
            bestAttachOrder = candidateAttachOrder;
            return found;
        }

        private bool CanSpliceAfterPred(VisualItem predVisual, int newSlot)
        {
            if (predVisual == null)
            {
                return false;
            }

            int count = _enabledIndices.Count;
            int predSlot = GetEnabledSlot(predVisual.LogicalIndex);
            if (predSlot < 0 || newSlot < 0 || newSlot >= count)
            {
                return false;
            }

            return IsLogicallySequentialForward(predSlot, newSlot, count);
        }

        private bool CanSpliceBeforeSucc(VisualItem succVisual, int newSlot)
        {
            if (succVisual == null)
            {
                return false;
            }

            int count = _enabledIndices.Count;
            int succSlot = GetEnabledSlot(succVisual.LogicalIndex);
            if (succSlot < 0 || newSlot < 0 || newSlot >= count)
            {
                return false;
            }

            return IsLogicallySequentialForward(newSlot, succSlot, count);
        }

        /// <summary>
        /// True when <paramref name="newSlot"/> belongs between two linked chain nodes.
        /// After <see cref="ShiftChainLogicalIndicesFrom"/>, pred/succ slots may be pred+1 and pred+2
        /// (a gap); only pred→new and new→succ must be catalog-sequential, not pred→succ.
        /// </summary>
        private bool AreSpliceNeighborsSequential(VisualItem predVisual, VisualItem succVisual, int newSlot)
        {
            if (!CanSpliceAfterPred(predVisual, newSlot) || !CanSpliceBeforeSucc(succVisual, newSlot))
            {
                return false;
            }

            int count = _enabledIndices.Count;
            int predSlot = GetEnabledSlot(predVisual.LogicalIndex);
            int succSlot = GetEnabledSlot(succVisual.LogicalIndex);
            return predSlot >= 0 &&
                   succSlot >= 0 &&
                   IsLogicallySequentialForward(predSlot, newSlot, count) &&
                   IsLogicallySequentialForward(newSlot, succSlot, count);
        }

        private void PlaceSplicedVisualBetween(VisualItem newVisual, VisualItem predVisual, VisualItem succVisual)
        {
            if (predVisual != null && succVisual != null)
            {
                float axis = 0.5f * (GetVisualAxis(predVisual) + GetVisualAxis(succVisual));
                SetVisualAxis(newVisual, axis);
                return;
            }

            if (predVisual != null)
            {
                float axis = GetVisualAxis(predVisual) + GetGapBetween(predVisual, newVisual);
                SetVisualAxis(newVisual, axis);
                return;
            }

            if (succVisual != null)
            {
                float axis = GetVisualAxis(succVisual) - GetGapBetween(newVisual, succVisual);
                SetVisualAxis(newVisual, axis);
            }
        }

        private VisualItem FindChainVisualNearestToAxis(float targetAxis, VisualItem preferIfTied = null)
        {
            VisualItem best = null;
            float bestDist = float.MaxValue;
            float bestAxis = 0f;
            const float tieEpsilon = 0.05f;
            VisualItem v = _chainHead;
            while (v != null)
            {
                float axis = GetVisualAxis(v);
                float d = Mathf.Abs(axis - targetAxis);
                if (d < bestDist - tieEpsilon)
                {
                    bestDist = d;
                    best = v;
                    bestAxis = axis;
                }
                else if (best != null && d <= bestDist + tieEpsilon)
                {
                    if (preferIfTied != null)
                    {
                        if (v == preferIfTied)
                        {
                            best = v;
                            bestDist = d;
                            bestAxis = axis;
                        }
                    }
                    else if (axis > bestAxis)
                    {
                        // Deterministic tie-break: prefer the item on the positive axis side.
                        best = v;
                        bestDist = d;
                        bestAxis = axis;
                    }
                }

                v = v.Next;
            }

            if (best == null && preferIfTied != null && IsVisualActiveInChain(preferIfTied))
            {
                return preferIfTied;
            }

            return best;
        }

        private VisualItem FindChainVisualNearestToAxisForLogical(int logicalIndex, float targetAxis)
        {
            VisualItem best = null;
            float bestDist = float.MaxValue;
            VisualItem v = _chainHead;
            while (v != null)
            {
                if (v.LogicalIndex == logicalIndex)
                {
                    float dist = Mathf.Abs(GetVisualAxis(v) - targetAxis);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = v;
                    }
                }

                v = v.Next;
            }

            return best;
        }

        /// <summary>
        /// Infinite scroll: prefer the on-chain instance already near the viewport over another wrap cycle.
        /// </summary>
        private bool TryResolveScrollTargetForLogical(
            int logicalIndex,
            out VisualItem targetVisual,
            out int targetOrder,
            out float targetOffset)
        {
            targetVisual = null;
            targetOrder = 0;
            targetOffset = 0f;

            if (logicalIndex < 0 || logicalIndex >= _items.Count)
            {
                return false;
            }

            VisualItem centeredCandidate = null;
            if (IsVisualActiveInChain(_centeredChainVisual) && _centeredChainVisual.LogicalIndex == logicalIndex)
            {
                centeredCandidate = _centeredChainVisual;
            }
            else if (_hasSettledOrder &&
                     IsVisualActiveInChain(_settledChainVisual) &&
                     _settledChainVisual.LogicalIndex == logicalIndex)
            {
                centeredCandidate = _settledChainVisual;
            }
            else
            {
                centeredCandidate = FindChainVisualNearestToAxisForLogical(logicalIndex, 0f);
            }

            if (IsVisualActiveInChain(centeredCandidate) &&
                Mathf.Abs(GetVisualAxis(centeredCandidate)) <= snap_switch_dead_zone)
            {
                targetVisual = centeredCandidate;
                targetOrder = centeredCandidate.AbsoluteOrderIndex;
                targetOffset = ClampOffsetForMode(ComputeScrollOffsetToCenterVisual(centeredCandidate));
                return true;
            }

            if (!IsFiniteMode())
            {
                VisualItem onChainNearest = FindChainVisualNearestToAxisForLogical(logicalIndex, 0f);
                if (IsVisualActiveInChain(onChainNearest))
                {
                    targetVisual = onChainNearest;
                    targetOrder = onChainNearest.AbsoluteOrderIndex;
                    targetOffset = ClampOffsetForMode(ComputeScrollOffsetToCenterVisual(onChainNearest));
                    return true;
                }
            }

            targetOrder = ComputeNearestOrderForLogical(logicalIndex, _scrollOffset);
            targetOffset = ClampOffsetForMode(GetOrderCenterPosition(targetOrder));
            targetVisual = FindChainVisualByOrder(targetOrder);
            return true;
        }

        private bool IsVisualAlreadyCenteredForScroll(VisualItem visual, int logicalIndex)
        {
            return IsVisualActiveInChain(visual) &&
                   visual.LogicalIndex == logicalIndex &&
                   Mathf.Abs(GetVisualAxis(visual)) <= snap_switch_dead_zone;
        }

        private void ReleaseChainFrom(VisualItem fromInclusive, bool forward)
        {
            VisualItem cursor = fromInclusive;
            while (cursor != null)
            {
                VisualItem next = forward ? cursor.Next : cursor.Prev;
                DetachFromChain(cursor);
                ReleaseVisual(cursor);
                cursor = next;
            }
        }

        /// <summary>
        /// Extends the visible chain by spawning the next/prev enabled catalog entry at each edge.
        /// </summary>
        private void EnsureViewportCoverageFromCatalog(float halfExtent)
        {
            if (_chainHead == null || _enabledIndices.Count == 0)
            {
                return;
            }

            SpawnCatalogNeighborAtEdge(forward: false, halfExtent);
            SpawnCatalogNeighborAtEdge(forward: true, halfExtent);
        }

        /// <summary>
        /// Infinite lists: ensure every absolute order needed for the viewport exists on the chain.
        /// Needed for small catalogs (e.g. two items) where edge grow cannot wrap across chain ends.
        /// </summary>
        private void EnsureInfiniteViewportOrderCoverage(float halfExtent)
        {
            if (IsFiniteMode() || _chainHead == null || _enabledIndices.Count < 2)
            {
                return;
            }

            int centerOrder = ClampOrderForMode(GetNearestOrderToOffset(_scrollOffset));
            CollectAbsoluteOrdersCoveringViewport(centerOrder, halfExtent, _viewportOrdersScratch);
            if (_viewportOrdersScratch.Count == 0)
            {
                return;
            }

            bool inserted;
            do
            {
                inserted = false;
                for (int i = 0; i < _viewportOrdersScratch.Count; i++)
                {
                    int order = _viewportOrdersScratch[i];
                    if (FindChainVisualByOrder(order) != null)
                    {
                        continue;
                    }

                    // Order gaps (e.g. -172 then -170) make FindChainVisualByOrder miss while the
                    // chain already covers neighbors — re-sync first so we reuse nodes instead of spawning.
                    if (FindChainVisualByOrder(order - 1) != null && FindChainVisualByOrder(order + 1) != null)
                    {
                        SyncOrdersAlongChainLinks();
                        if (FindChainVisualByOrder(order) != null)
                        {
                            continue;
                        }
                    }

                    int logical = ResolveLogicalIndexFromOrder(order);
                    if (logical < 0)
                    {
                        continue;
                    }

                    if (TryInsertVisualForAbsoluteOrder(order, logical))
                    {
                        inserted = true;
                        break;
                    }
                }
            }
            while (inserted);
        }

        private bool TryInsertVisualForAbsoluteOrder(int order, int logical)
        {
            if (FindChainVisualByOrder(order) != null)
            {
                return true;
            }

            VisualItem pred = FindChainVisualByOrder(order - 1);
            VisualItem succ = FindChainVisualByOrder(order + 1);
            if (pred == null && succ == null && _chainHead != null)
            {
                return false;
            }

            VisualItem visual = AcquireAndAttachFreshVisual(order, logical);
            if (visual == null)
            {
                return false;
            }

            visual.AbsoluteOrderIndex = order;

            if (pred != null)
            {
                InsertChainLinkAfter(pred, visual);
                PlaceSplicedVisualBetween(visual, pred, succ);
                return true;
            }

            if (succ != null)
            {
                InsertChainLinkBefore(succ, visual);
                PlaceSplicedVisualBetween(visual, null, succ);
                return true;
            }

            if (_chainHead == null)
            {
                InsertAsOnlyChainNode(visual);
                SetVisualAxisToLatticeOrder(visual, order);
                return true;
            }

            ReleaseVisual(visual);
            return false;
        }

        /// <summary>
        /// Next enabled catalog neighbor from the chain edge (by slot sequence, not chain scan).
        /// </summary>
        private bool TryResolveNextCatalogNeighbor(VisualItem edge, bool forward, out int nextOrder, out int nextLogical)
        {
            nextOrder = 0;
            nextLogical = -1;
            if (edge == null)
            {
                return false;
            }

            int count = _enabledIndices.Count;
            int edgeSlot = GetEnabledSlot(edge.LogicalIndex);
            if (edgeSlot < 0 || count <= 0)
            {
                return false;
            }

            int neighborSlot = forward
                ? (IsFiniteMode() ? edgeSlot + 1 : Mod(edgeSlot + 1, count))
                : (IsFiniteMode() ? edgeSlot - 1 : Mod(edgeSlot + count - 1, count));

            if (IsFiniteMode() && (neighborSlot < 0 || neighborSlot >= count))
            {
                return false;
            }

            nextLogical = _enabledIndices[neighborSlot];
            nextOrder = edge.AbsoluteOrderIndex + (forward ? 1 : -1);

            if (IsFiniteMode())
            {
                nextOrder = neighborSlot;
                return true;
            }

            if (count > 1 && ResolveLogicalIndexFromOrder(nextOrder) != nextLogical)
            {
                return false;
            }

            return true;
        }

        private void SpawnCatalogNeighborAtEdge(bool forward, float halfExtent)
        {
            int bufferRemaining = Mathf.Max(0, buffer_item_count);
            int safety = _enabledIndices.Count + buffer_item_count + 8;
            while (safety-- > 0)
            {
                VisualItem edge = forward ? _chainTail : _chainHead;
                if (edge == null)
                {
                    return;
                }

                if (!TryResolveNextCatalogNeighbor(edge, forward, out int nextOrder, out int nextLogical))
                {
                    return;
                }

                VisualItem existingAtOrder = FindChainVisualByOrder(nextOrder);
                if (existingAtOrder != null)
                {
                    return;
                }

                if (IsFiniteMode() && FindChainVisualByLogicalIndex(nextLogical) != null)
                {
                    return;
                }

                if (!IsFiniteMode() && HasChainVisualForLogicalNearOrder(nextLogical, nextOrder))
                {
                    return;
                }

                if (_enabledIndices.Count > 1 &&
                    IsLogicalAlreadyAdjacentOnChainEdge(edge, nextLogical, forward))
                {
                    return;
                }

                float edgeAxis = GetVisualAxis(edge);
                float edgeHalf = GetVisualHalfSize(edge);
                // Anticipate the placement of the new visual to decide whether
                // it would be inside the strict viewport or fall into buffer.
                float prospectiveHalf = 0.5f * _items[nextLogical].Height;
                float gap = edgeHalf + prospectiveHalf + item_gap;
                float prospectiveAxis = edgeAxis + (forward ? gap : -gap);

                bool inStrictViewport = prospectiveAxis >= -halfExtent && prospectiveAxis <= halfExtent;
                if (!inStrictViewport && bufferRemaining <= 0)
                {
                    return;
                }

                VisualItem next = AcquireAndAttachFreshVisual(nextOrder, nextLogical);
                if (next == null)
                {
                    return;
                }

                if (forward)
                {
                    AppendToTail(next);
                }
                else
                {
                    PrependToHead(next);
                }

                // Use the freshly measured half-size for actual placement.
                float actualGap = edgeHalf + GetVisualHalfSize(next) + item_gap;
                float actualAxis = edgeAxis + (forward ? actualGap : -actualGap);
                SetVisualAxis(next, actualAxis);
                next.SnapToTargetOnPrepare = true;
                next.HiddenFramesRemaining = _pendingInitialReveal ? 0 : 1;

                if (!inStrictViewport)
                {
                    bufferRemaining--;
                }
            }
        }

        private void TrimChainCoverage(float halfExtent)
        {
            if (_chainHead == null || _deferChainTrim)
            {
                return;
            }

            int bufferAllowance = Mathf.Max(0, buffer_item_count);
            float strictMin = -halfExtent;

            int headOutside = CountChainOutsideFromHead(strictMin);
            while (_chainHead != null && _chainHead != _chainTail && headOutside > bufferAllowance)
            {
                VisualItem head = _chainHead;
                if (head == _snapTargetChainVisual)
                {
                    break;
                }

                if (GetVisualAxis(head) >= strictMin)
                {
                    break;
                }

                DetachFromChain(head);
                ReleaseVisual(head);
                headOutside--;
            }

            int tailOutside = CountChainOutsideFromTail(halfExtent);
            while (_chainTail != null && _chainHead != _chainTail && tailOutside > bufferAllowance)
            {
                VisualItem tail = _chainTail;
                if (tail == _snapTargetChainVisual)
                {
                    break;
                }

                if (GetVisualAxis(tail) <= halfExtent)
                {
                    break;
                }

                DetachFromChain(tail);
                ReleaseVisual(tail);
                tailOutside--;
            }
        }

        private int CountChainOutsideFromHead(float strictMin)
        {
            int count = 0;
            VisualItem v = _chainHead;
            while (v != null && GetVisualAxis(v) < strictMin)
            {
                count++;
                v = v.Next;
            }
            return count;
        }

        private int CountChainOutsideFromTail(float strictMax)
        {
            int count = 0;
            VisualItem v = _chainTail;
            while (v != null && GetVisualAxis(v) > strictMax)
            {
                count++;
                v = v.Prev;
            }
            return count;
        }

        private void UpdateVisualPresentation(float halfExtent)
        {
            float scaleMoveT = GetExponentialMoveT(scale_lerp_speed);

            VisualItem v = _chainHead;
            while (v != null)
            {
                if (v.RectTransform != null)
                {
                    bool isCenteredVisual = v == _centeredChainVisual;
                    float axis = GetVisualAxis(v);

                    float targetScale = 1f;
                    if (enable_distance_scaling)
                    {
                        float t = Mathf.InverseLerp(halfExtent, 0f, Mathf.Abs(axis));
                        targetScale = Mathf.Lerp(edge_scale, center_scale, t);
                    }
                    if (enable_center_highlight_scaling && isCenteredVisual)
                    {
                        targetScale += highlight_scale_boost;
                    }
                    Vector3 desiredScale = v.BaseLocalScale * targetScale;
                    v.RectTransform.localScale = Vector3.Lerp(v.RectTransform.localScale, desiredScale, scaleMoveT);

                    if (v.CanvasGroup != null && !_pendingInitialReveal)
                    {
                        if (v.HiddenFramesRemaining > 0)
                        {
                            v.HiddenFramesRemaining--;
                            v.CanvasGroup.alpha = 0f;
                            v.CanvasGroup.interactable = false;
                            v.CanvasGroup.blocksRaycasts = false;
                        }
                        else
                        {
                            v.CanvasGroup.alpha = 1f;
                            v.CanvasGroup.interactable = true;
                            v.CanvasGroup.blocksRaycasts = true;
                        }
                    }
                }

                v = v.Next;
            }
        }

        private void CheckRelayoutSettle()
        {
            if (!_relayoutActive)
            {
                return;
            }

            if (_chainHead == null)
            {
                EndRelayout();
                return;
            }

            // Relayout settles when every linked neighbor pair is at its gap.
            VisualItem v = _chainHead.Next;
            while (v != null)
            {
                float target = GetTargetAxisAfterPrev(v);
                if (Mathf.Abs(GetVisualAxis(v) - target) > relayout_settle_epsilon)
                {
                    return;
                }
                v = v.Next;
            }

            EndRelayout();
        }

        // -----------------------------------------------------------------
        // Chain link/unlink helpers
        // -----------------------------------------------------------------

        private void InsertAsOnlyChainNode(VisualItem visual)
        {
            if (visual == null)
            {
                return;
            }

            if (_chainHead != null && _chainHead != visual)
            {
                ReleaseAllChainVisuals();
            }

            visual.Prev = null;
            visual.Next = null;
            _chainHead = visual;
            _chainTail = visual;
            visual.IsInChain = true;
        }

        private void ReleaseOrphanedChainIslands()
        {
            if (_containerRect == null)
            {
                return;
            }

            _reachableChainRectsScratch.Clear();
            for (VisualItem chainVisual = _chainHead; chainVisual != null; chainVisual = chainVisual.Next)
            {
                if (chainVisual.RectTransform != null)
                {
                    _reachableChainRectsScratch.Add(chainVisual.RectTransform);
                }
            }

            for (int i = 0; i < _containerRect.childCount; i++)
            {
                RectTransform childRect = _containerRect.GetChild(i) as RectTransform;
                if (childRect == null || !childRect.gameObject.activeSelf || _reachableChainRectsScratch.Contains(childRect))
                {
                    continue;
                }

                ScrollerItemRuntimeInfo runtimeInfo = childRect.GetComponent<ScrollerItemRuntimeInfo>();
                if (runtimeInfo == null || runtimeInfo.Manager != this)
                {
                    continue;
                }

                ReleaseOrphanIslandAtRectTransform(childRect, _reachableChainRectsScratch);
            }
        }

        private void ReleaseOrphanIslandAtRectTransform(RectTransform rectTransform, HashSet<RectTransform> mainChainRects)
        {
            VisualItem seed = TryFindVisualItemForRectTransform(rectTransform);
            if (seed == null)
            {
                ScrollerItemRuntimeInfo runtimeInfo = rectTransform.GetComponent<ScrollerItemRuntimeInfo>();
                seed = new VisualItem
                {
                    RectTransform = rectTransform,
                    RuntimeInfo = runtimeInfo,
                    LogicalIndex = runtimeInfo != null ? runtimeInfo.LogicalIndex : 0,
                    CanvasGroup = rectTransform.GetComponent<CanvasGroup>(),
                };
                DetachFromChain(seed);
                ReleaseVisual(seed);
                return;
            }

            VisualItem left = seed;
            while (left.Prev != null && left.Prev.RectTransform != null && !mainChainRects.Contains(left.Prev.RectTransform))
            {
                left = left.Prev;
            }

            VisualItem cursor = left;
            while (cursor != null)
            {
                VisualItem next = cursor.Next;
                if (cursor.RectTransform != null && mainChainRects.Contains(cursor.RectTransform))
                {
                    break;
                }

                DetachFromChain(cursor);
                ReleaseVisual(cursor);
                cursor = next;
            }
        }

        private VisualItem TryFindVisualItemForRectTransform(RectTransform rectTransform)
        {
            for (VisualItem chainVisual = _chainHead; chainVisual != null; chainVisual = chainVisual.Next)
            {
                if (chainVisual.RectTransform == rectTransform)
                {
                    return chainVisual;
                }
            }

            foreach (Stack<VisualItem> pool in _pooledVisualsByLogicalIndex.Values)
            {
                foreach (VisualItem pooledVisual in pool)
                {
                    if (pooledVisual?.RectTransform == rectTransform)
                    {
                        return pooledVisual;
                    }
                }
            }

            return null;
        }

        private void AppendToTail(VisualItem visual)
        {
            if (visual == null)
            {
                return;
            }
            if (_chainTail == null)
            {
                InsertAsOnlyChainNode(visual);
                return;
            }
            visual.Prev = _chainTail;
            visual.Next = null;
            _chainTail.Next = visual;
            _chainTail = visual;
            visual.IsInChain = true;
        }

        private void PrependToHead(VisualItem visual)
        {
            if (visual == null)
            {
                return;
            }
            if (_chainHead == null)
            {
                InsertAsOnlyChainNode(visual);
                return;
            }
            visual.Next = _chainHead;
            visual.Prev = null;
            _chainHead.Prev = visual;
            _chainHead = visual;
            visual.IsInChain = true;
        }

        private void DetachFromChain(VisualItem visual)
        {
            if (visual == null || !visual.IsInChain)
            {
                return;
            }

            VisualItem prev = visual.Prev;
            VisualItem next = visual.Next;
            if (prev != null)
            {
                prev.Next = next;
            }
            if (next != null)
            {
                next.Prev = prev;
            }
            if (_chainHead == visual)
            {
                _chainHead = next;
            }
            if (_chainTail == visual)
            {
                _chainTail = prev;
            }
            visual.Prev = null;
            visual.Next = null;
            visual.IsInChain = false;
        }

        private void ReleaseAllChainVisuals()
        {
            VisualItem v = _chainHead;
            while (v != null)
            {
                VisualItem next = v.Next;
                v.Prev = null;
                v.Next = null;
                v.IsInChain = false;
                ReleaseVisual(v);
                v = next;
            }
            _chainHead = null;
            _chainTail = null;
        }

        /// <summary>
        /// Clears the chain and any active scroller wrappers still parented under the
        /// container (orphans from bad splices). ReleaseAllChainVisuals alone leaves
        /// those visible and causes duplicate copies of the same list entry.
        /// </summary>
        private void PurgeAllScrollerVisualsFromContainer()
        {
            ReleaseAllChainVisuals();

            if (_containerRect == null)
            {
                return;
            }

            for (int i = _containerRect.childCount - 1; i >= 0; i--)
            {
                RectTransform childRect = _containerRect.GetChild(i) as RectTransform;
                if (childRect == null || !childRect.gameObject.activeSelf)
                {
                    continue;
                }

                ScrollerItemRuntimeInfo runtimeInfo = childRect.GetComponent<ScrollerItemRuntimeInfo>();
                if (runtimeInfo == null || runtimeInfo.Manager != this)
                {
                    continue;
                }

                VisualItem tracked = TryFindVisualItemForRectTransform(childRect);
                if (tracked != null)
                {
                    DetachFromChain(tracked);
                    ReleaseVisual(tracked);
                }
                else
                {
                    Destroy(childRect.gameObject);
                }
            }
        }

        private static bool IsLogicalAlreadyAdjacentOnChainEdge(VisualItem edge, int logical, bool forward)
        {
            if (edge == null || logical < 0)
            {
                return false;
            }

            if (edge.LogicalIndex == logical)
            {
                return true;
            }

            VisualItem towardNeighbor = forward ? edge.Next : edge.Prev;
            return towardNeighbor != null && towardNeighbor.LogicalIndex == logical;
        }

        private void SetVisualAxisToLatticeOrder(VisualItem visual, int absoluteOrder)
        {
            if (visual == null)
            {
                return;
            }

            float axis = GetOrderCenterPosition(absoluteOrder) - _scrollOffset;
            SetVisualAxis(visual, axis);
            visual.SnapToTargetOnPrepare = true;
        }

        /// <summary>
        /// Builds the visible chain from lattice orders around the current scroll offset:
        /// one node per absolute order, ascending slot sequence, no grow-from-seed drift.
        /// </summary>
        private void RebuildChainCoveringViewport()
        {
            int count = _enabledIndices.Count;
            if (count == 0)
            {
                return;
            }

            float halfExtent = GetVisibleHalfExtent();
            int centerOrder = ClampOrderForMode(GetNearestOrderToOffset(_scrollOffset));
            CollectAbsoluteOrdersCoveringViewport(centerOrder, halfExtent, _viewportOrdersScratch);
            if (_viewportOrdersScratch.Count == 0)
            {
                return;
            }

            VisualItem prev = null;
            for (int i = 0; i < _viewportOrdersScratch.Count; i++)
            {
                int order = _viewportOrdersScratch[i];
                int logical = ResolveLogicalIndexFromOrder(order);
                if (logical < 0)
                {
                    continue;
                }

                VisualItem visual = AcquireAndAttachFreshVisual(order, logical);
                if (visual == null)
                {
                    continue;
                }

                if (prev == null)
                {
                    InsertAsOnlyChainNode(visual);
                }
                else
                {
                    AppendToTail(visual);
                }

                SetVisualAxisToLatticeOrder(visual, order);
                prev = visual;
            }
        }

        private float GetBufferAxisExtent()
        {
            int count = _enabledIndices.Count;
            if (count <= 0 || buffer_item_count <= 0)
            {
                return 0f;
            }

            float spanSum = 0f;
            for (int i = 0; i < count; i++)
            {
                spanSum += Mathf.Max(1f, _items[_enabledIndices[i]].Height + item_gap);
            }

            float averageSpan = spanSum / count;
            return buffer_item_count * averageSpan;
        }

        private void CollectAbsoluteOrdersCoveringViewport(int centerOrder, float halfExtent, List<int> ordersOut)
        {
            ordersOut.Clear();
            int count = _enabledIndices.Count;
            if (count <= 0)
            {
                return;
            }

            float bufferExtent = GetBufferAxisExtent();
            float maxAxis = halfExtent + bufferExtent;
            float minAxis = -halfExtent - bufferExtent;
            int safety = count * 4 + (buffer_item_count * 4) + 16;

            ordersOut.Add(centerOrder);

            for (int step = 1; step < safety; step++)
            {
                int order = centerOrder + step;
                if (IsFiniteMode() && order >= count)
                {
                    break;
                }

                float axis = GetOrderCenterPosition(order) - _scrollOffset;
                if (axis > maxAxis)
                {
                    break;
                }

                ordersOut.Add(order);
            }

            int backwardCount = 0;
            for (int step = 1; step < safety; step++)
            {
                int order = centerOrder - step;
                if (IsFiniteMode() && order < 0)
                {
                    break;
                }

                float axis = GetOrderCenterPosition(order) - _scrollOffset;
                if (axis < minAxis)
                {
                    break;
                }

                backwardCount++;
            }

            if (backwardCount > 0)
            {
                var backward = new List<int>(backwardCount);
                for (int step = backwardCount; step >= 1; step--)
                {
                    backward.Add(centerOrder - step);
                }

                backward.AddRange(ordersOut);
                ordersOut.Clear();
                ordersOut.AddRange(backward);
            }

            ordersOut.Sort();
        }

        private void LogChainInvariantFailureIfNeeded(string phase)
        {
            if (ValidateChainInvariants(out string reason))
            {
                return;
            }

            Debug.LogWarning($"ScrollerManager chain invariant failed during {phase}: {reason}");
        }

        private bool ValidateChainInvariants(out string reason)
        {
            reason = string.Empty;
            if (_chainHead == null)
            {
                return true;
            }

            if (_chainHead.Prev != null)
            {
                reason = "head prev link not null";
                return false;
            }

            HashSet<int> seenOrders = new HashSet<int>();
            int count = _enabledIndices.Count;
            int? previousOrder = null;
            VisualItem current = _chainHead;
            while (current != null)
            {
                if (GetEnabledSlot(current.LogicalIndex) < 0)
                {
                    reason = $"disabled logical in chain: {current.LogicalIndex}";
                    return false;
                }

                if (!seenOrders.Add(current.AbsoluteOrderIndex))
                {
                    reason = $"duplicate absolute order in chain: {current.AbsoluteOrderIndex}";
                    return false;
                }

                if (ResolveLogicalIndexFromOrder(current.AbsoluteOrderIndex) != current.LogicalIndex)
                {
                    reason = $"order/logical mismatch at order {current.AbsoluteOrderIndex}";
                    return false;
                }

                if (previousOrder.HasValue && current.AbsoluteOrderIndex <= previousOrder.Value)
                {
                    reason = "non-increasing absolute orders";
                    return false;
                }

                if (current.Next != null)
                {
                    int slotCurrent = GetEnabledSlot(current.LogicalIndex);
                    int slotNext = GetEnabledSlot(current.Next.LogicalIndex);
                    if (slotCurrent < 0 || slotNext < 0 || !IsLogicallySequentialForward(slotCurrent, slotNext, count))
                    {
                        reason = "non-sequential enabled slots";
                        return false;
                    }

                    if (count > 1 && current.LogicalIndex == current.Next.LogicalIndex)
                    {
                        reason = "adjacent duplicate logicals";
                        return false;
                    }
                }

                previousOrder = current.AbsoluteOrderIndex;
                if (current.Next == null && _chainTail != current)
                {
                    reason = "tail pointer mismatch";
                    return false;
                }

                current = current.Next;
            }

            return true;
        }

        // -----------------------------------------------------------------
        // Pool helpers + visual acquisition
        // -----------------------------------------------------------------

        private VisualItem AcquireAndAttachFreshVisual(int absoluteOrder, int logicalIndex)
        {
            if (logicalIndex < 0 || logicalIndex >= _items.Count)
            {
                return null;
            }

            VisualItem visual = AcquireVisualFromPool(logicalIndex);
            visual.AbsoluteOrderIndex = absoluteOrder;
            visual.LogicalIndex = logicalIndex;
            visual.SnapToTargetOnPrepare = true;
            visual.HiddenFramesRemaining = _pendingInitialReveal ? 0 : 1;
            ApplyRuntimeInfoBinding(visual, logicalIndex);
            SyncVisualContentToCatalog(visual);

            if (visual.RectTransform != null)
            {
                visual.RectTransform.SetParent(_containerRect, false);
                visual.RectTransform.gameObject.SetActive(true);
                if (visual.CanvasGroup != null)
                {
                    bool isVisible = !_pendingInitialReveal && visual.HiddenFramesRemaining <= 0;
                    visual.CanvasGroup.alpha = isVisible ? 1f : 0f;
                    visual.CanvasGroup.interactable = isVisible;
                    visual.CanvasGroup.blocksRaycasts = isVisible;
                }
            }

            RequestVisualContentRefresh(visual.RuntimeInfo);
            return visual;
        }

        private VisualItem AcquireVisualFromPool(int logicalIndex)
        {
            int poolKey = GetVisualPoolKey(logicalIndex);
            if (!_pooledVisualsByLogicalIndex.TryGetValue(poolKey, out Stack<VisualItem> pool))
            {
                pool = new Stack<VisualItem>();
                _pooledVisualsByLogicalIndex[poolKey] = pool;
            }

            while (pool.Count > 0)
            {
                VisualItem pooledVisual = pool.Pop();
                if (pooledVisual != null && pooledVisual.RectTransform != null)
                {
                    return pooledVisual;
                }
            }

            VisualItem inactive = TryClaimInactiveScrollerVisual(logicalIndex, poolKey);
            if (inactive != null)
            {
                return inactive;
            }

            return CreateNewVisualWrapper(logicalIndex);
        }

        /// <summary>
        /// Reclaims inactive scroller wrappers that fell off the pool stack (e.g. after bad splices)
        /// before allocating another prefab instance.
        /// </summary>
        private VisualItem TryClaimInactiveScrollerVisual(int logicalIndex, int poolKey)
        {
            if (_containerRect == null || logicalIndex < 0 || logicalIndex >= _items.Count)
            {
                return null;
            }

            GameObject expectedPrefab = _items[logicalIndex].Prefab;
            for (int i = 0; i < _containerRect.childCount; i++)
            {
                RectTransform childRect = _containerRect.GetChild(i) as RectTransform;
                if (childRect == null || childRect.gameObject.activeSelf)
                {
                    continue;
                }

                ScrollerItemRuntimeInfo runtimeInfo = childRect.GetComponent<ScrollerItemRuntimeInfo>();
                if (runtimeInfo == null || runtimeInfo.Manager != this)
                {
                    continue;
                }

                if (TryFindVisualItemForRectTransform(childRect) != null)
                {
                    continue;
                }

                if (!InactiveScrollerVisualMatchesPoolKey(runtimeInfo, logicalIndex, poolKey, expectedPrefab))
                {
                    continue;
                }

                return new VisualItem
                {
                    LogicalIndex = logicalIndex,
                    BoundContentPrefab = expectedPrefab,
                    RectTransform = childRect,
                    CanvasGroup = childRect.GetComponent<CanvasGroup>(),
                    RuntimeInfo = runtimeInfo,
                    BaseLocalScale = childRect.localScale,
                    SnapToTargetOnPrepare = true,
                    HalfSizeAxis = 0.5f * _items[logicalIndex].Height,
                    HasMeasuredHalfSize = false,
                };
            }

            return null;
        }

        private bool InactiveScrollerVisualMatchesPoolKey(
            ScrollerItemRuntimeInfo runtimeInfo,
            int logicalIndex,
            int poolKey,
            GameObject expectedPrefab)
        {
            if (runtimeInfo == null)
            {
                return false;
            }

            if (IsSharedVisualRecycleMode())
            {
                return true;
            }

            if (runtimeInfo.LogicalIndex >= 0 &&
                runtimeInfo.LogicalIndex < _items.Count &&
                GetVisualPoolKey(runtimeInfo.LogicalIndex) == poolKey)
            {
                return true;
            }

            if (expectedPrefab == null || runtimeInfo.ContentRect == null)
            {
                return false;
            }

            string expectedContentName = expectedPrefab.name + "_ScrollerItem";
            return runtimeInfo.ContentRect.name == expectedContentName;
        }

        private VisualItem CreateNewVisualWrapper(int logicalIndex)
        {
            GameObject wrapper = new GameObject(
                _items[logicalIndex].Prefab.name + "_ScrollerSlot",
                typeof(RectTransform),
                typeof(ScrollerItemRuntimeInfo),
                typeof(ContentSizeFitter),
                typeof(CanvasGroup));
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
            runtimeInfo.Initialize(-1, -1, wrapperRect, contentRect);
            runtimeInfo.SetManager(this);
            CanvasGroup canvasGroup = wrapper.GetComponent<CanvasGroup>();
            canvasGroup.alpha = 1f;
            canvasGroup.interactable = true;
            canvasGroup.blocksRaycasts = true;

            GameObject sourcePrefab = _items[logicalIndex].Prefab;
            return new VisualItem
            {
                LogicalIndex = logicalIndex,
                BoundContentPrefab = sourcePrefab,
                RectTransform = wrapperRect,
                CanvasGroup = canvasGroup,
                RuntimeInfo = runtimeInfo,
                BaseLocalScale = wrapperRect.localScale,
                SnapToTargetOnPrepare = true,
                HalfSizeAxis = 0.5f * _items[logicalIndex].Height,
                HasMeasuredHalfSize = false,
            };
        }

        private void ReleaseVisual(VisualItem visual)
        {
            if (visual == null || visual.RectTransform == null)
            {
                return;
            }

            if (visual.IsInChain)
            {
                DetachFromChain(visual);
            }

            if (visual.RuntimeInfo != null)
            {
                visual.RuntimeInfo.SetCentered(false);
            }
            if (visual == _centeredChainVisual)
            {
                _centeredChainVisual = null;
            }
            if (visual == _snapTargetChainVisual)
            {
                _snapTargetChainVisual = null;
            }
            if (visual == _settledChainVisual)
            {
                _settledChainVisual = null;
                _hasSettledOrder = false;
            }
            if (visual == _relayoutBiasChainVisual)
            {
                _relayoutBiasChainVisual = null;
            }
            visual.HiddenFramesRemaining = 0;

            int poolKey = GetVisualPoolKey(visual.LogicalIndex);
            if (!_pooledVisualsByLogicalIndex.TryGetValue(poolKey, out Stack<VisualItem> pool))
            {
                pool = new Stack<VisualItem>();
                _pooledVisualsByLogicalIndex[poolKey] = pool;
            }

            visual.RectTransform.gameObject.SetActive(false);
            pool.Push(visual);
        }

        private bool ApplyRuntimeInfoBinding(VisualItem visual, int logicalIndex)
        {
            if (visual == null || visual.RuntimeInfo == null || logicalIndex < 0 || logicalIndex >= _items.Count)
            {
                return false;
            }

            int dataIndex = _items[logicalIndex].DataIndex;
            bool bindingChanged = visual.RuntimeInfo.LogicalIndex != logicalIndex ||
                                  visual.RuntimeInfo.DataIndex != dataIndex;

            visual.RuntimeInfo.SetLogicalIndex(logicalIndex);
            visual.RuntimeInfo.SetDataIndex(dataIndex);
            visual.RuntimeInfo.SetManager(this);
            if (bindingChanged)
            {
                RequestVisualContentRefresh(visual.RuntimeInfo, scheduleDeferredOnly: false);
            }

            return bindingChanged;
        }

        /// <summary>
        /// Re-instantiates pooled content when the logical now maps to a different prefab.
        /// </summary>
        private void SyncVisualContentToCatalog(VisualItem visual)
        {
            if (visual?.RuntimeInfo == null || visual.LogicalIndex < 0 || visual.LogicalIndex >= _items.Count)
            {
                return;
            }

            GameObject expectedPrefab = _items[visual.LogicalIndex].Prefab;
            if (expectedPrefab == null)
            {
                return;
            }

            if (visual.BoundContentPrefab == expectedPrefab)
            {
                return;
            }

            RectTransform wrapperRect = visual.RuntimeInfo.WrapperRect;
            if (wrapperRect == null)
            {
                return;
            }

            RectTransform oldContent = visual.RuntimeInfo.ContentRect;
            if (oldContent != null)
            {
                Destroy(oldContent.gameObject);
            }

            GameObject content = Instantiate(expectedPrefab, wrapperRect);
            content.name = expectedPrefab.name + "_ScrollerItem";
            RectTransform contentRect = content.GetComponent<RectTransform>();
            if (contentRect == null)
            {
                contentRect = content.AddComponent<RectTransform>();
            }

            visual.RuntimeInfo.Initialize(
                visual.LogicalIndex,
                _items[visual.LogicalIndex].DataIndex,
                wrapperRect,
                contentRect);
            visual.RuntimeInfo.SetManager(this);
            visual.BoundContentPrefab = expectedPrefab;
            visual.HalfSizeAxis = 0.5f * _items[visual.LogicalIndex].Height;
            visual.HasMeasuredHalfSize = false;
            RequestVisualContentRefresh(visual.RuntimeInfo, scheduleDeferredOnly: false);
        }

        /// <summary>
        /// Finite lists: every enabled catalog entry must have exactly one chain visual after insert.
        /// </summary>
        private void EnsureMissingEnabledLogicalsOnChain()
        {
            if (!IsFiniteMode() || _enabledIndices.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _enabledIndices.Count; i++)
            {
                int logical = _enabledIndices[i];
                if (FindChainVisualByLogicalIndex(logical) != null)
                {
                    continue;
                }

                int slot = GetEnabledSlot(logical);
                if (slot < 0)
                {
                    continue;
                }

                int order = slot;
                VisualItem visual = AcquireAndAttachFreshVisual(order, logical);
                if (visual == null)
                {
                    continue;
                }

                if (_chainHead == null)
                {
                    InsertAsOnlyChainNode(visual);
                    SetVisualAxisToLatticeOrder(visual, order);
                    continue;
                }

                if (!TryAttachSplicedVisualByEnabledSlot(visual, slot, order))
                {
                    ReleaseVisual(visual);
                }
            }

            PruneToSingleVisualPerLogicalOnChain();
        }

        private void RequestVisualContentRefresh(ScrollerItemRuntimeInfo runtimeInfo, bool scheduleDeferredOnly = false)
        {
            if (runtimeInfo == null)
            {
                return;
            }

            if (!scheduleDeferredOnly)
            {
                runtimeInfo.NotifyContentRefreshRequested();
            }

            for (int i = 0; i < _pendingContentRefreshes.Count; i++)
            {
                if (_pendingContentRefreshes[i] == runtimeInfo)
                {
                    return;
                }
            }

            _pendingContentRefreshes.Add(runtimeInfo);
        }

        // -----------------------------------------------------------------
        // Size refresh
        // -----------------------------------------------------------------

        private bool RefreshItemSizesOnTick()
        {
            if (!enable_runtime_size_checks)
            {
                return false;
            }
            if (_items.Count == 0 || _chainHead == null)
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

            VisualItem v = _chainHead;
            while (v != null)
            {
                if (v.RectTransform == null || v.LogicalIndex < 0 || v.LogicalIndex >= _items.Count)
                {
                    v = v.Next;
                    continue;
                }

                float measured = MeasureVisualPrimarySizeInContainer(v);
                if (measured <= 0.01f)
                {
                    measured = _items[v.LogicalIndex].Height;
                }

                if (!v.HasMeasuredHalfSize)
                {
                    v.HalfSizeAxis = 0.5f * measured;
                    v.HasMeasuredHalfSize = true;
                    anyChanged = true;
                }
                else
                {
                    float currentSize = v.HalfSizeAxis * 2f;
                    float next = Mathf.MoveTowards(currentSize, measured, maxStep);
                    if (Mathf.Abs(next - currentSize) > size_refresh_epsilon)
                    {
                        v.HalfSizeAxis = 0.5f * next;
                        anyChanged = true;
                    }
                    else if (Mathf.Abs(measured - currentSize) <= size_refresh_epsilon)
                    {
                        v.HalfSizeAxis = 0.5f * measured;
                    }
                }

                // Update the canonical item height so future calculations
                // converge toward the latest measurement.
                if (Mathf.Abs(_items[v.LogicalIndex].Height - measured) > size_refresh_epsilon)
                {
                    _items[v.LogicalIndex].Height = measured;
                    anyChanged = true;
                }

                v = v.Next;
            }

            if (anyChanged)
            {
                RebuildEnabledSpacingData();
                return true;
            }

            return false;
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

            if (visual.RectTransform != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(visual.RectTransform);
            }

            return ScrollerAxisAdapter.MeasureRectInContainer(_containerRect, targetRect, _worldCornersBuffer, scroll_axis);
        }

        // -----------------------------------------------------------------
        // Structure change
        // -----------------------------------------------------------------

        private void ApplyStructureChangeAndRefreshVisuals(bool preserveExistingVisuals = true)
        {
            bool wantsRelayout = preserveExistingVisuals && smooth_relayout_on_structure_change;
            if (!wantsRelayout)
            {
                _relayoutBiasChainVisual = null;
            }

            // Capture the visual we want to keep "still" through the structure
            // change: the chain visual currently nearest the viewport center.
            // After the spacing rebuilds we re-anchor _scrollOffset so this
            // visual stays at the same on-screen axis.
            TryCaptureStabilityAnchorForStructureChange(
                out VisualItem stabilityAnchor,
                out float stabilityAxis,
                out VisualItem relayoutBiasSuccessor);

            if (wantsRelayout)
            {
                // Do not clear _scrollVelocity — the user may be scrolling while
                // items are removed. Only interrupt snap so it does not fight relayout.
                ClearActiveSnapState();
            }

            RefreshEnabledIndices();
            SyncSinglePrefabCountFromEnabledItems();

            if (_enabledIndices.Count == 0)
            {
                ReleaseAllChainVisuals();
                _centeredLogicalIndex = -1;
                _centeredChainVisual = null;
                _lastCenteredBroadcastVisual = null;
                ResetMotionStateForEmptyScroller();
                EndRelayout();
                RefreshLinkedScrollbarState();
                return;
            }

            // Drop any chain visuals whose logical is no longer enabled.
            PruneInvalidChainVisuals();

            // PurgeVisualsForLogicalIndex may have already set _relayoutBiasChainVisual
            // while the removed node was still linked.
            if (!IsVisualActiveInChain(_relayoutBiasChainVisual))
            {
                bool stabilityAnchorRemoved = stabilityAnchor != null && !IsVisualActiveInChain(stabilityAnchor);
                if (wantsRelayout && stabilityAnchorRemoved && IsVisualActiveInChain(relayoutBiasSuccessor))
                {
                    _relayoutBiasChainVisual = relayoutBiasSuccessor;
                }
            }

            ConsumePendingMutation(out MutationKind mutationKind, out int mutationLogicalIndex);

            // Re-anchor _scrollOffset so the stability visual's logical lands on
            // its original screen axis after the new spacing is applied. If the
            // original stability visual was disabled itself, pick a replacement
            // that's closest to where the old anchor sat.
            if (wantsRelayout && _chainHead != null)
            {
                VisualItem reAnchor = null;
                float reAnchorAxis = stabilityAxis;
                if (IsVisualActiveInChain(_relayoutBiasChainVisual))
                {
                    reAnchor = _relayoutBiasChainVisual;
                    reAnchorAxis = GetVisualAxis(reAnchor);
                }
                else if (stabilityAnchor != null && stabilityAnchor.IsInChain)
                {
                    reAnchor = stabilityAnchor;
                }
                else
                {
                    reAnchor = FindChainVisualNearestToAxis(stabilityAxis, relayoutBiasSuccessor);
                    if (reAnchor != null)
                    {
                        reAnchorAxis = GetVisualAxis(reAnchor);
                    }
                }

                if (reAnchor != null && IsLogicalEnabled(reAnchor.LogicalIndex))
                {
                    int newOrder = ComputeNearestOrderForLogical(reAnchor.LogicalIndex, _scrollOffset);
                    float newLattice = GetOrderCenterPosition(newOrder);
                    _scrollOffset = newLattice - reAnchorAxis;

                    if (TryComputeMutationPreferredOffset(mutationKind, mutationLogicalIndex, out float preferredOffset))
                    {
                        _scrollOffset = preferredOffset;
                    }
                }
                else
                {
                    // No usable in-chain anchor remained; reset the chain so a
                    // fresh anchor builds during the upcoming SyncVisibleWindow.
                    ReleaseAllChainVisuals();
                    _relayoutBiasChainVisual = null;
                }
            }

            _relayoutActive = wantsRelayout;
            _skipCollectiveScrollThisFrame = true;
            _lastScrollOffsetForChainMotion = _scrollOffset;

            ApplyPendingMutationToChain(mutationKind, mutationLogicalIndex);
            NormalizeMotionStateAfterStructureChange();
            _validateChainThisFrame = true;

            SyncVisibleWindow();
            RefreshLinkedScrollbarState();
        }

        private void ApplyPendingMutationToChain(MutationKind mutationKind, int mutationLogicalIndex)
        {
            if (_enabledIndices.Count == 0)
            {
                return;
            }

            switch (mutationKind)
            {
                case MutationKind.Insert:
                    if (!TrySpliceAddedVisualIntoChain(mutationLogicalIndex))
                    {
                        EnsureLogicalVisualOnChain(mutationLogicalIndex);
                    }

                    if (IsFiniteMode())
                    {
                        PruneToSingleVisualPerLogicalOnChain();
                        EnsureMissingEnabledLogicalsOnChain();
                    }

                    ReleaseOrphanedChainIslands();
                    if (!IsFiniteMode() && _chainHead != null)
                    {
                        SyncOrdersAlongChainLinks();
                    }
                    break;

                case MutationKind.Remove:
                    if (mutationLogicalIndex >= 0)
                    {
                        PurgeVisualsForLogicalIndex(mutationLogicalIndex);
                    }
                    break;

                case MutationKind.Rebuild:
                    PurgeAllScrollerVisualsFromContainer();
                    RebuildChainCoveringViewport();
                    break;
            }
        }

        private bool TryComputeMutationPreferredOffset(MutationKind mutationKind, int mutationLogicalIndex, out float preferredOffset)
        {
            preferredOffset = 0f;
            if (mutationKind != MutationKind.Insert || mutationLogicalIndex < 0)
            {
                return false;
            }

            int slot = GetEnabledSlot(mutationLogicalIndex);
            if (slot != 0 || _enabledIndices.Count == 0)
            {
                return false;
            }

            if (_enabledIndices.Count == 1)
            {
                int order = IsFiniteMode() ? 0 : ComputeNearestOrderForLogical(mutationLogicalIndex, 0f);
                preferredOffset = GetOrderCenterPosition(order);
                return true;
            }

            float frontSpan = _enabledPrefixPositions[1];
            float nearStartThreshold = Mathf.Max(1f, 0.5f * (_items[mutationLogicalIndex].Height + item_gap));
            if (Mathf.Abs(_scrollOffset) <= nearStartThreshold)
            {
                int newOrder = IsFiniteMode() ? 0 : ComputeNearestOrderForLogical(mutationLogicalIndex, 0f);
                preferredOffset = GetOrderCenterPosition(newOrder);
                return true;
            }

            preferredOffset = _scrollOffset + frontSpan;
            return true;
        }

        private void PruneInvalidChainVisuals()
        {
            VisualItem v = _chainHead;
            while (v != null)
            {
                VisualItem next = v.Next;
                int logical = v.LogicalIndex;
                if (!IsLogicalEnabled(logical))
                {
                    TryAssignRelayoutBiasBeforeRemoving(v, logical);
                    DetachFromChain(v);
                    ReleaseVisual(v);
                }
                v = next;
            }
        }

        private int ComputeNearestOrderForLogical(int logicalIndex, float referenceOffset)
        {
            int orderInEnabled = GetEnabledSlot(logicalIndex);
            if (orderInEnabled < 0)
            {
                return 0;
            }

            if (IsFiniteMode())
            {
                return orderInEnabled;
            }

            int count = _enabledIndices.Count;
            float prefix = _enabledPrefixPositions[orderInEnabled];
            int cycle = Mathf.RoundToInt((referenceOffset - prefix) / _enabledCycleLength);
            int candidate = (cycle * count) + orderInEnabled;
            return RefineOrderToNearestCycle(candidate, referenceOffset);
        }

        private void ClearActiveSnapState()
        {
            _snapVelocity = 0f;
            _hasSnapTarget = false;
            _snapTargetLockedUntilUserInput = false;
            _snapTargetChainVisual = null;
        }

        private void InterruptPassiveSnapForScrollDrive()
        {
            ClearActiveSnapState();
            _hasProgrammaticStepAnchor = false;
        }

        private void ClearScrollbarDriveState()
        {
            _scrollbarPointerHeld = false;
            _scrollbarOffsetLeadsChain = false;
            _scrollbarScrollVelocity = 0f;
        }

        private void ResetMotionStateForEmptyScroller()
        {
            _scrollOffset = 0f;
            _scrollVelocity = 0f;
            ClearActiveSnapState();
            ClearScrollbarDriveState();
            _hasSettledOrder = false;
            _settledChainVisual = null;
            _hasProgrammaticStepAnchor = false;
        }

        private bool DoesScrollOffsetLeadChain()
        {
            return IsUserActivelyScrolling() ||
                   _hasSnapTarget ||
                   _hasProgrammaticStepAnchor ||
                   _scrollbarOffsetLeadsChain;
        }

        private float GetExponentialMoveT(float strength)
        {
            return 1f - Mathf.Exp(-Mathf.Max(0.01f, strength) * Time.deltaTime);
        }

        private float GetSpringMoveT(bool passiveRelayout)
        {
            float strength = passiveRelayout ? relayout_lerp_speed : chain_spring_strength;
            return GetExponentialMoveT(strength);
        }

        private bool TryCaptureStabilityAnchorForStructureChange(
            out VisualItem stabilityAnchor,
            out float stabilityAxis,
            out VisualItem relayoutBiasSuccessor)
        {
            stabilityAnchor = null;
            stabilityAxis = 0f;
            relayoutBiasSuccessor = null;

            if (_chainHead == null)
            {
                return false;
            }

            stabilityAnchor = IsVisualActiveInChain(_centeredChainVisual)
                ? _centeredChainVisual
                : FindChainVisualNearestToAxis(0f, _centeredChainVisual);
            if (stabilityAnchor == null)
            {
                return false;
            }

            stabilityAxis = GetVisualAxis(stabilityAnchor);
            relayoutBiasSuccessor = ChooseRelayoutBiasSuccessor(stabilityAnchor);
            return true;
        }

        private float EffectiveScrollbarValue(float normalizedValue)
        {
            float value = Mathf.Clamp01(normalizedValue);
            return invert_scrollbar_value ? 1f - value : value;
        }

        private float GetScrollOffsetFromNormalizedScrollbar(float effectiveValue)
        {
            GetFiniteOffsetBounds(out float minOffset, out float maxOffset);
            return ClampOffsetForMode(Mathf.Lerp(minOffset, maxOffset, effectiveValue));
        }

        private float GetNormalizedScrollbarValueFromOffset()
        {
            GetFiniteOffsetBounds(out float minOffset, out float maxOffset);
            float range = Mathf.Max(0f, maxOffset - minOffset);
            if (range <= 0.0001f)
            {
                return 0f;
            }

            float normalized = Mathf.Clamp01((_scrollOffset - minOffset) / range);
            return invert_scrollbar_value ? 1f - normalized : normalized;
        }

        private void SyncSinglePrefabCountFromEnabledItems()
        {
            if (item_source_mode != ScrollerItemSourceMode.SinglePrefabWithCount)
            {
                return;
            }

            single_prefab_count = _enabledIndices.Count;
        }

        private void NormalizeMotionStateAfterStructureChange()
        {
            if (_enabledIndices.Count == 0)
            {
                ResetMotionStateForEmptyScroller();
                return;
            }

            if (!_relayoutActive || !IsFiniteMode())
            {
                _scrollOffset = ClampOffsetForMode(_scrollOffset);
            }

            if (_hasSnapTarget)
            {
                _snapTargetOrder = ClampOrderForMode(_snapTargetOrder);
                _snapTargetOffset = GetOrderCenterPosition(_snapTargetOrder);
            }

            if (_hasSettledOrder && !IsVisualActiveInChain(_settledChainVisual))
            {
                _hasSettledOrder = false;
                _settledChainVisual = null;
            }
            else if (_hasSettledOrder && IsVisualActiveInChain(_settledChainVisual) &&
                     !IsLogicalEnabled(_settledChainVisual.LogicalIndex))
            {
                _hasSettledOrder = false;
                _settledChainVisual = null;
            }

            if (_hasProgrammaticStepAnchor)
            {
                _programmaticStepOrder = ClampOrderForMode(_programmaticStepOrder);
            }
        }

        private void PurgeVisualsForLogicalIndex(int logicalIndex, ScrollerItemRuntimeInfo requester = null)
        {
            // Pick the relayout bias winner from the deleteSelf instance (or the copy
            // nearest center) BEFORE any detach — purging every matching logical can
            // reorder the chain and invalidate Next/Prev on the centered node.
            VisualItem biasSource = null;
            bool forceBias = false;
            float bestDist = float.MaxValue;
            VisualItem v = _chainHead;
            while (v != null)
            {
                if (v.LogicalIndex == logicalIndex)
                {
                    if (requester != null && v.RuntimeInfo == requester)
                    {
                        biasSource = v;
                        forceBias = true;
                    }
                    else if (!forceBias)
                    {
                        float dist = Mathf.Abs(GetVisualAxis(v));
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            biasSource = v;
                        }
                    }
                }

                v = v.Next;
            }

            if (biasSource != null)
            {
                TryAssignRelayoutBiasBeforeRemoving(biasSource, logicalIndex, forceBias);
            }

            v = _chainHead;
            while (v != null)
            {
                VisualItem next = v.Next;
                if (v.LogicalIndex == logicalIndex)
                {
                    DetachFromChain(v);
                    if (IsSharedVisualRecycleMode())
                    {
                        ReleaseVisual(v);
                    }
                    else if (v.RectTransform != null)
                    {
                        Destroy(v.RectTransform.gameObject);
                    }
                }

                v = next;
            }

            int poolKey = GetVisualPoolKey(logicalIndex);
            if (!_pooledVisualsByLogicalIndex.TryGetValue(poolKey, out Stack<VisualItem> pool))
            {
                return;
            }

            if (IsSharedVisualRecycleMode())
            {
                _poolFilterScratch.Clear();
                while (pool.Count > 0)
                {
                    VisualItem pooledVisual = pool.Pop();
                    if (pooledVisual == null || pooledVisual.RectTransform == null)
                    {
                        continue;
                    }

                    if (pooledVisual.LogicalIndex == logicalIndex)
                    {
                        Destroy(pooledVisual.RectTransform.gameObject);
                    }
                    else
                    {
                        _poolFilterScratch.Push(pooledVisual);
                    }
                }

                while (_poolFilterScratch.Count > 0)
                {
                    pool.Push(_poolFilterScratch.Pop());
                }
            }
            else
            {
                while (pool.Count > 0)
                {
                    VisualItem pooledVisual = pool.Pop();
                    if (pooledVisual != null && pooledVisual.RectTransform != null)
                    {
                        Destroy(pooledVisual.RectTransform.gameObject);
                    }
                }

                _pooledVisualsByLogicalIndex.Remove(poolKey);
            }
        }

        private void DeactivateAllActiveVisuals()
        {
            VisualItem v = _chainHead;
            while (v != null)
            {
                VisualItem next = v.Next;
                v.Prev = null;
                v.Next = null;
                v.IsInChain = false;
                ReleaseVisual(v);
                v = next;
            }
            _chainHead = null;
            _chainTail = null;
        }

        private void DestroyAllVisualsAndClearPools()
        {
            VisualItem v = _chainHead;
            while (v != null)
            {
                VisualItem next = v.Next;
                if (v.RectTransform != null)
                {
                    Destroy(v.RectTransform.gameObject);
                }
                v = next;
            }
            _chainHead = null;
            _chainTail = null;

            foreach (KeyValuePair<int, Stack<VisualItem>> kvp in _pooledVisualsByLogicalIndex)
            {
                Stack<VisualItem> pool = kvp.Value;
                while (pool.Count > 0)
                {
                    VisualItem pooled = pool.Pop();
                    if (pooled != null && pooled.RectTransform != null)
                    {
                        Destroy(pooled.RectTransform.gameObject);
                    }
                }
            }
            _pooledVisualsByLogicalIndex.Clear();

            _centeredLogicalIndex = -1;
            _centeredChainVisual = null;
            _lastCenteredBroadcastVisual = null;
            _snapTargetChainVisual = null;
            _settledChainVisual = null;
        }

        private void SetVisibilityForChainVisuals(bool isVisible)
        {
            VisualItem v = _chainHead;
            while (v != null)
            {
                if (v.CanvasGroup != null)
                {
                    v.CanvasGroup.alpha = isVisible ? 1f : 0f;
                    v.CanvasGroup.interactable = isVisible;
                    v.CanvasGroup.blocksRaycasts = isVisible;
                }
                v = v.Next;
            }
        }

        private void BroadcastCenteredState()
        {
            if (_centeredChainVisual == _lastCenteredBroadcastVisual)
            {
                return;
            }

            if (_lastCenteredBroadcastVisual != null && _lastCenteredBroadcastVisual.RuntimeInfo != null)
            {
                _lastCenteredBroadcastVisual.RuntimeInfo.SetCentered(false);
            }

            if (_centeredChainVisual != null && _centeredChainVisual.RuntimeInfo != null)
            {
                _centeredChainVisual.RuntimeInfo.SetCentered(true);
            }

            _lastCenteredBroadcastVisual = _centeredChainVisual;
        }

        // -----------------------------------------------------------------
        // Scrollbar + misc
        // -----------------------------------------------------------------

        private void RefreshLinkedScrollbarState()
        {
            if (linked_scrollbar == null)
            {
                return;
            }

            bool canUseFiniteRange = IsFiniteMode() && _enabledIndices.Count > 0;
            linked_scrollbar.interactable = canUseFiniteRange;

            if (!canUseFiniteRange)
            {
                _suppressScrollbarCallback = true;
                linked_scrollbar.size = 1f;
                linked_scrollbar.SetValueWithoutNotify(invert_scrollbar_value ? 1f : 0f);
                _suppressScrollbarCallback = false;
                return;
            }

            float normalized = GetNormalizedScrollbarValueFromOffset();
            float visibleHalfExtent = _frameVisibleHalfExtentValid
                ? _frameVisibleHalfExtent
                : GetVisibleHalfExtent();
            float visibleSpan = visibleHalfExtent * 2f;
            GetFiniteOffsetBounds(out float minOffset, out float maxOffset);
            float range = Mathf.Max(0f, maxOffset - minOffset);
            float fullSpan = visibleSpan + range;
            float size = fullSpan > 0.0001f ? Mathf.Clamp01(visibleSpan / fullSpan) : 1f;

            _suppressScrollbarCallback = true;
            linked_scrollbar.size = size;
            if (!_scrollbarPointerHeld)
            {
                linked_scrollbar.SetValueWithoutNotify(normalized);
            }

            _suppressScrollbarCallback = false;
        }

        private bool IsSharedVisualRecycleMode()
        {
            return item_source_mode == ScrollerItemSourceMode.SinglePrefabWithCount;
        }

        private int GetVisualPoolKey(int logicalIndex)
        {
            return IsSharedVisualRecycleMode() ? SharedSinglePrefabPoolKey : logicalIndex;
        }

        private void RebindLinkedScrollbarCallback()
        {
            if (_registeredScrollbar == linked_scrollbar)
            {
                return;
            }

            UnregisterLinkedScrollbarCallback();
            RegisterLinkedScrollbarCallback();
        }

        private void RegisterLinkedScrollbarCallback()
        {
            if (linked_scrollbar == null)
            {
                return;
            }

            linked_scrollbar.onValueChanged.RemoveListener(OnScrollbarValueChanged);
            linked_scrollbar.onValueChanged.AddListener(OnScrollbarValueChanged);
            _registeredScrollbar = linked_scrollbar;
            EnsureScrollbarDragRelay();
        }

        private void UnregisterLinkedScrollbarCallback()
        {
            if (_registeredScrollbar == null)
            {
                return;
            }

            _registeredScrollbar.onValueChanged.RemoveListener(OnScrollbarValueChanged);
            _registeredScrollbar = null;
            ClearScrollbarDriveState();
        }

        private void EnsureScrollbarDragRelay()
        {
            if (linked_scrollbar == null)
            {
                return;
            }

            if (_scrollbarDragRelay == null)
            {
                _scrollbarDragRelay = linked_scrollbar.GetComponent<LinkedScrollbarDragRelay>();
                if (_scrollbarDragRelay == null)
                {
                    _scrollbarDragRelay = linked_scrollbar.gameObject.AddComponent<LinkedScrollbarDragRelay>();
                }
            }

            _scrollbarDragRelay.Bind(this);
        }

        private bool IsFiniteMode()
        {
            return list_mode == ScrollerListMode.Finite;
        }

        private bool IsUserActivelyScrolling()
        {
            return _isDragging || _scrollbarPointerHeld || Mathf.Abs(_scrollVelocity) > 0.001f;
        }

        private void ApplyLinkedScrollbarNormalizedValue(float effectiveValue)
        {
            if (!IsFiniteMode() || _enabledIndices.Count == 0)
            {
                return;
            }

            EndRelayout();
            _scrollbarTargetOffset = GetScrollOffsetFromNormalizedScrollbar(effectiveValue);
            _scrollbarOffsetLeadsChain = true;
            _scrollVelocity = 0f;
            InterruptPassiveSnapForScrollDrive();
            TryJumpScrollbarOffsetToTarget();
        }

        /// <summary>
        /// Instantly commits the scrollbar target when it is far from the current offset (e.g. track click).
        /// </summary>
        /// <returns>True if an instant jump was applied.</returns>
        private bool TryJumpScrollbarOffsetToTarget()
        {
            if (scrollbar_jump_distance_threshold <= 0f)
            {
                return false;
            }

            float delta = Mathf.Abs(_scrollbarTargetOffset - _scrollOffset);
            if (delta < scrollbar_jump_distance_threshold)
            {
                return false;
            }

            _scrollOffset = _scrollbarTargetOffset;
            _scrollbarScrollVelocity = 0f;
            ApplyScrollOffsetToChainImmediately();
            return true;
        }

        internal void NotifyLinkedScrollbarPointerDown()
        {
            _scrollbarPointerHeld = true;
            _scrollbarOffsetLeadsChain = true;
            _scrollbarTargetOffset = _scrollOffset;
            _scrollbarScrollVelocity = 0f;
            _scrollVelocity = 0f;
            InterruptPassiveSnapForScrollDrive();
        }

        internal void NotifyLinkedScrollbarPointerUp()
        {
            _scrollbarPointerHeld = false;
        }

        private int ClampOrderForMode(int order)
        {
            if (!IsFiniteMode())
            {
                return order;
            }

            if (_enabledIndices.Count <= 0)
            {
                return 0;
            }

            return Mathf.Clamp(order, 0, _enabledIndices.Count - 1);
        }

        private float ClampOffsetForMode(float offset)
        {
            if (!IsFiniteMode() || _enabledIndices.Count == 0)
            {
                return offset;
            }

            GetFiniteOffsetBounds(out float minOffset, out float maxOffset);
            return Mathf.Clamp(offset, minOffset, maxOffset);
        }

        private void GetFiniteOffsetBounds(out float minOffset, out float maxOffset)
        {
            int count = _enabledIndices.Count;
            if (count <= 0)
            {
                minOffset = 0f;
                maxOffset = 0f;
                return;
            }

            float visibleHalfExtent = _frameVisibleHalfExtentValid
                ? _frameVisibleHalfExtent
                : GetVisibleHalfExtent();
            float firstCenter = GetOrderCenterPosition(0);
            float lastCenter = GetOrderCenterPosition(count - 1);
            float firstHalfSize = 0.5f * _items[_enabledIndices[0]].Height;
            float lastHalfSize = 0.5f * _items[_enabledIndices[count - 1]].Height;

            minOffset = firstCenter - (visibleHalfExtent - firstHalfSize);
            maxOffset = lastCenter + (visibleHalfExtent - lastHalfSize);

            if (minOffset > maxOffset)
            {
                float collapsed = 0.5f * (minOffset + maxOffset);
                minOffset = collapsed;
                maxOffset = collapsed;
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

        private sealed class LinkedScrollbarDragRelay : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
        {
            private ScrollerManager _owner;

            public void Bind(ScrollerManager owner)
            {
                _owner = owner;
            }

            public void OnPointerDown(PointerEventData eventData)
            {
                _owner?.NotifyLinkedScrollbarPointerDown();
            }

            public void OnPointerUp(PointerEventData eventData)
            {
                _owner?.NotifyLinkedScrollbarPointerUp();
            }
        }
    }
}
