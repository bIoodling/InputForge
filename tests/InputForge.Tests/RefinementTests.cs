using InputForge.Core;
using Xunit;

namespace InputForge.Tests;

public class RefinementTests
{
    [Fact]
    public void CustomCurvePassesThroughConstrainedKnotsAndRemainsMonotone()
    {
        var settings = CurveKnots.Move(new(), 0, .19f, .75f);
        settings = CurveKnots.Move(settings, 1, .2f, .05f);
        Assert.True(settings.Input2 - settings.Input1 >= CurveKnots.Gap - .000001f);
        Assert.Equal(settings.Point1, Mapper.EvaluateCurve(settings.Input1, settings), 5);
        Assert.Equal(settings.Point2, Mapper.EvaluateCurve(settings.Input2, settings), 5);
        float previous = 0;
        for (int i = 0; i <= 1000; i++)
        {
            float value = Mapper.EvaluateCurve(i / 1000f, settings);
            Assert.InRange(value, previous - .000001f, 1);
            previous = value;
        }
        Assert.Equal(1, previous);
    }

    [Fact]
    public void PresetsAreDistinctValidAndProduceDifferentOutput()
    {
        var values = Enum.GetValues<InputPreset>().Select(InputPresets.Create).ToArray();
        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.All(values, settings => Assert.True((Preferences.Create() with { Mouse = settings }).IsValid()));
        Assert.True(Mapper.EvaluateCurve(.25f, values[(int)InputPreset.Precision]) <
            Mapper.EvaluateCurve(.25f, values[(int)InputPreset.Responsive]));
    }

    [Theory]
    [InlineData(.15, .9, Control.Up)]
    [InlineData(.6, -.9, Control.Down)]
    [InlineData(-.9, .15, Control.Left)]
    [InlineData(.9, -.15, Control.Right)]
    public void DpadUsesFourNonOverlappingRegions(double x, double y, Control target) =>
        Assert.Equal(target, DirectionalPad.Direction(x, y));

    [Fact]
    public void AutoCameraDoesNotRetriggerHeldButtonsAndRespectsManualOrbit()
    {
        var camera = new TestCamera();
        var trigger = new ControllerState(0, default, default, 0, 1);
        Assert.Equal(CameraView.Shoulder, camera.Update(trigger, 1, 2));
        Assert.Null(camera.Update(trigger, 4, 2));
        Assert.Null(camera.Update(default, 5, 2));
        Assert.Equal(CameraView.Front, camera.Update(default, 6, 2));
        camera.Manual(7);
        Assert.Null(camera.Update(trigger, 8, 2));
        Assert.Null(camera.Update(trigger, 10, 2));
        camera.Update(default, 11, 2);
        Assert.Equal(CameraView.Shoulder, camera.Update(trigger, 12, 2));
        Assert.Equal(CameraView.Manual, camera.Update(default, 15, 2));
    }
}
