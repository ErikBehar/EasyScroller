using UnityEngine;
using UnityEngine.UI;
using EasyScroller;

public class ItemExampleLoader : MonoBehaviour
{
    private ScrollerItemRuntimeInfo info;

    public Image image_holder;

    //note that this is just a simple demo
    //it would probably be better to load addressables
    //and only hold their references in the data 
    //instead of the direct reference to the sprites
    //in the data holder
    public ExampleDataHolder data;

    public UnityEngine.Events.UnityEvent onCenterEnter;
    public UnityEngine.Events.UnityEvent onCenterExit;

    private void Start()
    {
        GetInfo();
        info.OnCenteredStateChanged.AddListener(OnCenterStateChanged);
        info.OnContentRefreshRequested.AddListener(RefreshContent);
        RefreshContent();
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
        GetInfo();
        info.RequestRemoveSelf();
    }

    private void OnDestroy()
    {
        if (info != null)
        {
            info.OnCenteredStateChanged.RemoveListener(OnCenterStateChanged);
            info.OnContentRefreshRequested.RemoveListener(RefreshContent);
        }
    }

    private void GetInfo()
    {
        if (info == null)
        {
            info = transform.parent.GetComponent<ScrollerItemRuntimeInfo>();
        }
    }

    private void RefreshContent()
    {
        GetInfo();
        if (image_holder == null || data == null || data.sprite_list == null || data.sprite_list.Count == 0)
        {
            return;
        }

        int index = Mathf.Clamp(info.DataIndex, 0, data.sprite_list.Count - 1);
        image_holder.sprite = data.sprite_list[index];
    }
}
