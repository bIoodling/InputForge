using InputForge.Core;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;

namespace InputForge.App;

internal sealed class VirtualXbox : IDisposable
{
    private readonly ViGEmClient bus;
    private readonly IXbox360Controller pad;
    private bool disposed;

    public VirtualXbox()
    {
        bus = new ViGEmClient();
        try
        {
            pad = bus.CreateXbox360Controller();
            pad.AutoSubmitReport = false;
            pad.Connect();
            Submit(default);
        }
        catch
        {
            try { pad?.Disconnect(); } catch { }
            try { (pad as IDisposable)?.Dispose(); } catch { }
            bus.Dispose();
            throw;
        }
    }

    public int UserIndex => pad.UserIndex;

    public void Submit(ControllerReport report)
    {
        pad.ButtonState = report.Buttons;
        pad.LeftThumbX = report.LX;
        pad.LeftThumbY = report.LY;
        pad.RightThumbX = report.RX;
        pad.RightThumbY = report.RY;
        pad.LeftTrigger = report.LT;
        pad.RightTrigger = report.RT;
        pad.SubmitReport();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Submit(default); } catch { }
        try { pad.Disconnect(); } catch { }
        try { (pad as IDisposable)?.Dispose(); } catch { }
        try { bus.Dispose(); } catch { }
    }
}
