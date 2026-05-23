using EasyScroller;
using TMPro;
using UnityEngine;

namespace EasyScroller.Samples
{
    public class AddItemIndex : MonoBehaviour
    {
        public TMP_InputField input_field;
        public ScrollerManager scroll_manager;

        public GameObject prefab;

        public void AddItemToScroller()
        {
            if (!TryParseInputIndex(out int itemIndex))
            {
                return;
            }

            scroll_manager.InsertItemAtRuntime(itemIndex);
        }

        public void AddPrefabToScroller()
        {
            if (!TryParseInputIndex(out int itemIndex))
            {
                return;
            }

            scroll_manager.InsertItemAtRuntime(itemIndex, prefab);
        }

        private bool TryParseInputIndex(out int itemIndex)
        {
            itemIndex = 0;
            if (input_field == null || !int.TryParse(input_field.text, out itemIndex))
            {
                Debug.LogWarning("AddItemIndex: enter a valid integer index.");
                return false;
            }

            return true;
        }
    }
}
