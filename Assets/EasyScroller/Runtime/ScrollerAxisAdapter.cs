using UnityEngine;

namespace EasyScroller
{
    public enum ScrollerAxis
    {
        Vertical = 0,
        Horizontal = 1
    }

    public static class ScrollerAxisAdapter
    {
        public static float GetPrimary(Vector2 value, ScrollerAxis axis)
        {
            return axis == ScrollerAxis.Vertical ? value.y : value.x;
        }

        public static Vector2 WithPrimary(Vector2 original, float primary, ScrollerAxis axis)
        {
            return axis == ScrollerAxis.Vertical
                ? new Vector2(original.x, primary)
                : new Vector2(primary, original.y);
        }

        public static float GetRectHalfSize(RectTransform rect, ScrollerAxis axis, float fallback)
        {
            if (rect == null)
            {
                return fallback;
            }

            float size = axis == ScrollerAxis.Vertical
                ? Mathf.Abs(rect.rect.height)
                : Mathf.Abs(rect.rect.width);

            float half = size * 0.5f;
            return half > 0.01f ? half : fallback;
        }

        public static float GetSizeDeltaPrimary(RectTransform rect, ScrollerAxis axis)
        {
            if (rect == null)
            {
                return 0f;
            }

            return axis == ScrollerAxis.Vertical
                ? Mathf.Abs(rect.sizeDelta.y)
                : Mathf.Abs(rect.sizeDelta.x);
        }

        public static float GetPrimaryScale(Vector3 scale, ScrollerAxis axis)
        {
            return axis == ScrollerAxis.Vertical ? Mathf.Abs(scale.y) : Mathf.Abs(scale.x);
        }

        public static float MeasureRectInContainer(RectTransform container, RectTransform target, Vector3[] worldCornersBuffer, ScrollerAxis axis)
        {
            if (container == null || target == null)
            {
                return 0f;
            }

            Bounds localBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(container, target);
            float boundsSize = axis == ScrollerAxis.Vertical ? Mathf.Abs(localBounds.size.y) : Mathf.Abs(localBounds.size.x);
            if (boundsSize > 0.01f)
            {
                return boundsSize;
            }

            target.GetWorldCorners(worldCornersBuffer);
            Vector3 bottomLeftLocal = container.InverseTransformPoint(worldCornersBuffer[0]);
            Vector3 topLeftLocal = container.InverseTransformPoint(worldCornersBuffer[1]);
            Vector3 bottomRightLocal = container.InverseTransformPoint(worldCornersBuffer[3]);

            return axis == ScrollerAxis.Vertical
                ? Mathf.Abs(topLeftLocal.y - bottomLeftLocal.y)
                : Mathf.Abs(bottomRightLocal.x - bottomLeftLocal.x);
        }
    }
}
