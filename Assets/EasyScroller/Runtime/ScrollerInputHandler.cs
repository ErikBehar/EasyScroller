using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

public class ScrollerInputHandler : MonoBehaviour, IInitializePotentialDragHandler, IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    [SerializeField, Tooltip("ScrollerManager instance to control.")]
    private ScrollerManager scroller_manager;
    [SerializeField, Tooltip("Optional drag area override. Defaults to this RectTransform.")]
    private RectTransform drag_area_override;
    [SerializeField, Tooltip("Allow drag input when pointer is over raycastable item UI.")]
    private bool enable_item_drag = true;
    [SerializeField, Tooltip("Invert mouse/touch drag direction.")]
    private bool invert_drag_direction = false;
    [SerializeField, Tooltip("Multiplier from pointer pixel delta to anchored-position movement.")]
    private float drag_pixels_to_units = 1f;
    [SerializeField, Tooltip("Enable fallback mouse drag in the configured drag area when no item drag is active.")]
    private bool enable_mouse_area_drag = true;
    [SerializeField, Tooltip("Use unscaled time when running programmatic spin.")]
    private bool spin_use_unscaled_time = true;

    private RectTransform _dragAreaRect;
    private Canvas _canvas;
    private bool _eventDragActive;
    private bool _mouseAreaDragActive;
    private bool _mouseAreaDragHasBegun;
    private Vector2 _lastMousePosition;
    private Coroutine _spinRoutine;

    void Awake()
    {
        if (scroller_manager == null)
        {
            scroller_manager = GetComponent<ScrollerManager>();
        }

        _dragAreaRect = drag_area_override != null ? drag_area_override : transform as RectTransform;
        _canvas = (_dragAreaRect != null ? _dragAreaRect : transform).GetComponentInParent<Canvas>();
    }

    void Update()
    {
        HandleMouseAreaDrag();
    }

    public void StartSpin(int direction, float speedUnitsPerSecond, float durationSeconds)
    {
        if (scroller_manager == null || direction == 0 || speedUnitsPerSecond <= 0f || durationSeconds <= 0f)
        {
            return;
        }

        StopSpin();
        _spinRoutine = StartCoroutine(SpinRoutine(direction > 0 ? 1f : -1f, speedUnitsPerSecond, durationSeconds));
    }

    public void StartSpinPositive(float speedUnitsPerSecond, float durationSeconds)
    {
        StartSpin(1, speedUnitsPerSecond, durationSeconds);
    }

    public void StartSpinNegative(float speedUnitsPerSecond, float durationSeconds)
    {
        StartSpin(-1, speedUnitsPerSecond, durationSeconds);
    }

    public void StartSpinUp(float speedUnitsPerSecond, float durationSeconds)
    {
        StartSpinPositive(speedUnitsPerSecond, durationSeconds);
    }

    public void StartSpinDown(float speedUnitsPerSecond, float durationSeconds)
    {
        StartSpinNegative(speedUnitsPerSecond, durationSeconds);
    }

    public void StopSpin()
    {
        if (_spinRoutine != null)
        {
            StopCoroutine(_spinRoutine);
            _spinRoutine = null;
            if (scroller_manager != null)
            {
                scroller_manager.EndUserDrag();
            }
        }
    }

    public void OnInitializePotentialDrag(PointerEventData eventData)
    {
        if (!enable_item_drag)
        {
            return;
        }

        if (eventData != null)
        {
            eventData.useDragThreshold = false;
        }
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (scroller_manager != null)
        {
            scroller_manager.NotifyPointerDown();
        }
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (!enable_item_drag)
        {
            return;
        }

        _eventDragActive = true;
        if (scroller_manager != null)
        {
            scroller_manager.BeginUserDrag();
        }
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!enable_item_drag)
        {
            return;
        }

        if (eventData == null || scroller_manager == null)
        {
            return;
        }

        float canvasScale = (_canvas != null && _canvas.scaleFactor > 0f) ? _canvas.scaleFactor : 1f;
        float direction = invert_drag_direction ? -1f : 1f;
        float pointerPrimaryDelta = ScrollerAxisAdapter.GetPrimary(eventData.delta, GetActiveAxis());
        float deltaUnits = (pointerPrimaryDelta / canvasScale) * drag_pixels_to_units * direction;
        float dt = Time.unscaledDeltaTime;
        scroller_manager.ApplyUserDragDelta(deltaUnits, dt);
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (!enable_item_drag)
        {
            return;
        }

        _eventDragActive = false;
        if (scroller_manager != null)
        {
            scroller_manager.EndUserDrag();
        }
    }

    private void HandleMouseAreaDrag()
    {
        if (!enable_mouse_area_drag || scroller_manager == null || _dragAreaRect == null)
        {
            return;
        }

        if (_eventDragActive)
        {
            _mouseAreaDragActive = false;
            _mouseAreaDragHasBegun = false;
            return;
        }

        Mouse mouse = Mouse.current;
        if (mouse == null)
        {
            return;
        }

        Vector2 mousePosition = mouse.position.ReadValue();
        Camera eventCamera = GetEventCamera();
        if (mouse.leftButton.wasPressedThisFrame && RectTransformUtility.RectangleContainsScreenPoint(_dragAreaRect, mousePosition, eventCamera))
        {
            _mouseAreaDragActive = true;
            _mouseAreaDragHasBegun = false;
            _lastMousePosition = mousePosition;
            scroller_manager.NotifyPointerDown();
            return;
        }

        if (_mouseAreaDragActive && mouse.leftButton.isPressed)
        {
            Vector2 currentMousePosition = mouse.position.ReadValue();
            Vector2 deltaPointer = currentMousePosition - _lastMousePosition;
            float deltaPixels = ScrollerAxisAdapter.GetPrimary(deltaPointer, GetActiveAxis());
            _lastMousePosition = currentMousePosition;

            if (Mathf.Abs(deltaPixels) > 0.01f)
            {
                if (!_mouseAreaDragHasBegun)
                {
                    _mouseAreaDragHasBegun = true;
                    scroller_manager.BeginUserDrag();
                }

                float canvasScale = (_canvas != null && _canvas.scaleFactor > 0f) ? _canvas.scaleFactor : 1f;
                float direction = invert_drag_direction ? -1f : 1f;
                float deltaUnits = (deltaPixels / canvasScale) * drag_pixels_to_units * direction;
                float dt = Time.unscaledDeltaTime;
                scroller_manager.ApplyUserDragDelta(deltaUnits, dt);
            }
        }

        if (_mouseAreaDragActive && mouse.leftButton.wasReleasedThisFrame)
        {
            if (_mouseAreaDragHasBegun)
            {
                scroller_manager.EndUserDrag();
            }

            _mouseAreaDragActive = false;
            _mouseAreaDragHasBegun = false;
        }
    }

    private Camera GetEventCamera()
    {
        if (_canvas == null)
        {
            return null;
        }

        return _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;
    }

    private ScrollerAxis GetActiveAxis()
    {
        return scroller_manager != null ? scroller_manager.ScrollAxis : ScrollerAxis.Vertical;
    }

    private System.Collections.IEnumerator SpinRoutine(float directionSign, float speedUnitsPerSecond, float durationSeconds)
    {
        float elapsed = 0f;
        scroller_manager.NotifyPointerDown();
        scroller_manager.BeginUserDrag();

        while (elapsed < durationSeconds)
        {
            float dt = spin_use_unscaled_time ? Time.unscaledDeltaTime : Time.deltaTime;
            if (dt <= 0f)
            {
                yield return null;
                continue;
            }

            float direction = invert_drag_direction ? -directionSign : directionSign;
            float deltaUnits = direction * speedUnitsPerSecond * dt;
            scroller_manager.ApplyUserDragDelta(deltaUnits, dt);

            elapsed += dt;
            yield return null;
        }

        scroller_manager.EndUserDrag();
        _spinRoutine = null;
    }
}
