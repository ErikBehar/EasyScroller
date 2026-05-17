using UnityEngine;
using UnityEngine.UI;

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

        image_holder.sprite = data.sprite_list[info.OriginalIndex];

        if (info.IsCentered)
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
