using EasyScroller;
using TMPro;
using UnityEngine;

namespace EasyScroller.Samples
{
    public class ScrollToIndex : MonoBehaviour
    {
        public TMP_InputField input_field;
        public ScrollerManager scroll_manager;

        public void DoScrollToIndex()
        {
            if (input_field == null || !int.TryParse(input_field.text, out int visibleSlot))
            {
                Debug.LogWarning("ScrollToIndex: enter a valid integer slot.");
                return;
            }

            scroll_manager.ScrollToVisibleSlot(visibleSlot);
        }
    }
}
