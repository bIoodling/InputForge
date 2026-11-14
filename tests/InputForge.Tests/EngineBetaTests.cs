using System.IO;
using System.Numerics;
using InputForge.Core;
using Xunit;

namespace InputForge.Tests;

public class EngineBetaTests
{
    private static readonly MouseSettings Linear = new(Deadzone: 0, Filter: Smoothing.Off);

    [Theory]
    [InlineData(1, .078125f)]
    [InlineData(2, .15625f)]
    [InlineData(5, .390625f)]
    [InlineData(50, 1)]
    public void MotionIsProportionalThenSaturates(int counts, float expected)
    {
        var mapper = new Mapper();
        Assert.Equal(expected, mapper.ProcessMouse(new(counts, 0), Linear, .008f, .016f).X, 5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void EquivalentVelocitySurvivesMouseRatesAndOutputCadences(int cadence)
    {
        var window = new MotionWindow();
        var mapper = new Mapper();
        for (int now = 1; now <= 160; now++)
        {
            if ((now - 1) % cadence == 0) Assert.True(window.Add(cadence, -cadence, now, now));
            if (now < 16) continue;
            foreach (float tick in new[] { .002f, .004f, .008f, .018f })
            {
                var output = mapper.ProcessMouse(window.Counts(now), Linear, tick, .016f);
                Assert.InRange(output.X, .706f, .708f);
                Assert.InRange(output.Y, .706f, .708f);
            }
        }
    }

    [Fact]
    public void BatchingAndRapidAlternationDoNotBiasTheAccumulator()
    {
        var split = new MotionWindow();
        split.Add(800_000, 0, 8, 8);
        split.Add(-799_995, 0, 8, 10);
        var batched = new MotionWindow();
        batched.Add(5, 0, 8, 10);
        Assert.Equal(batched.Counts(10), split.Counts(10));
        Assert.False(split.Add(int.MinValue, 0, 10, 10));
        Assert.False(split.Add(2, 0, 10, 32));
        Assert.Equal(Vector2.Zero, split.Counts(24));
    }

    [Fact]
    public void SlowAlternationDiagonalAndNoiseThresholdStayBounded()
    {
        var mapper = new Mapper();
        var window = new MotionWindow();
        for (int stamp = 100; stamp < 116; stamp++)
            window.Add(stamp % 2 == 0 ? 1 : -1, 0, stamp, stamp);
        Assert.Equal(Vector2.Zero, mapper.ProcessMouse(window.Counts(115), Linear, .007f, .016f));
        Assert.Equal(Vector2.Zero, mapper.ProcessMouse(new(1, 0), Linear with { NoiseCounts = 1 }, .007f, .016f));
        var diagonal = mapper.ProcessMouse(new(2, -2), Linear, .007f, .016f);
        Assert.Equal(diagonal.X, diagonal.Y);
        Assert.InRange(diagonal.Length(), .22f, .23f);
    }

    [Fact]
    public void LongPauseUsesFreshMotionAndReversalAndStopsReturnToNeutral()
    {
        var session = new InputSession();
        var bindings = DefaultBindings.Create();
        session.SetScope(1, bindings);
        session.Move(4000, 0, 100, 100);
        Assert.Equal(Vector2.UnitX, session.Update(bindings, Linear, .008f, 100).Right);
        session.Move(-4000, 0, 250, 250);
        Assert.Equal(-Vector2.UnitX, session.Update(bindings, Linear, .15f, 250).Right);
        Assert.Equal(Vector2.Zero, session.Update(bindings, Linear, .016f, 266).Right);
        session.Move(4000, 0, 270, 270);
        var filtered = Linear with { Filter = Smoothing.LowPass };
        Assert.True(session.Update(bindings, filtered, .008f, 270).Right.X > 0);
        for (int stamp = 278; stamp < 670; stamp += 8) session.Update(bindings, filtered, .008f, stamp);
        Assert.Equal(Vector2.Zero, session.LastMouseSample.Output);
    }

    [Fact]
    public void FocusSwitchReleasesAxesButtonsAndOldEvents()
    {
        var session = new InputSession();
        var bindings = DefaultBindings.Create();
        session.SetScope(1, bindings);
        foreach (int key in new[] { 'W', (int)'A', 32, DefaultBindings.LeftShift, 256, 257 })
            session.SetButton(key, true);
        session.Move(20, 0, 30, 30);
        var state = session.Update(bindings, Linear, .008f, 30);
        Assert.InRange(state.Left.X, -.708f, -.706f);
        Assert.Equal(Vector2.UnitX, state.Right);
        Assert.True(ControllerReport.TryEncode(state, out var report));
        Assert.Equal((ushort)0x3000, report.Buttons);
        Assert.Equal(byte.MaxValue, report.LT);
        Assert.Equal(byte.MaxValue, report.RT);
        session.SetButton(32, false);
        Assert.Equal((ushort)0x2000, session.Update(bindings, Linear, .008f, 38).XboxButtons);
        session.SetScope(2, bindings);
        Assert.Equal(default, session.Update(bindings, Linear, .008f, 40));
        session.SetScope(0, bindings);
        Assert.False(session.Move(100, 0, 42, 42));
        session.SetScope(1, bindings);
        Assert.Equal(default, session.Update(bindings, Linear, .008f, 50));
    }

    [Fact]
    public void DeadlineSkipsBacklogAndEmergencyLatchRequiresAcknowledgement()
    {
        var deadlines = new ReportDeadline(1000, 125, 0);
        Assert.True(deadlines.Take(0));
        Assert.False(deadlines.Take(7));
        Assert.True(deadlines.Take(41));
        Assert.Equal(4, deadlines.Missed);
        Assert.False(deadlines.Take(41));

        var safety = new EngineSafety();
        safety.Trip("Backend failed");
        safety.Trip("Emergency stop");
        Assert.Equal("Backend failed", safety.Reason);
        Assert.False(safety.Acknowledge("Emergency stop"));
        Assert.True(safety.Acknowledge(safety.Reason!));
        Assert.Null(safety.Reason);
    }

    [Fact]
    public void MalformedValuesDoNotReachXboxReports()
    {
        Assert.False(ControllerReport.TryEncode(new(0, new(float.NaN, 0), default, 0, 0), out var invalid));
        Assert.Equal(default, invalid);
        Assert.True(ControllerReport.TryEncode(new(0, new(-20, 30), default, 2, -1), out var bounded));
        Assert.Equal(short.MinValue, bounded.LX);
        Assert.Equal(short.MaxValue, bounded.LY);
        Assert.Equal(byte.MaxValue, bounded.LT);
        Assert.Equal(0, bounded.RT);

        var mapper = new Mapper();
        Assert.Equal(Vector2.Zero, mapper.ProcessMouse(new(float.PositiveInfinity, 0), Linear, .008f, .016f));
        Assert.Equal(Vector2.Zero, mapper.ProcessMouse(new(12, 0), Linear with { Horizontal = float.NaN }, .008f, .016f));
        var bindings = DefaultBindings.Create();
        bindings[Control.A] = EngineSafety.EmergencyKey;
        var keys = new bool[261];
        keys[EngineSafety.EmergencyKey] = true;
        Assert.Equal(default, mapper.Update(bindings, keys, default, Linear, .008f));
    }

    [Fact]
    public void LegacyProfileMigrationKeepsAssociationAndBindingsAndBacksUpOldBytes()
    {
        string path = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N") + ".json");
        string backup = path + ".v0.bak";
        try
        {
            var settings = Preferences.Create() with { Application = @"C:\Sample.exe" };
            var profile = ProfileLibrary.FromLegacy(settings);
            string legacy = System.Text.Json.JsonSerializer.Serialize(profile);
            File.WriteAllText(path, legacy);
            ProfileStore.WriteLibrary(path, ProfileStore.ReadLibrary(path));
            Assert.Equal(legacy, File.ReadAllText(backup));
            var updated = ProfileStore.ReadLibrary(path);
            Assert.Equal(1, updated.Schema);
            Assert.Equal(settings.Application, updated.Profiles[0].Settings.Application);
            Assert.Equal(settings.Bindings[Control.B], updated.Profiles[0].Settings.Bindings[Control.B]);
            File.WriteAllText(path, """{"Schema":99}""");
            Assert.Throws<InvalidDataException>(() => ProfileStore.ReadLibrary(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(backup);
        }
    }
}
