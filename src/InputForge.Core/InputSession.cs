using System.Numerics;

namespace InputForge.Core;

public sealed class InputSession
{
    private readonly bool[] held = new bool[261];
    private readonly Mapper mapper = new();
    private Vector2 delta;
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
        delta.X = Math.Clamp(delta.X + x, -4096, 4096);
        delta.Y = Math.Clamp(delta.Y + y, -4096, 4096);
    }

    public ControllerState Update(IReadOnlyDictionary<Control, int> bindings, MouseSettings mouse, float seconds)
    {
        if (window == 0) return default;
        var state = mapper.Update(bindings, held, delta, mouse, seconds);
        LastMouseSample = new(seconds, delta, mapper.MouseTarget, state.Right);
        delta = default;
        return state;
    }
}
