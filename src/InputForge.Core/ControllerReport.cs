namespace InputForge.Core;

public readonly record struct ControllerReport(ushort Buttons, short LX, short LY, short RX, short RY, byte LT, byte RT)
{
    public static bool TryEncode(ControllerState state, out ControllerReport report)
    {
        report = default;
        if (!float.IsFinite(state.Left.X) || !float.IsFinite(state.Left.Y) ||
            !float.IsFinite(state.Right.X) || !float.IsFinite(state.Right.Y) ||
            !float.IsFinite(state.LT) || !float.IsFinite(state.RT)) return false;
        report = new(state.XboxButtons, Axis(state.Left.X), Axis(state.Left.Y), Axis(state.Right.X), Axis(state.Right.Y),
            Trigger(state.LT), Trigger(state.RT));
        return true;
    }

    private static short Axis(float value)
    {
        value = Math.Clamp(value, -1, 1);
        return (short)MathF.Round(value * (value < 0 ? 32768 : 32767));
    }

    private static byte Trigger(float value) => (byte)MathF.Round(Math.Clamp(value, 0, 1) * 255);
}
