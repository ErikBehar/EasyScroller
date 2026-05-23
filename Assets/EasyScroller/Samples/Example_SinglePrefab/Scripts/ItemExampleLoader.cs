using EasyScroller;
using UnityEngine;
using UnityEngine.UI;

namespace EasyScroller.Samples
{
    public class ItemExampleLoader : MonoBehaviour
    {
        private ScrollerItemRuntimeInfo info;

        public Image image_holder;

        // Note: demo-only. Prefer Addressables and hold references in your data source.
        public ExampleDataHolder data;

        public UnityEngine.Events.UnityEvent onCenterEnter;
        public UnityEngine.Events.UnityEvent onCenterExit;

        private void Start()
        {
            if (!TryBindInfo())
            {
                return;
            }

            info.OnCenteredStateChanged.AddListener(OnCenterStateChanged);
            info.OnContentRefreshRequested.AddListener(RefreshContent);
            RefreshContent();
            OnCenterStateChanged(info.IsCentered);
        }

        private void OnCenterStateChanged(bool centered)
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
                info.OnContentRefreshRequested.RemoveListener(RefreshContent);
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

        private void RefreshContent()
        {
            if (!TryBindInfo())
            {
                return;
            }

            if (image_holder == null || data == null || data.sprite_list == null || data.sprite_list.Count == 0)
            {
                return;
            }

            int index = Mathf.Clamp(info.DataIndex, 0, data.sprite_list.Count - 1);
            image_holder.sprite = data.sprite_list[index];
        }
    }
}
