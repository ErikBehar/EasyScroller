using UnityEngine;

public class ItemExample : MonoBehaviour
{
    private ScrollerItemRuntimeInfo info;

    public UnityEngine.Events.UnityEvent onCenterEnter;
    public UnityEngine.Events.UnityEvent onCenterExit;

    private void Start()
    {
        GetInfo();
        info.OnCenteredStateChanged.AddListener(OnCenterStateChanged);

        if ( info.IsCentered)
        {
            onCenterEnter.Invoke();
        }
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
        GetInfo();
        info.RequestRemoveSelf();
    }

    private void GetInfo()
    {
        if (info == null)
        {
            info = transform.parent.GetComponent<ScrollerItemRuntimeInfo>();
        }
    }
}
