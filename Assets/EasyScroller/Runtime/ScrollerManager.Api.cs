using System.Collections.Generic;
using UnityEngine;

namespace EasyScroller
{
    public partial class ScrollerManager
    {
        /// <summary>
        /// Gets the currently configured primary scroll axis.
        /// </summary>
        public ScrollerAxis ScrollAxis => scroll_axis;

        /// <summary>
        /// Replaces the prefab list source and rebuilds runtime visuals/state.
        /// </summary>
        /// <param name="prefabs">Prefabs to use in logical order; null entries are ignored.</param>
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

            DestroyAllVisualsAndClearPools();
            BuildItemState();
            _pendingInitialReveal = hide_items_until_initial_settle;
            ApplyStructureChangeAndRefreshVisuals(false);
        }

        /// <summary>
        /// Applies a normalized scrollbar value (0..1) to finite-mode scroll offset.
        /// Wire this to <c>Scrollbar.onValueChanged</c>.
        /// </summary>
        /// <param name="normalizedValue">Incoming normalized scrollbar value.</param>
        public void OnScrollbarValueChanged(float normalizedValue)
        {
            if (_suppressScrollbarCallback)
            {
                return;
            }

            if (_enabledIndices.Count == 0)
            {
                return;
            }

            ApplyLinkedScrollbarNormalizedValue(EffectiveScrollbarValue(normalizedValue));
        }

        /// <summary>
        /// Enables or disables an item by runtime/logical index.
        /// </summary>
        /// <param name="itemIndex">Runtime/logical item index in the current item list.</param>
        /// <param name="enabled">True to enable, false to disable.</param>
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
            ApplyStructureChangeAndRefreshVisuals();
        }

        /// <summary>
        /// Gets the logical index of the item currently nearest the center.
        /// </summary>
        /// <returns>Centered logical index, or -1 when no item is centered.</returns>
        public int GetCenteredLogicalIndex()
        {
            return _centeredLogicalIndex;
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="AddItemAtRuntime()"/>.
        /// </summary>
        public void AddItemNoRet()
        {
            AddItemAtRuntime();
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="AddItemAtRuntime(GameObject)"/>.
        /// </summary>
        /// <param name="prefab">Prefab to append in prefab-list mode.</param>
        public void AddItemWithPrefabNoRet(GameObject prefab)
        {
            AddItemAtRuntime(prefab);
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="InsertItemAtRuntime(int)"/>.
        /// </summary>
        /// <param name="itemIndex">Target insert index.</param>
        public void InsertItemNoRet(int itemIndex)
        {
            InsertItemAtRuntime(itemIndex);
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="InsertItemAtRuntime(int, GameObject)"/>.
        /// </summary>
        /// <param name="itemIndex">Target insert index.</param>
        /// <param name="prefab">Prefab to insert in prefab-list mode.</param>
        public void InsertItemWithPrefabNoRet(int itemIndex, GameObject prefab)
        {
            InsertItemAtRuntime(itemIndex, prefab);
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="RemoveItemPermanentlyAtRuntime(int)"/>.
        /// </summary>
        /// <param name="itemIndex">Runtime/logical index to remove.</param>
        public void RemoveItemPermanentlyNoRet(int itemIndex)
        {
            RemoveItemPermanentlyAtRuntime(itemIndex);
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="ReorderItemAtRuntime(int, int)"/>.
        /// </summary>
        /// <param name="fromIndex">Current runtime/logical index.</param>
        /// <param name="toIndex">Destination runtime/logical index.</param>
        public void ReorderItemNoRet(int fromIndex, int toIndex)
        {
            ReorderItemAtRuntime(fromIndex, toIndex);
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="ScrollToLogicalIndex(int, bool)"/> (animated).
        /// </summary>
        /// <param name="logicalIndex">Target logical index.</param>
        public void ScrollToLogicalIndexNoRet(int logicalIndex)
        {
            ScrollToLogicalIndex(logicalIndex);
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="ScrollToLogicalIndex(int, bool)"/> (instant).
        /// </summary>
        /// <param name="logicalIndex">Target logical index.</param>
        public void JumpToLogicalIndexNoRet(int logicalIndex)
        {
            ScrollToLogicalIndex(logicalIndex, false);
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="ScrollToDataIndex(int, bool)"/> (animated).
        /// </summary>
        /// <param name="dataIndex">Stable data index to locate.</param>
        public void ScrollToDataIndexNoRet(int dataIndex)
        {
            ScrollToDataIndex(dataIndex);
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="ScrollToDataIndex(int, bool)"/> (instant).
        /// </summary>
        /// <param name="dataIndex">Stable data index to locate.</param>
        public void JumpToDataIndexNoRet(int dataIndex)
        {
            ScrollToDataIndex(dataIndex, false);
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="ScrollToRuntimeInfo(ScrollerItemRuntimeInfo, bool)"/> (animated).
        /// </summary>
        /// <param name="runtimeInfo">Runtime item info to resolve target index from.</param>
        public void ScrollToRuntimeInfoNoRet(ScrollerItemRuntimeInfo runtimeInfo)
        {
            ScrollToRuntimeInfo(runtimeInfo);
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="ScrollToRuntimeInfo(ScrollerItemRuntimeInfo, bool)"/> (instant).
        /// </summary>
        /// <param name="runtimeInfo">Runtime item info to resolve target index from.</param>
        public void JumpToRuntimeInfoNoRet(ScrollerItemRuntimeInfo runtimeInfo)
        {
            ScrollToRuntimeInfo(runtimeInfo, false);
        }

        /// <summary>
        /// Adds an item at runtime in prefab-list mode, or delegates to single-prefab mode add.
        /// </summary>
        /// <param name="prefab">Prefab to append when in prefab-list mode.</param>
        /// <returns>True if the add succeeds; otherwise false.</returns>
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
            _pendingSpliceLogicalIndex = _items.Count - 1;
            ApplyStructureChangeAndRefreshVisuals();
            return true;
        }

        /// <summary>
        /// Adds an item at runtime in single-prefab mode.
        /// </summary>
        /// <returns>True if the add succeeds; otherwise false.</returns>
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

            int nextDataIndex = GetNextDataIndex();
            _items.Add(new ItemState(single_prefab, ResolvePrefabPrimarySize(single_prefab), -1, nextDataIndex));
            _pendingSpliceLogicalIndex = _items.Count - 1;
            ApplyStructureChangeAndRefreshVisuals();
            return true;
        }

        /// <summary>
        /// Soft-removes (disables) an item at runtime by logical index.
        /// </summary>
        /// <param name="itemIndex">Runtime/logical item index to disable.</param>
        /// <returns>True if the item was disabled; otherwise false.</returns>
        public bool RemoveItemAtRuntime(int itemIndex, ScrollerItemRuntimeInfo requester = null)
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
            PurgeVisualsForLogicalIndex(itemIndex, requester);
            ApplyStructureChangeAndRefreshVisuals();
            return true;
        }

        /// <summary>
        /// Soft-removes (disables) an item by prefab-list index (prefab-list mode only).
        /// </summary>
        /// <param name="prefabListIndex">Index in the configured prefab list.</param>
        /// <returns>True if the item was disabled; otherwise false.</returns>
        public bool RemoveItemByPrefabListIndex(int prefabListIndex)
        {
            if (item_source_mode != ScrollerItemSourceMode.PrefabList)
            {
                Debug.LogWarning("RemoveItemByPrefabListIndex(...) is only valid in PrefabList mode.");
                return false;
            }

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

        /// <summary>
        /// Soft-removes (disables) an item using its runtime info binding.
        /// </summary>
        /// <param name="runtimeInfo">Runtime info instance belonging to the item.</param>
        /// <returns>True if the item was disabled; otherwise false.</returns>
        public bool RemoveItemByRuntimeInfo(ScrollerItemRuntimeInfo runtimeInfo)
        {
            if (runtimeInfo == null)
            {
                return false;
            }

            return RemoveItemAtRuntime(runtimeInfo.LogicalIndex, runtimeInfo);
        }

        /// <summary>
        /// Inserts an item at the requested index in prefab-list mode.
        /// Delegates to single-prefab insert when that source mode is active.
        /// </summary>
        /// <param name="itemIndex">Target insert position among enabled items (0 = first; pass enabled count to append).</param>
        /// <param name="prefab">Prefab to insert in prefab-list mode.</param>
        /// <returns>True if the insert succeeds; otherwise false.</returns>
        public bool InsertItemAtRuntime(int itemIndex, GameObject prefab)
        {
            if (item_source_mode == ScrollerItemSourceMode.SinglePrefabWithCount)
            {
                return InsertItemAtRuntime(itemIndex);
            }

            if (prefab == null)
            {
                Debug.LogWarning("InsertItemAtRuntime(itemIndex, prefab) requires a non-null prefab in PrefabList mode.");
                return false;
            }

            RefreshEnabledIndices();
            int enabledSlot = Mathf.Clamp(itemIndex, 0, _enabledIndices.Count);
            int clampedIndex = ResolveLogicalIndexForEnabledInsertSlot(enabledSlot);
            int nextDataIndex = GetNextDataIndex();
            if (!smooth_relayout_on_structure_change)
            {
                DestroyAllVisualsAndClearPools();
            }

            ItemState newItem = new ItemState(prefab, ResolvePrefabPrimarySize(prefab), clampedIndex, nextDataIndex);
            _items.Insert(clampedIndex, newItem);
            prefab_list.Insert(clampedIndex, prefab);
            if (smooth_relayout_on_structure_change)
            {
                ShiftChainLogicalIndicesFrom(clampedIndex, 1);
            }
            ReindexSourcePrefabIndices();
            _pendingSpliceLogicalIndex = clampedIndex;
            ApplyStructureChangeAndRefreshVisuals();
            return true;
        }

        /// <summary>
        /// Inserts a new single-prefab item at the requested index.
        /// </summary>
        /// <param name="itemIndex">Target insert position among enabled items (0 = first; pass enabled count to append).</param>
        /// <returns>True if the insert succeeds; otherwise false.</returns>
        public bool InsertItemAtRuntime(int itemIndex)
        {
            if (item_source_mode == ScrollerItemSourceMode.PrefabList)
            {
                Debug.LogWarning("InsertItemAtRuntime(itemIndex) requires a prefab argument in PrefabList mode.");
                return false;
            }

            if (single_prefab == null)
            {
                Debug.LogWarning("single_prefab is null; cannot insert item in SinglePrefabWithCount mode.");
                return false;
            }

            RefreshEnabledIndices();
            int enabledSlot = Mathf.Clamp(itemIndex, 0, _enabledIndices.Count);
            int clampedIndex = ResolveLogicalIndexForEnabledInsertSlot(enabledSlot);
            int nextDataIndex = GetNextDataIndex();
            if (!smooth_relayout_on_structure_change)
            {
                DestroyAllVisualsAndClearPools();
            }

            ItemState newItem = new ItemState(single_prefab, ResolvePrefabPrimarySize(single_prefab), -1, nextDataIndex);
            _items.Insert(clampedIndex, newItem);
            if (smooth_relayout_on_structure_change)
            {
                ShiftChainLogicalIndicesFrom(clampedIndex, 1);
            }
            _pendingSpliceLogicalIndex = clampedIndex;
            ApplyStructureChangeAndRefreshVisuals();
            return true;
        }

        /// <summary>
        /// Hard-removes an item from runtime data by logical index.
        /// </summary>
        /// <param name="itemIndex">Runtime/logical item index to remove.</param>
        /// <returns>True if the item was removed; otherwise false.</returns>
        public bool RemoveItemPermanentlyAtRuntime(int itemIndex)
        {
            if (itemIndex < 0 || itemIndex >= _items.Count)
            {
                return false;
            }

            ItemState removedItem = _items[itemIndex];
            _items.RemoveAt(itemIndex);

            if (item_source_mode == ScrollerItemSourceMode.PrefabList)
            {
                int sourceIndex = removedItem.SourcePrefabIndex;
                if (sourceIndex >= 0 && sourceIndex < prefab_list.Count)
                {
                    prefab_list.RemoveAt(sourceIndex);
                }
                ReindexSourcePrefabIndices();
            }

            DestroyAllVisualsAndClearPools();
            ApplyStructureChangeAndRefreshVisuals();
            return true;
        }

        /// <summary>
        /// Reorders an item from one logical index to another.
        /// </summary>
        /// <param name="fromIndex">Current runtime/logical index.</param>
        /// <param name="toIndex">Destination runtime/logical index.</param>
        /// <returns>True if reorder succeeds; otherwise false.</returns>
        public bool ReorderItemAtRuntime(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= _items.Count || toIndex < 0 || toIndex >= _items.Count)
            {
                return false;
            }

            if (fromIndex == toIndex)
            {
                return true;
            }

            ItemState movedItem = _items[fromIndex];
            _items.RemoveAt(fromIndex);
            _items.Insert(toIndex, movedItem);

            if (item_source_mode == ScrollerItemSourceMode.PrefabList)
            {
                GameObject movedPrefab = prefab_list[fromIndex];
                prefab_list.RemoveAt(fromIndex);
                prefab_list.Insert(toIndex, movedPrefab);
                ReindexSourcePrefabIndices();
            }

            DestroyAllVisualsAndClearPools();
            ApplyStructureChangeAndRefreshVisuals();
            return true;
        }

        /// <summary>
        /// Scrolls or jumps to a specific logical item index.
        /// </summary>
        /// <param name="logicalIndex">Target logical index.</param>
        /// <param name="animated">True to animate via snap; false to jump instantly.</param>
        /// <returns>True if the target is valid and scrolling was applied; otherwise false.</returns>
        public bool ScrollToLogicalIndex(int logicalIndex, bool animated = true)
        {
            if (logicalIndex < 0 || logicalIndex >= _items.Count || !_items[logicalIndex].Enabled)
            {
                return false;
            }

            int targetOrder = GetTargetOrderForLogicalIndex(logicalIndex);
            float targetOffset = ClampOffsetForMode(GetOrderCenterPosition(targetOrder));
            return ScrollToOffsetAndOrder(targetOffset, targetOrder, animated);
        }

        /// <summary>
        /// Scrolls or jumps to the first enabled item matching a stable data index.
        /// </summary>
        /// <param name="dataIndex">Data index to locate.</param>
        /// <param name="animated">True to animate via snap; false to jump instantly.</param>
        /// <returns>True if a matching enabled item exists; otherwise false.</returns>
        public bool ScrollToDataIndex(int dataIndex, bool animated = true)
        {
            int logicalIndex = FindLogicalIndexByDataIndex(dataIndex);
            if (logicalIndex < 0)
            {
                return false;
            }

            return ScrollToLogicalIndex(logicalIndex, animated);
        }

        /// <summary>
        /// Scrolls or jumps to an item resolved from runtime info.
        /// </summary>
        /// <param name="runtimeInfo">Runtime info instance containing target logical index.</param>
        /// <param name="animated">True to animate via snap; false to jump instantly.</param>
        /// <returns>True if the target is valid and scrolling was applied; otherwise false.</returns>
        public bool ScrollToRuntimeInfo(ScrollerItemRuntimeInfo runtimeInfo, bool animated = true)
        {
            if (runtimeInfo == null)
            {
                return false;
            }

            return ScrollToLogicalIndex(runtimeInfo.LogicalIndex, animated);
        }

        /// <summary>
        /// Centers the next item along the configured positive primary axis (chain successor).
        /// </summary>
        /// <param name="steps">How many items to advance.</param>
        /// <returns>True if centering was started; otherwise false.</returns>
        public bool CenterNextItem(int steps = 1)
        {
            return CenterAdjacentChainItem(1, steps);
        }

        /// <summary>
        /// Centers the previous item along the configured negative primary axis (chain predecessor).
        /// </summary>
        /// <param name="steps">How many items to move back.</param>
        /// <returns>True if centering was started; otherwise false.</returns>
        public bool CenterPreviousItem(int steps = 1)
        {
            return CenterAdjacentChainItem(-1, steps);
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="CenterNextItem(int)"/>.
        /// </summary>
        /// <param name="steps">How many items to advance.</param>
        public void CenterNextItemNoRet(int steps = 1)
        {
            CenterNextItem(steps);
        }

        /// <summary>
        /// UnityEvent-friendly wrapper for <see cref="CenterPreviousItem(int)"/>.
        /// </summary>
        /// <param name="steps">How many items to move back.</param>
        public void CenterPreviousItemNoRet(int steps = 1)
        {
            CenterPreviousItem(steps);
        }

        /// <summary>
        /// Notifies the scroller that pointer interaction began.
        /// Resets inertial velocity.
        /// </summary>
        public void NotifyPointerDown()
        {
            _scrollVelocity = 0f;
        }

        /// <summary>
        /// Marks the start of a user drag interaction.
        /// </summary>
        public void BeginUserDrag()
        {
            EndRelayout();
            _isDragging = true;
            _scrollVelocity = 0f;
            InterruptPassiveSnapForScrollDrive();
        }

        /// <summary>
        /// Applies user drag delta in axis units and updates velocity estimate.
        /// </summary>
        /// <param name="deltaUnits">Delta movement in scroller units.</param>
        /// <param name="deltaTime">Time elapsed for velocity estimation.</param>
        public void ApplyUserDragDelta(float deltaUnits, float deltaTime)
        {
            float previousOffset = _scrollOffset;
            _scrollOffset += deltaUnits;
            _scrollOffset = ClampOffsetForMode(_scrollOffset);
            float realizedDelta = _scrollOffset - previousOffset;
            _scrollVelocity = deltaTime > 0f ? (realizedDelta / deltaTime) : 0f;
        }

        /// <summary>
        /// Marks the end of a user drag interaction.
        /// </summary>
        public void EndUserDrag()
        {
            _isDragging = false;
        }
    }
}
