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
        private int _settledOrder;
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
        private float _lastScrollOffsetForChainMotion;
        private bool _skipCollectiveScrollThisFrame;

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
                Debug.LogWarning("ScrollerManager has no prefabs configured. Call SetPrefabs(...) during debug mode.");
            }
            RefreshEnabledIndices();
            _relayoutActive = false;
            _pendingInitialReveal = hide_items_until_initial_settle;
            _lastScrollOffsetForChainMotion = _scrollOffset;
            SyncVisibleWindow();
            RefreshLinkedScrollbarState();
        }

        void Update()
        {
            if (_enabledIndices.Count == 0)
            {
                DeactivateAllActiveVisuals();
                RefreshLinkedScrollbarState();
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
                _scrollVelocity = Mathf.Lerp(_scrollVelocity, 0f, inertia_damping * dt);
            }

            // Spring toward an active snap target (programmatic next/prev or auto-snap).
            if (_hasSnapTarget && !_relayoutActive && !_isDragging && Mathf.Abs(_scrollVelocity) < snap_velocity_threshold)
            {
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
                    _settledOrder = _snapTargetOrder;
                    _settledChainVisual = _snapTargetChainVisual;
                    _hasSnapTarget = false;
                }
            }
            else if (_hasSnapTarget)
            {
                ClearActiveSnapState();
            }

            // Auto-snap after release only when enabled and nothing else is driving a target.
            if (enable_snapping &&
                !_hasSnapTarget &&
                !_relayoutActive &&
                !_isDragging &&
                !_scrollbarOffsetLeadsChain &&
                Mathf.Abs(_scrollVelocity) < snap_velocity_threshold)
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

        private int GetTargetOrderForLogicalIndex(int logicalIndex)
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
            int referenceOrder = GetReferenceOrderForProgrammaticNavigation();
            int cycle = FloorDiv(referenceOrder, count);
            int candidate = (cycle * count) + orderInEnabled;
            return RefineOrderToNearestCycle(candidate, _scrollOffset);
        }

        private int RefineOrderToNearestCycle(int candidate, float referenceOffset)
        {
            int count = _enabledIndices.Count;
            if (IsFiniteMode() || count <= 0)
            {
                return candidate;
            }

            int candidatePlus = candidate + count;
            int candidateMinus = candidate - count;
            float bestDistance = Mathf.Abs(GetOrderCenterPosition(candidate) - referenceOffset);
            int bestOrder = candidate;

            float plusDistance = Mathf.Abs(GetOrderCenterPosition(candidatePlus) - referenceOffset);
            if (plusDistance < bestDistance)
            {
                bestDistance = plusDistance;
                bestOrder = candidatePlus;
            }

            float minusDistance = Mathf.Abs(GetOrderCenterPosition(candidateMinus) - referenceOffset);
            if (minusDistance < bestDistance)
            {
                bestOrder = candidateMinus;
            }

            return bestOrder;
        }

        private int GetReferenceOrderForProgrammaticNavigation()
        {
            bool userDrivenMotion = _isDragging || Mathf.Abs(_scrollVelocity) > 0.001f;
            if (userDrivenMotion)
            {
                _hasProgrammaticStepAnchor = false;
                return GetNearestOrderToOffset(_scrollOffset);
            }

            if (_hasProgrammaticStepAnchor)
            {
                return _programmaticStepOrder;
            }

            if (_hasSnapTarget)
            {
                return _snapTargetOrder;
            }

            if (_hasSettledOrder)
            {
                if (IsVisualActiveInChain(_settledChainVisual))
                {
                    return _settledChainVisual.AbsoluteOrderIndex;
                }

                return _settledOrder;
            }

            return GetNearestOrderToOffset(_scrollOffset);
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
            _settledOrder = targetVisual.AbsoluteOrderIndex;
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
            if (_enabledIndices.Count == 0 || direction == 0 || steps <= 0)
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
                int targetOrder = ClampOrderForMode(current.AbsoluteOrderIndex + (sign * steps));
                float targetOffset = ClampOffsetForMode(GetOrderCenterPosition(targetOrder));
                return ScrollToOffsetAndOrder(targetOffset, targetOrder, true);
            }

            return CenterChainVisual(target, animated: true);
        }

        private bool ScrollToOffsetAndOrder(float targetOffset, int targetOrder, bool animated)
        {
            if (_enabledIndices.Count == 0)
            {
                return false;
            }

            StopUserMotionForProgrammaticScroll();
            _hasProgrammaticStepAnchor = true;
            _programmaticStepOrder = ClampOrderForMode(targetOrder);

            if (ShouldSmoothProgrammaticScroll(animated))
            {
                VisualItem targetVisual = FindChainVisualByOrder(_programmaticStepOrder);
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
                _scrollOffset = ClampOffsetForMode(targetOffset);
                ClearActiveSnapState();
                _hasSettledOrder = true;
                _settledOrder = _programmaticStepOrder;
                _settledChainVisual = FindChainVisualByOrder(_programmaticStepOrder);
                _relayoutActive = false;
                ApplyScrollOffsetToChainImmediately();
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

            // Finite bounds: ease only during passive relayout. User scroll/drag
            // always gets immediate clamping so input is never damped away.
            float clampedOffset = ClampOffsetForMode(_scrollOffset);
            if (_relayoutActive && IsFiniteMode() && !IsUserActivelyScrolling())
            {
                float lerpT = 1f - Mathf.Exp(-Mathf.Max(0.01f, relayout_lerp_speed) * Time.deltaTime);
                _scrollOffset = Mathf.Lerp(_scrollOffset, clampedOffset, lerpT);
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

            // 2) Reconcile: drop chain visuals that no longer reference enabled
            //    logicals, drop everything past a non-sequential break, then
            //    refresh AbsoluteOrderIndex values relative to the chain head.
            ReconcileChainLogicalsAndOrders();

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
            bool offsetLeadsChain = IsUserActivelyScrolling() ||
                                    _hasSnapTarget ||
                                    _hasProgrammaticStepAnchor ||
                                    _scrollbarOffsetLeadsChain;
            if (!offsetLeadsChain)
            {
                SyncScrollOffsetFromChainCenter();
                _lastScrollOffsetForChainMotion = _scrollOffset;
            }

            // 5) Grow / trim coverage at chain edges.
            float halfExtent = GetVisibleHalfExtent();
            GrowChainCoverage(halfExtent);
            TrimChainCoverage(halfExtent);

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

            float dt = Time.deltaTime;
            float springT = 1f - Mathf.Exp(-Mathf.Max(0.01f, chain_spring_strength) * dt);
            float relayoutT = 1f - Mathf.Exp(-Mathf.Max(0.01f, relayout_lerp_speed) * dt);
            bool passiveRelayout = _relayoutActive && !IsUserActivelyScrolling();
            float moveT = passiveRelayout ? relayoutT : springT;

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
            if (!_relayoutActive || IsUserActivelyScrolling() || !IsVisualActiveInChain(_relayoutBiasChainVisual))
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

        private void ReconcileChainLogicalsAndOrders()
        {
            // 1) Drop chain visuals whose logical was disabled or removed.
            VisualItem v = _chainHead;
            while (v != null)
            {
                VisualItem next = v.Next;
                int slot = GetEnabledSlot(v.LogicalIndex);
                if (slot < 0)
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

            // 2) Find the first non-sequential break (in either direction).
            //    Drop everything past it. This handles inserts / reorders by
            //    forcing the affected side of the chain to rebuild from grow
            //    coverage on subsequent passes.
            int count = _enabledIndices.Count;
            v = _chainHead;
            while (v != null && v.Next != null)
            {
                int slotV = GetEnabledSlot(v.LogicalIndex);
                int slotNext = GetEnabledSlot(v.Next.LogicalIndex);
                bool sequential = slotV >= 0 && slotNext >= 0 && IsLogicallySequentialForward(slotV, slotNext, count);
                if (!sequential)
                {
                    ReleaseChainFrom(v.Next, forward: true);
                    break;
                }
                v = v.Next;
            }

            if (_chainHead == null)
            {
                return;
            }

            // 3) Reassign AbsoluteOrderIndex values starting from chain head.
            //    The head's order is chosen so its lattice position is close to
            //    _scrollOffset, keeping the scroll metric meaningful for snap
            //    targets and scrollbar normalization.
            int headOrder = ComputeNearestOrderForLogical(_chainHead.LogicalIndex, _scrollOffset);
            _chainHead.AbsoluteOrderIndex = headOrder;
            VisualItem cursor = _chainHead.Next;
            int orderValue = headOrder;
            while (cursor != null)
            {
                orderValue++;
                cursor.AbsoluteOrderIndex = orderValue;
                cursor = cursor.Next;
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
                _snapTargetChainVisual = targetVisual;
                _snapTargetOrder = targetVisual.AbsoluteOrderIndex;
                _snapTargetOffset = ComputeScrollOffsetToCenterVisual(targetVisual);
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

        private void GrowChainCoverage(float halfExtent)
        {
            if (_chainHead == null || _enabledIndices.Count == 0)
            {
                return;
            }

            GrowChainDirection(forward: true, halfExtent);
            GrowChainDirection(forward: false, halfExtent);
        }

        private void GrowChainDirection(bool forward, float halfExtent)
        {
            int bufferRemaining = Mathf.Max(0, buffer_item_count);
            int safety = _enabledIndices.Count * 4 + (buffer_item_count * 4) + 16;
            while (safety-- > 0)
            {
                VisualItem edge = forward ? _chainTail : _chainHead;
                if (edge == null)
                {
                    return;
                }

                int nextOrder = edge.AbsoluteOrderIndex + (forward ? 1 : -1);
                if (IsFiniteMode() && (nextOrder < 0 || nextOrder >= _enabledIndices.Count))
                {
                    return;
                }

                int nextLogical = ResolveLogicalIndexFromOrder(nextOrder);
                if (nextLogical < 0)
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
            if (_chainHead == null)
            {
                return;
            }

            int bufferAllowance = Mathf.Max(0, buffer_item_count);
            float strictMin = -halfExtent;

            int headOutside = CountChainOutsideFromHead(strictMin);
            while (_chainHead != null && _chainHead != _chainTail && headOutside > bufferAllowance)
            {
                VisualItem head = _chainHead;
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
            float dt = Time.deltaTime;

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
                    v.RectTransform.localScale = Vector3.Lerp(v.RectTransform.localScale, desiredScale, scale_lerp_speed * dt);

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
            visual.Prev = null;
            visual.Next = null;
            _chainHead = visual;
            _chainTail = visual;
            visual.IsInChain = true;
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

            return CreateNewVisualWrapper(logicalIndex);
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
            runtimeInfo.Initialize(logicalIndex, _items[logicalIndex].DataIndex, wrapperRect, contentRect);
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
                visual.RuntimeInfo.NotifyContentRefreshRequested();
            }

            return bindingChanged;
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
            VisualItem stabilityAnchor = null;
            float stabilityAxis = 0f;
            VisualItem relayoutBiasSuccessor = null;
            if (_chainHead != null)
            {
                if (IsVisualActiveInChain(_centeredChainVisual))
                {
                    stabilityAnchor = _centeredChainVisual;
                }
                else
                {
                    stabilityAnchor = FindChainVisualNearestToAxis(0f, _centeredChainVisual);
                }
                if (stabilityAnchor != null)
                {
                    stabilityAxis = GetVisualAxis(stabilityAnchor);
                    relayoutBiasSuccessor = ChooseRelayoutBiasSuccessor(stabilityAnchor);
                }
            }

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
                _scrollOffset = 0f;
                _scrollVelocity = 0f;
                ClearActiveSnapState();
                _hasSettledOrder = false;
                _settledChainVisual = null;
                _hasProgrammaticStepAnchor = false;
                _lastCenteredBroadcastVisual = null;
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

            // Re-anchor _scrollOffset so the stability visual's logical lands on
            // its original screen axis after the new spacing is applied. If the
            // original stability visual was disabled itself, pick a replacement
            // that's closest to where the old anchor sat. Order reassignment
            // for the rest of the chain happens later via ReconcileChain.
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

            NormalizeMotionStateAfterStructureChange();

            SyncVisibleWindow();
            RefreshLinkedScrollbarState();
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

        private void SyncSinglePrefabCountFromEnabledItems()
        {
            if (item_source_mode != ScrollerItemSourceMode.SinglePrefabWithCount)
            {
                return;
            }

            int enabledCount = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Enabled)
                {
                    enabledCount++;
                }
            }

            single_prefab_count = enabledCount;
        }

        private void NormalizeMotionStateAfterStructureChange()
        {
            if (_enabledIndices.Count == 0)
            {
                _scrollOffset = 0f;
                _scrollVelocity = 0f;
                _snapVelocity = 0f;
                _hasSnapTarget = false;
                _hasSettledOrder = false;
                _hasProgrammaticStepAnchor = false;
                _snapTargetLockedUntilUserInput = false;
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

            if (_hasSettledOrder)
            {
                _settledOrder = ClampOrderForMode(_settledOrder);
                int settledLogical = ResolveLogicalIndexFromOrder(_settledOrder);
                if (settledLogical < 0 || settledLogical >= _items.Count || !_items[settledLogical].Enabled)
                {
                    _hasSettledOrder = false;
                    _settledChainVisual = null;
                }
            }

            if (_hasSettledOrder && !IsVisualActiveInChain(_settledChainVisual))
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

            GetFiniteOffsetBounds(out float minOffset, out float maxOffset);
            float range = Mathf.Max(0f, maxOffset - minOffset);
            float normalized = range > 0.0001f ? Mathf.Clamp01((_scrollOffset - minOffset) / range) : 0f;
            if (invert_scrollbar_value)
            {
                normalized = 1f - normalized;
            }

            float visibleSpan = GetVisibleHalfExtent() * 2f;
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
            _scrollbarPointerHeld = false;
            _scrollbarOffsetLeadsChain = false;
            _scrollbarScrollVelocity = 0f;
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
            GetFiniteOffsetBounds(out float minOffset, out float maxOffset);
            _scrollbarTargetOffset = ClampOffsetForMode(Mathf.Lerp(minOffset, maxOffset, effectiveValue));
            _scrollbarOffsetLeadsChain = true;
            _scrollVelocity = 0f;
            ClearActiveSnapState();
            _hasProgrammaticStepAnchor = false;
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
            ClearActiveSnapState();
            _hasProgrammaticStepAnchor = false;
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

            float visibleHalfExtent = GetVisibleHalfExtent();
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
