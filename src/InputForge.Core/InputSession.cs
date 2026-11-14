using System.Numerics;

namespace InputForge.Core;

public sealed class InputSession
{
    private readonly bool[] held = new bool[261];
    private readonly Mapper mapper = new();
    private Vector2 delta;
    private readonly MotionWindow motion = new();
    private nint window;
    private object? configuration;
    public MouseSample LastMouseSample { get; private set; }

    public bool SetScope(nint nextWindow, object nextConfiguration)
    {
        if (window == nextWindow && ReferenceEquals(configuration, nextConfiguration)) return false;
        window = nextWindow;
        configuration = nextConfiguration;
        Clear();
        return true;
    }

    public void Clear()
    {
        Array.Clear(held);
        delta = default;
        motion.Clear();
        mapper.Reset();
        LastMouseSample = default;
    }

    public void SetButton(int input, bool down)
    {
        if (window == 0 || (uint)input >= (uint)held.Length || input == 0) return;
        held[input] = down;
        if (input is DefaultBindings.LeftShift or DefaultBindings.RightShift)
            held[0x10] = held[DefaultBindings.LeftShift] || held[DefaultBindings.RightShift];
    }

    public void Move(int x, int y)
    {
        if (window == 0) return;
        delta += new Vector2(x, y);
    }

    public bool Move(int x, int y, long stamp, long now)
    {
        if (window == 0 || !motion.Add(x, y, stamp, now)) return false;
        delta = Vector2.Clamp(delta + new Vector2(x, y), new(-1e9f), new(1e9f));
        return true;
    }

    public ControllerState Update(IReadOnlyDictionary<Control, int> bindings, MouseSettings mouse, float seconds, long? now = null)
    {
        if (window == 0) return default;
        Vector2 raw = delta;
        float? windowSeconds = null;
        Vector2 sampled = raw;
        if (now is { } tick)
        {
            sampled = motion.Counts(tick);
            windowSeconds = MotionWindow.WindowMilliseconds / 1000f;
        }
        var state = mapper.Update(bindings, held, sampled, mouse, seconds, windowSeconds);
        LastMouseSample = new(seconds, raw, mapper.MouseTarget, state.Right, sampled, windowSeconds);
        delta = default;
        return state;
    }
}
