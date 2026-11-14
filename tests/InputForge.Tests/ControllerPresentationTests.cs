using System.Numerics;
using InputForge.Core;
using Xunit;

namespace InputForge.Tests;

public class ControllerPresentationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1.55)]
    [InlineData(3.12)]
    public void DragMovesObjectInScreenDirectionAtEveryYaw(double yaw)
    {
        var pose = OrbitPose.Front with { Yaw = yaw, Pitch = 0 };
        Vector3 eye = pose.Position();
        Vector3 screenRight = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, eye));
        Assert.True(Vector3.Dot(pose.Drag(-20, 0).Position() - eye, screenRight) > 0);
        Assert.True(Vector3.Dot(pose.Drag(20, 0).Position() - eye, screenRight) < 0);
        Assert.True(pose.Drag(0, -20).Position().Y < eye.Y);
        Assert.True(pose.Drag(0, 20).Position().Y > eye.Y);
    }

    [Fact]
    public void OrbitLimitsPitchAndZoomAndInterpolatesAcrossYawWrap()
    {
        var pose = OrbitPose.Front with { Yaw = Math.PI - .1 };
        var other = pose with { Yaw = -Math.PI + .1 };
        Assert.Equal(Math.PI, pose.Interpolate(other, .5).Yaw, 6);
        Assert.True(OrbitPose.Front.Position().Z > 0);
        Assert.True(OrbitPose.Rear.Position().Z < 0);
        Assert.Equal(1.35, pose.Drag(0, 10000).Pitch);
        Assert.Equal(-1.35, pose.Drag(0, -10000).Pitch);
        Assert.Equal(4.9, pose.Zoom(1000).Distance);
        Assert.Equal(12, pose.Zoom(-1000).Distance);
    }

    [Fact]
    public void OutputStateDrivesSimultaneousSticksButtonsAndProportionalTriggers()
    {
        var state = new ControllerState((1u << (int)Control.A) | (1u << (int)Control.LeftClick),
            new(-.4f,.8f), new(.5f,-.2f), .3f, .75f);
        Assert.Equal(new PartMotion(true, state.Left, 1), PartMotion.From(Control.LeftClick,state));
        Assert.Equal(new PartMotion(true, state.Right, 0), PartMotion.From(Control.RightClick,state));
        Assert.Equal(new PartMotion(true,Vector2.Zero,.3f), PartMotion.From(Control.LeftTrigger,state));
        Assert.Equal(new PartMotion(true,Vector2.Zero,.75f), PartMotion.From(Control.RightTrigger,state));
        Assert.Equal(new PartMotion(true,Vector2.Zero,1), PartMotion.From(Control.A,state));
        Assert.Equal(default, PartMotion.From(Control.B,state));
        Assert.All(Enum.GetValues<Control>(), id => Assert.Equal(default,PartMotion.From(id,default)));
    }

    [Fact]
    public void MouseRebindingAndClearingDriveTheVisualState()
    {
        var mouse = new MouseSettings(Filter: Smoothing.Off);
        var mapper = new Mapper();
        var keys = new bool[261];
        var bindings = DefaultBindings.Create();
        keys[DefaultBindings.MouseLeft] = keys[DefaultBindings.MouseRight] = true;
        var output = mapper.Update(bindings, keys, Vector2.Zero, mouse, .008f);
        Assert.True(PartMotion.From(Control.LeftTrigger,output).Active);
        Assert.True(PartMotion.From(Control.RightTrigger,output).Active);
        bindings[Control.RightTrigger] = 0;
        bindings[Control.B] = DefaultBindings.MouseLeft;
        output = mapper.Update(bindings, keys, Vector2.Zero, mouse, .008f);
        Assert.False(PartMotion.From(Control.RightTrigger,output).Active);
        Assert.True(PartMotion.From(Control.B,output).Active);
    }
}
