using System.Runtime.InteropServices;
using InputForge.App;
using InputForge.Core;
using Xunit;

namespace InputForge.Tests;

public sealed class DriverFactAttribute : FactAttribute
{
    public DriverFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("INPUTFORGE_DRIVER_TEST") != "1")
            Skip = "Opt in with INPUTFORGE_DRIVER_TEST=1 on a Windows machine with ViGEmBus installed.";
    }
}

public class VirtualXboxIntegrationTests
{
    [DriverFact]
    public void InstalledDriverReportsKnownStateThroughXInputAndDisconnects()
    {
        using var controller = new VirtualXbox();
        var desired = new ControllerReport(0x3000, -12000, 21000, 27000, -16000, 81, 201);
        controller.Submit(desired);

        int index = -1;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { index = controller.UserIndex; return index is >= 0 and < 4; }
            catch { return false; }
        }, TimeSpan.FromSeconds(3)), "No XInput slot was assigned. Check the installed driver and available slots.");

        Assert.True(WaitFor(index, desired));
        controller.Submit(default);
        Assert.True(WaitFor(index, default));
        controller.Dispose();
        Assert.True(SpinWait.SpinUntil(() => XInputGetState((uint)index, out _) == 1167, TimeSpan.FromSeconds(3)),
            "Expected the virtual XInput slot to disappear.");
    }

    private static bool WaitFor(int index, ControllerReport report) => SpinWait.SpinUntil(() =>
        XInputGetState((uint)index, out var state) == 0 &&
        state.Buttons == report.Buttons && state.LX == report.LX && state.LY == report.LY &&
        state.RX == report.RX && state.RY == report.RY && state.LT == report.LT && state.RT == report.RT,
        TimeSpan.FromSeconds(3));

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        internal uint Packet;
        internal ushort Buttons;
        internal byte LT, RT;
        internal short LX, LY, RX, RY;
    }

    [DllImport("xinput1_4")]
    private static extern uint XInputGetState(uint index, out XInputState state);
}
