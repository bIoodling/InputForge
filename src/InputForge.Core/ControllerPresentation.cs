using System.Numerics;

namespace InputForge.Core;

public readonly record struct OrbitPose(double Yaw, double Pitch, double Distance)
{
    public static OrbitPose Front => new(-.14, .19, 6.8);
    public static OrbitPose Rear => new(Math.PI, .24, 6.8);
    public OrbitPose Drag(double dx, double dy) => this with
    {
        Yaw = Math.IEEERemainder(Yaw - dx * .009, 2 * Math.PI),
        Pitch = Math.Clamp(Pitch + dy * .009, -1.35, 1.35)
    };

    public OrbitPose Zoom(double steps) => this with
    {
        Distance = Math.Clamp(Distance * Math.Pow(.88, steps), 4.9, 12)
    };

    public OrbitPose Interpolate(OrbitPose target, double amount) => new(
        Yaw + Math.IEEERemainder(target.Yaw - Yaw, 2 * Math.PI) * amount,
        Pitch + (target.Pitch - Pitch) * amount,
        Distance + (target.Distance - Distance) * amount);

    public Vector3 Position(float distanceScale = 1) => (float)(Distance * distanceScale) * new Vector3(
        (float)(Math.Sin(Yaw) * Math.Cos(Pitch)), (float)Math.Sin(Pitch),
        (float)(Math.Cos(Yaw) * Math.Cos(Pitch)));
}

public readonly record struct PartMotion(bool Active, Vector2 Stick, float Travel)
{
    public static PartMotion From(Control id, ControllerState state)
    {
        bool pressed = state.IsPressed(id);
        Vector2 stick = id switch
        {
            Control.LeftClick => state.Left,
            Control.RightClick => state.Right,
            _ => Vector2.Zero
        };
        float travel = id switch
        {
            Control.LeftTrigger => Math.Clamp(state.LT, 0, 1),
            Control.RightTrigger => Math.Clamp(state.RT, 0, 1),
            _ => pressed ? 1 : 0
        };
        return new(pressed || travel > 0 || stick.LengthSquared() > 0, stick, travel);
    }
}
