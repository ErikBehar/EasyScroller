using EasyScroller;
using UnityEngine;
using TMPro;

public class AddItemIndex : MonoBehaviour
{
    public TMP_InputField input_field;
    public ScrollerManager scroll_manager;

    public void AddItemToScroller()
    {
        scroll_manager.InsertItemAtRuntime( int.Parse(input_field.text));
    }
}