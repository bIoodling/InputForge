using System.Diagnostics;
using InputForge.App;
using Xunit;
using Xunit.Abstractions;

namespace InputForge.Tests;

public class NativeReportLoopTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(125)]
    [InlineData(250)]
    [InlineData(500)]
    public async Task DedicatedSchedulerProducesReportsAndStops(int rate)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        var ticks = new List<long>();
        long missed = 0;
        var worker = Task.Factory.StartNew(() => NativeReportLoop.Run(() => stop.IsCancellationRequested,
            () => rate, (stamp, lost) => { ticks.Add(stamp); missed = lost; }),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await worker.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(ticks.Count >= 3);
        var duration = Stopwatch.GetElapsedTime(ticks[0], ticks[^1]).TotalSeconds;
        var gaps = ticks.Zip(ticks.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a, b).TotalMilliseconds).Order().ToArray();
        Assert.All(gaps, gap => Assert.True(gap > 0));
        output.WriteLine($"Scheduler-only target {rate} Hz; achieved {(ticks.Count - 1) / duration:F1} Hz; " +
            $"p95 interval {gaps[(int)(.95 * (gaps.Length - 1))]:F2} ms; missed {missed}.");
    }
}
