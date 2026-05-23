using EasyScroller;
using UnityEngine;

namespace EasyScroller.Samples
{
    public class SpinExample : MonoBehaviour
    {
        public int direction = 1;
        public float duration = 5f;
        public float speed = 5f;
        public ScrollerInputHandler scroll_input;

        public void StartSpin()
        {
            if (scroll_input == null)
            {
                Debug.LogWarning("SpinExample requires a ScrollerInputHandler reference.");
                return;
            }

            scroll_input.StartSpin(direction, speed, duration);
        }
    }
}
