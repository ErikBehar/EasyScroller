using EasyScroller;
using TMPro;
using UnityEngine;

public class ScrollToIndex : MonoBehaviour
{
    public TMP_InputField input_field;
    public ScrollerManager scroll_manager;

    public void DoScrollToIndex()
    {
        scroll_manager.ScrollToLogicalIndex(int.Parse(input_field.text));
    }
}
