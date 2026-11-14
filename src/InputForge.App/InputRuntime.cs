using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Threading.Channels;
using InputForge.Core;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;

namespace InputForge.App;

internal sealed record RuntimeConfig(bool Enabled, bool Test, bool CaptureAllowed, ProfileLibrary Library, Preferences Editing);
internal sealed record RuntimeStatus(ControllerState State, string Connection, string Application, int ReportsPerSecond,
    string? Profile = null, string Input = "", int InputEventsPerSecond = 0);

internal sealed class InputRuntime : IDisposable
{
    private readonly Thread worker;
    private readonly InputSession input = new();
    private readonly Channel<MouseSample> samples = Channel.CreateBounded<MouseSample>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true, SingleReader = true });
    private readonly nint ownProcess = Environment.ProcessId;
    private RuntimeConfig config;
    private RuntimeStatus status = new(default, "Starting", "Inactive", 0);
    private Dispatcher? dispatcher;
    private ViGEmClient? bus;
    private IXbox360Controller? pad;
    private HwndSource? source;
    private nint activeWindow;
    private nint cachedWindow;
    private uint cachedPid;
    private string cachedPath = "";
    private long lastTick = Stopwatch.GetTimestamp(), rateStart = Stopwatch.GetTimestamp();
    private int reports, outputRate;
    private int events, inputRate;
    private (int Kind, int A, int B) latestInput;
    private NamedProfile? activeProfile;
    private long nextConnect;
    private string connection = "Disconnected";
    private volatile bool stopping;

    public InputRuntime(RuntimeConfig initial)
    {
        config = initial;
        worker = new Thread(Run) { IsBackground = true, Name = "InputForge Raw Input" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public void Configure(RuntimeConfig value) => Volatile.Write(ref config, value);
    public RuntimeStatus Read() => Volatile.Read(ref status);
    public bool ReadSample(out MouseSample sample) => samples.Reader.TryRead(out sample);

    private void Run()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            if (stopping) return;
            source = new HwndSource(new HwndSourceParameters("InputForge Raw Input") { ParentWindow = new nint(-3) });
            source.AddHook(OnMessage);
            var devices = new RawDevice[] { new(1, 2, 0x2100, source.Handle), new(1, 6, 0x2100, source.Handle) };
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawDevice>()))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(8) };
            timer.Tick += (_, _) => Update();
            timer.Start();
            try { if (!stopping) Dispatcher.Run(); }
            finally { timer.Stop(); }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref status, new(default, $"Input unavailable ({ex.GetType().Name})", "Inactive", 0));
        }
        finally
        {
            Disconnect();
            source?.Dispose();
        }
    }

    private nint Focus(RuntimeConfig options, out NamedProfile? profile)
    {
        profile = null;
        if (!options.CaptureAllowed || (!options.Test && !options.Enabled)) return 0;
        nint foreground = GetForegroundWindow();
        if (foreground == 0) return 0;
        GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == 0) return 0;
        if (options.Test) return pid == (uint)ownProcess ? foreground : 0;
        if (pid == (uint)ownProcess) return 0;
        if (pid != cachedPid || foreground != cachedWindow)
        {
            cachedPid = pid;
            cachedWindow = foreground;
            cachedPath = "";
            nint process = OpenProcess(0x1000, false, pid);
            if (process != 0)
            {
                try
                {
                    var path = new StringBuilder(32768);
                    int length = path.Capacity;
                    if (QueryFullProcessImageName(process, 0, path, ref length)) cachedPath = path.ToString();
                }
                finally { CloseHandle(process); }
            }
        }
        profile = options.Library.ForApplication(cachedPath);
        return profile is not null ? foreground : 0;
    }

    private bool ApplyScope(RuntimeConfig options)
    {
        nint foreground = Focus(options, out activeProfile);
        if (input.SetScope(foreground, options))
        {
            latestInput = default;
            if (pad is not null)
            {
                try { Submit(default); }
                catch { Fault(); }
            }
            activeWindow = foreground;
        }
        return foreground != 0;
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x00FE)
        {
            input.Clear();
            return 0;
        }
        if (message != 0x00FF || !ApplyScope(Volatile.Read(ref config))) return 0;
        var packet = new RawPacket();
        uint size = (uint)Marshal.SizeOf<RawPacket>();
        uint bytes = GetRawInputData(lParam, 0x10000003, ref packet, ref size, (uint)Marshal.SizeOf<RawHeader>());
        if (bytes == uint.MaxValue || bytes < 40) return 0;
        if (packet.Header.Type is 0 or 1) events++;
        if (packet.Header.Type == 1 && packet.Key is > 0 and < 255)
        {
            bool down = (packet.KeyFlags & 1) == 0;
            if (config.Test) latestInput = (1, packet.Key, down ? 1 : 0);
            if (packet.Key is 0x10 or DefaultBindings.LeftShift or DefaultBindings.RightShift)
            {
                int side = packet.MakeCode == 0x36 ? DefaultBindings.RightShift : DefaultBindings.LeftShift;
                input.SetButton(side, down);
            }
            else input.SetButton(packet.Key, down);
        }
        if (packet.Header.Type == 0 && bytes >= 48)
        {
            if (config.Test) latestInput = packet.MouseButtons == 0 ? (2, packet.X, packet.Y) : (3, packet.MouseButtons, 0);
            if ((packet.MouseFlags & 1) == 0)
            {
                input.Move(packet.X, packet.Y);
            }
            for (int i = 0; i < 5; i++)
            {
                if ((packet.MouseButtons & (1 << (2 * i))) != 0) input.SetButton(256 + i, true);
                if ((packet.MouseButtons & (2 << (2 * i))) != 0) input.SetButton(256 + i, false);
            }
        }
        return 0;
    }

    private void Update()
    {
        var options = Volatile.Read(ref config);
        var now = Stopwatch.GetTimestamp();
        float seconds = (float)Stopwatch.GetElapsedTime(lastTick, now).TotalSeconds;
        lastTick = now;
        bool scoped = ApplyScope(options);
        if (!scoped || options.Test)
        {
            if (pad is not null) Disconnect();
            connection = options.Test ? "Testing locally" : "Standby";
        }
        else if (pad is null && now >= nextConnect)
        {
            try
            {
                bus = new ViGEmClient();
                pad = bus.CreateXbox360Controller();
                pad.AutoSubmitReport = false;
                pad.Connect();
                connection = "Connected";
            }
            catch { Fault(); }
        }

        var settings = activeProfile?.Settings ?? options.Editing;
        var output = input.Update(settings.Bindings, settings.Mouse, seconds);
        if (scoped && options.Test) samples.Writer.TryWrite(input.LastMouseSample);
        if (pad is not null)
        {
            try
            {
                if (GetForegroundWindow() != activeWindow || !ReferenceEquals(options, Volatile.Read(ref config)))
                {
                    output = default;
                    input.Clear();
                    Submit(output);
                    Disconnect();
                    connection = "Standby";
                }
                else
                {
                    Submit(output);
                    reports++;
                }
            }
            catch { Fault(); output = default; }
        }
        else if (!options.Test) output = default;

        double elapsed = Stopwatch.GetElapsedTime(rateStart, now).TotalSeconds;
        if (elapsed >= 1)
        {
            outputRate = (int)(reports / elapsed);
            reports = 0;
            inputRate = (int)(events / elapsed);
            events = 0;
            rateStart = now;
        }
        var next = new RuntimeStatus(output, connection,
            scoped ? options.Test ? "InputForge · Testing Mode" : System.IO.Path.GetFileName(settings.Application) : "Inactive",
            outputRate, activeProfile?.Name, scoped && options.Test ? InputDescription() : "", scoped ? inputRate : 0);
        if (next != status) Volatile.Write(ref status, next);
    }

    private string InputDescription() => latestInput.Kind switch
    {
        1 => $"Key 0x{latestInput.A:X2} {(latestInput.B == 1 ? "down" : "up")}",
        2 => $"Mouse Δ {latestInput.A}, {latestInput.B}",
        3 => $"Mouse buttons 0x{latestInput.A:X}",
        _ => ""
    };

    private void Submit(ControllerState state)
    {
        if (pad is null) return;
        static short Axis(float value) => (short)MathF.Round(value * (value < 0 ? 32768 : 32767));
        pad.ButtonState = state.XboxButtons;
        pad.LeftThumbX = Axis(state.Left.X);
        pad.LeftThumbY = Axis(state.Left.Y);
        pad.RightThumbX = Axis(state.Right.X);
        pad.RightThumbY = Axis(state.Right.Y);
        pad.LeftTrigger = (byte)MathF.Round(state.LT * 255);
        pad.RightTrigger = (byte)MathF.Round(state.RT * 255);
        pad.SubmitReport();
    }

    private void Fault()
    {
        Disconnect();
        input.Clear();
        connection = "ViGEmBus unavailable";
        nextConnect = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
    }

    private void Disconnect()
    {
        if (pad is not null)
        {
            try { Submit(default); } catch { }
            try { pad.Disconnect(); } catch { }
            // The vendor's interface does not expose IDisposable on all versions.
            if (pad is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
            pad = null;
        }
        try { bus?.Dispose(); } catch { }
        bus = null;
    }

    public void Dispose()
    {
        stopping = true;
        dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        worker.Join(TimeSpan.FromSeconds(2));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawDevice(ushort Page, ushort Usage, uint Flags, nint Target);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawHeader { internal uint Type, Size; internal nint Device, Param; }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct RawPacket
    {
        [FieldOffset(0)] internal RawHeader Header;
        [FieldOffset(24)] internal ushort MouseFlags;
        [FieldOffset(24)] internal ushort MakeCode;
        [FieldOffset(26)] internal ushort KeyFlags;
        [FieldOffset(28)] internal ushort MouseButtons;
        [FieldOffset(30)] internal ushort Key;
        [FieldOffset(36)] internal int X;
        [FieldOffset(40)] internal int Y;
    }

    [DllImport("user32", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);

    [DllImport("user32")]
    private static extern uint GetRawInputData(nint input, uint command, ref RawPacket packet, ref uint size, uint headerSize);

    [DllImport("user32")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("kernel32", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder path, ref int length);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(nint handle);
}
