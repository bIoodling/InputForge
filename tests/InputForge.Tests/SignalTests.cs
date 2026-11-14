using System.Numerics;
using InputForge.Core;
using Xunit;

namespace InputForge.Tests;

public class SignalTests
{
    [Fact]
    public void StalledOrInvalidTickCannotReplayStaleMouseMotion()
    {
        var mapper = new Mapper();
        var settings = new MouseSettings(Filter: Smoothing.Off);
        Assert.NotEqual(Vector2.Zero, mapper.ProcessMouse(new(1, 1), settings, .008f));
        Assert.Equal(Vector2.Zero, mapper.ProcessMouse(new(4000, 4000), settings, .2f));
        Assert.Equal(Vector2.Zero, mapper.MouseTarget);
        Assert.Equal(Vector2.Zero, mapper.ProcessMouse(default, settings, .008f));
        Assert.Equal(Vector2.Zero, mapper.ProcessMouse(new(1, 1), settings, float.NaN));
    }

    [Fact]
    public void ReplayUsesSameMapperAndDoesNotPersistFilterState()
    {
        MouseSample[] samples = [new(.008f,new(1,0),default,default),
            new(.008f,new(30,0),default,default), new(.008f,default,default,default)];
        var settings = new MouseSettings(Filter: Smoothing.Off);
        var first = ReplayStats.Measure(samples, settings);
        Assert.Equal(first, ReplayStats.Measure(samples, settings));
        Assert.Equal(3, first.Samples);
        Assert.Equal(1, first.Saturated);
        Assert.Equal(1f, first.Peak);
        Assert.NotEqual(first.Mean, ReplayStats.Measure(samples, settings with { Filter = Smoothing.LowPass }).Mean);
    }
}
