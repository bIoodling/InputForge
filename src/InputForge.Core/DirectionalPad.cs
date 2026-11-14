namespace InputForge.Core;

public static class DirectionalPad
{
    public static Control Direction(double x, double y) => Math.Abs(x) > Math.Abs(y)
        ? x > 0 ? Control.Right : Control.Left
        : y > 0 ? Control.Up : Control.Down;
}
