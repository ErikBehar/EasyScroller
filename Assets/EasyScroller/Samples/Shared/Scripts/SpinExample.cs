using UnityEngine;

public class SpinExample : MonoBehaviour
{
    public int direction = 1;
    public float duration = 5f;
    public float speed = 5f;
    public ScrollerInputHandler scroll_input;

    public void StartSpin()
    {
        scroll_input.StartSpin(direction, speed, duration);
    }
}
