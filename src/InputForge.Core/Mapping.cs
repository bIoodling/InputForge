using System.Numerics;

namespace InputForge.Core;

public enum Control
{
    A, B, X, Y, LeftBumper, RightBumper, LeftTrigger, RightTrigger, View, Menu,
    Up, Down, Left, Right, LeftClick, RightClick,
    LeftUp, LeftDown, LeftLeft, LeftRight, RightUp, RightDown, RightLeft, RightRight
}

public enum ResponseCurve { Linear, Precision, Smooth, Aggressive, Custom, Dynamic }
public enum Smoothing { Off, Exponential, Adaptive, LowPass }

public sealed record MouseSettings(float Horizontal = 1, float Vertical = 1, float Deadzone = .08f,
    float Acceleration = 0, Smoothing Filter = Smoothing.Exponential,
    ResponseCurve Curve = ResponseCurve.Linear, float Point1 = .25f, float Point2 = .75f,
    float Input1 = 1f / 3, float Input2 = 2f / 3, float Saturation = 1, float AntiDeadzone = 0,
    bool InterpolatedKnots = false, float MaxOutput = 1);

public readonly record struct ControllerState(uint Buttons, Vector2 Left, Vector2 Right, float LT, float RT)
{
    public bool IsPressed(Control control) => (Buttons & (1u << (int)control)) != 0;

    public ushort XboxButtons =>
        (ushort)(Flag(Control.Up, 0x1) | Flag(Control.Down, 0x2) | Flag(Control.Left, 0x4) |
        Flag(Control.Right, 0x8) | Flag(Control.Menu, 0x10) | Flag(Control.View, 0x20) |
        Flag(Control.LeftClick, 0x40) | Flag(Control.RightClick, 0x80) |
        Flag(Control.LeftBumper, 0x100) | Flag(Control.RightBumper, 0x200) |
        Flag(Control.A, 0x1000) | Flag(Control.B, 0x2000) | Flag(Control.X, 0x4000) | Flag(Control.Y, 0x8000));

    private int Flag(Control control, int mask) => IsPressed(control) ? mask : 0;
}

public static class DefaultBindings
{
    public const int LeftShift = 0xA0;
    public const int RightShift = 0xA1;
    public const int MouseLeft = 256;
    public const int MouseRight = 257;
    public static Dictionary<Control, int> Create() => new()
    {
        [Control.A] = 0x20, [Control.B] = LeftShift, [Control.X] = 'R', [Control.Y] = 'F',
        [Control.LeftBumper] = 'Q', [Control.RightBumper] = 'E',
        [Control.LeftTrigger] = MouseRight, [Control.RightTrigger] = MouseLeft,
        [Control.View] = 'N', [Control.Menu] = 'M',
        [Control.Up] = '1', [Control.Down] = '2', [Control.Left] = '3', [Control.Right] = '4',
        [Control.LeftClick] = 'C', [Control.RightClick] = 'V',
        [Control.LeftUp] = 'W', [Control.LeftDown] = 'S', [Control.LeftLeft] = 'A', [Control.LeftRight] = 'D',
        [Control.RightUp] = 0x26, [Control.RightDown] = 0x28,
        [Control.RightLeft] = 0x25, [Control.RightRight] = 0x27
    };
}

public sealed class Mapper
{
    private Vector2 filtered;
    public Vector2 MouseTarget { get; private set; }

    public void Reset() => filtered = MouseTarget = default;

    public ControllerState Update(IReadOnlyDictionary<Control, int> bindings, ReadOnlySpan<bool> held,
        Vector2 delta, MouseSettings mouse, float seconds)
    {
        uint buttons = 0;
        foreach (var (control, input) in bindings)
            if (input > 0 && input < held.Length && held[input])
                buttons |= 1u << (int)control;

        bool Has(Control control) => (buttons & (1u << (int)control)) != 0;
        float Axis(Control negative, Control positive) => (Has(positive) ? 1 : 0) - (Has(negative) ? 1 : 0);

        // A stalled output tick must not turn old motion into a fresh deflection.
        var target = ProcessMouse(delta, mouse, seconds);
        Vector2 left = new(Axis(Control.LeftLeft, Control.LeftRight), Axis(Control.LeftDown, Control.LeftUp));
        Vector2 right = new(Axis(Control.RightLeft, Control.RightRight), Axis(Control.RightDown, Control.RightUp));
        return new(buttons, Limit(left), Limit(target + right), Has(Control.LeftTrigger) ? 1 : 0, Has(Control.RightTrigger) ? 1 : 0);
    }

    public Vector2 ProcessMouse(Vector2 delta, MouseSettings mouse, float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > .1f)
        {
            Reset();
            return default;
        }
        var scaled = new Vector2(delta.X * mouse.Horizontal, -delta.Y * mouse.Vertical) *
            (.004f / Math.Max(seconds, .001f));
        float magnitude = scaled.Length();
        var target = Vector2.Zero;
        if (magnitude > mouse.Deadzone)
        {
            float accelerated = magnitude * (1 + mouse.Acceleration * Math.Min(magnitude, 1));
            float normalized = Math.Clamp((accelerated - mouse.Deadzone) / (mouse.Saturation - mouse.Deadzone), 0, 1);
            float output = EvaluateCurve(normalized, mouse);
            if (output > 0) output = mouse.AntiDeadzone + (1 - mouse.AntiDeadzone) * output;
            target = scaled / magnitude * (output * mouse.MaxOutput);
        }
        MouseTarget = target;

        float timeConstant = mouse.Filter switch
        {
            Smoothing.Exponential => .018f,
            Smoothing.Adaptive => .028f - .022f * target.Length(),
            Smoothing.LowPass => .04f,
            _ => 0
        };
        float weight = timeConstant == 0 ? 1 : 1 - MathF.Exp(-seconds / timeConstant);
        filtered = Vector2.Lerp(filtered, target, weight);
        if (target == Vector2.Zero && filtered.LengthSquared() < .000004f)
            filtered = Vector2.Zero;

        return filtered;
    }

    public static float EvaluateCurve(float x, MouseSettings mouse) => mouse.Curve switch
    {
        ResponseCurve.Precision => x * x,
        ResponseCurve.Smooth => x * x * (3 - 2 * x),
        ResponseCurve.Aggressive => MathF.Sqrt(x),
        ResponseCurve.Dynamic => x * (.65f + .35f * x * x),
        ResponseCurve.Custom => CurveKnots.Evaluate(x, mouse),
        _ => x
    };

    private static Vector2 Limit(Vector2 vector) =>
        vector.LengthSquared() > 1 ? Vector2.Normalize(vector) : vector;
}
