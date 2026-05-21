using UnityEngine;
using EasyScroller;

public class ItemExample : MonoBehaviour
{
    private ScrollerItemRuntimeInfo info;

    public UnityEngine.Events.UnityEvent onCenterEnter;
    public UnityEngine.Events.UnityEvent onCenterExit;

    private void Start()
    {
        if (!TryBindInfo())
        {
            return;
        }

        info.OnCenteredStateChanged.AddListener(OnCenterStateChanged);
        OnCenterStateChanged(info.IsCentered);
    }

    public void OnCenterStateChanged(bool centered)
    {
        if (centered)
        {
            onCenterEnter.Invoke();
        }
        else
        {
            onCenterExit.Invoke();
        }
    }

    public void DeleteSelf()
    {
        if (!TryBindInfo())
        {
            return;
        }

        info.RequestRemoveSelf();
    }

    private void OnDestroy()
    {
        if (info != null)
        {
            info.OnCenteredStateChanged.RemoveListener(OnCenterStateChanged);
        }
    }

    private bool TryBindInfo()
    {
        if (info != null)
        {
            return true;
        }

        Transform current = transform.parent;
        while (current != null)
        {
            info = current.GetComponent<ScrollerItemRuntimeInfo>();
            if (info != null)
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }
}
