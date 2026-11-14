namespace InputForge.Core;

public enum CameraView { Front, Shoulder, Rear, Manual }

public sealed class TestCamera
{
    private const uint Shoulders = (1u << (int)Control.LeftBumper) | (1u << (int)Control.RightBumper) |
        (1u << (int)Control.LeftTrigger) | (1u << (int)Control.RightTrigger);
    private const uint Front = ((1u << (int)Control.RightClick + 1) - 1) & ~Shoulders;
    private uint previousButtons;
    private double lastActivity, manualUntil;
    private CameraView? returnView;
    public CameraView Current { get; private set; } = CameraView.Front;

    public void Manual(double now)
    {
        Current = CameraView.Manual;
        returnView = null;
        manualUntil = now + 2;
    }

    public void Reset()
    {
        previousButtons = 0;
        lastActivity = manualUntil = 0;
        returnView = null;
        Current = CameraView.Front;
    }

    public CameraView? Update(ControllerState state, double now, double idle)
    {
        uint held = state.Buttons;
        if (state.LT > .01f) held |= 1u << (int)Control.LeftTrigger;
        if (state.RT > .01f) held |= 1u << (int)Control.RightTrigger;
        uint pressed = held & ~previousButtons;
        previousButtons = held;
        if (now < manualUntil) return null;
        if ((pressed & Shoulders) != 0)
        {
            returnView ??= Current == CameraView.Shoulder ? CameraView.Front : Current;
            lastActivity = now;
            return Change(CameraView.Shoulder);
        }
        if ((pressed & Front) != 0)
        {
            returnView = null;
            return Change(CameraView.Front);
        }
        if ((held & (Shoulders | Front)) != 0) lastActivity = now;
        if (returnView is not { } previous || now - lastActivity < idle) return null;
        returnView = null;
        return Change(previous);
    }

    private CameraView? Change(CameraView view)
    {
        if (view == Current) return null;
        Current = view;
        return view;
    }
}
