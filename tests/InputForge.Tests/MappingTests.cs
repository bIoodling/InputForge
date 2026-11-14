using System.Numerics;
using InputForge.Core;
using Xunit;

namespace InputForge.Tests;

public class MappingTests
{
    [Fact]
    public void SimultaneousInputAndIndependentRelease()
    {
        var mapper = new Mapper();
        var keys = new bool[261];
        keys['W'] = keys['A'] = keys[32] = keys[DefaultBindings.MouseRight] = true;
        var bindings = DefaultBindings.Create();
        var mouse = new MouseSettings(Filter: Smoothing.Off);
        var state = mapper.Update(bindings, keys, new Vector2(4, -2), mouse, .008f);
        Assert.InRange(state.Left.Length(), .999f, 1.001f);
        Assert.True(state.Left.X < 0 && state.Left.Y > 0);
        Assert.True(state.Right.X > 0 && state.Right.Y > 0);
        Assert.Equal(1f, state.LT);
        Assert.Equal((ushort)0x1000, state.XboxButtons);
        keys['A'] = false;
        state = mapper.Update(bindings, keys, Vector2.Zero, mouse, .008f);
        Assert.Equal(Vector2.UnitY, state.Left);
        Assert.Equal(Vector2.Zero, state.Right);
        Assert.True(state.IsPressed(Control.A));
    }

    [Fact]
    public void OpposingDirectionsCancelAndResetClearsMouseSmoothing()
    {
        var mapper = new Mapper();
        var keys = new bool[261];
        keys['A'] = keys['D'] = true;
        var bindings = DefaultBindings.Create();
        var mouse = new MouseSettings();
        var state = mapper.Update(bindings, keys, Vector2.One * 10, mouse, .008f);
        Assert.Equal(Vector2.Zero, state.Left);
        Assert.NotEqual(Vector2.Zero, state.Right);
        mapper.Reset();
        Assert.Equal(default, mapper.Update(bindings, new bool[261], Vector2.Zero, mouse, .008f));
    }

    [Theory]
    [InlineData(ResponseCurve.Linear)]
    [InlineData(ResponseCurve.Precision)]
    [InlineData(ResponseCurve.Smooth)]
    [InlineData(ResponseCurve.Aggressive)]
    [InlineData(ResponseCurve.Custom)]
    public void CurvesAreBoundedAndMonotonic(ResponseCurve curve)
    {
        var mouse = new MouseSettings(Curve: curve);
        float last = 0;
        for (int i = 0; i <= 100; i++)
        {
            float value = Mapper.EvaluateCurve(i / 100f, mouse);
            Assert.InRange(value, last, 1);
            last = value;
        }
        Assert.Equal(1f, last);
    }
}
