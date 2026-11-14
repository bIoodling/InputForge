namespace InputForge.Core;

public static class CurveKnots
{
    public const float Gap = .03f;

    public static MouseSettings Move(MouseSettings mouse, int index, float input, float output)
    {
        if (!float.IsFinite(input) || !float.IsFinite(output)) return mouse;
        mouse = AsCustom(mouse);
        return index switch
        {
            0 => mouse with { Input1 = Math.Clamp(input, Gap, mouse.Input2 - Gap), Point1 = Math.Clamp(output, 0, mouse.Point2) },
            1 => mouse with { Input2 = Math.Clamp(input, mouse.Input1 + Gap, 1 - Gap), Point2 = Math.Clamp(output, mouse.Point1, 1) },
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    public static MouseSettings AsCustom(MouseSettings mouse)
    {
        if (mouse.Curve == ResponseCurve.Custom && mouse.InterpolatedKnots) return mouse;
        return mouse with
        {
            Curve = ResponseCurve.Custom, InterpolatedKnots = true, Input1 = 1f / 3, Input2 = 2f / 3,
            Point1 = Mapper.EvaluateCurve(1f / 3, mouse), Point2 = Mapper.EvaluateCurve(2f / 3, mouse)
        };
    }

    public static float Evaluate(float input, MouseSettings mouse)
    {
        float x = Math.Clamp(input, 0, 1);
        // Retain the old Bézier response until that curve is explicitly edited.
        if (!mouse.InterpolatedKnots)
            return 3 * (1 - x) * (1 - x) * x * mouse.Point1 + 3 * (1 - x) * x * x * mouse.Point2 + x * x * x;
        float a = mouse.Input1, b = mouse.Input2, c = mouse.Point1, d = mouse.Point2;
        float first = c / a, middle = (d - c) / (b - a), last = (1 - d) / (1 - b);
        // Harmonic mean slopes are bounded by twice the smaller secant.
        // Each Hermite segment is monotone and joins its neighbours with C1 continuity.
        float left = Slope(first, middle), right = Slope(middle, last);
        if (x <= a) return Segment(x / a, a, 0, c, first, left);
        if (x <= b) return Segment((x - a) / (b - a), b - a, c, d, left, right);
        return Segment((x - b) / (1 - b), 1 - b, d, 1, right, last);
    }

    private static float Slope(float a, float b) => a == 0 || b == 0 ? 0 : 2 * a * b / (a + b);

    private static float Segment(float t, float width, float low, float high, float startSlope, float endSlope)
    {
        float blend = t * t * (3 - 2 * t);
        float result = low + (high - low) * blend + width * t * (1 - t) *
            ((1 - t) * startSlope - t * endSlope);
        return Math.Clamp(result, low, high);
    }
}
