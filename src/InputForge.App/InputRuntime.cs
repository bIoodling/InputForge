using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Threading.Channels;
using InputForge.Core;

namespace InputForge.App;

internal sealed record RuntimeConfig(bool Enabled, bool Test, bool CaptureAllowed, ProfileLibrary Library, Preferences Editing, int Rate = 125);
internal sealed record RuntimeStatus(ControllerState State, string Connection, string Application, int ReportsPerSecond,
    string? Profile = null, string Input = "", int InputEventsPerSecond = 0,
    double MeanMappingMicroseconds = 0, double PeakMappingMicroseconds = 0,
    long MissedDeadlines = 0, long RejectedMotion = 0, ControllerReport? Submitted = null);

internal sealed class InputRuntime : IDisposable
{
    private readonly Thread worker;
    private readonly InputSession input = new();
    private readonly Channel<MouseSample> samples = Channel.CreateBounded<MouseSample>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true, SingleReader = true });
    private readonly nint ownProcess = Environment.ProcessId;
    private RuntimeConfig config;
    private RuntimeStatus status = new(default, "Starting", "Inactive", 0);
    private readonly EngineSafety safety = new();
    private readonly Timer watchdog;
    private long heartbeat = Stopwatch.GetTimestamp();
    private int outputExpected;
    private volatile bool hotkeyRegistered;
    private volatile bool failed;
    private long rejectedMotion;
    private uint scopeStart;
    private bool scopeChanged;
    private ControllerReport? submitted;
    private VirtualXbox? pad;
    private HwndSource? source;
    private nint activeWindow;
    private nint cachedWindow;
    private uint cachedPid;
    private string cachedPath = "";
    private string cachedName = "";
    private long lastTick = Stopwatch.GetTimestamp(), rateStart = Stopwatch.GetTimestamp();
    private int reports, outputRate;
    private int events, inputRate;
    private long mappingTicks, peakTicks;
    private int mappingSamples;
    private double meanMapping, peakMapping;
    private (int Kind, int A, int B) latestInput;
    private NamedProfile? activeProfile;
    private bool sessionNotifications;
    private string connection = "Disconnected";
    private volatile bool stopping;

    public InputRuntime(RuntimeConfig initial)
    {
        config = initial;
        worker = new Thread(Run) { IsBackground = true, Name = "InputForge Raw Input" };
        worker.SetApartmentState(ApartmentState.STA);
        watchdog = new Timer(_ =>
        {
            if (!stopping && Volatile.Read(ref outputExpected) != 0 &&
                Stopwatch.GetElapsedTime(Volatile.Read(ref heartbeat)).TotalMilliseconds > 750)
                safety.Trip("Output worker stopped responding");
        }, null, 250, 250);
        worker.Start();
    }

    public string? StopReason => safety.Reason;
    public bool HotkeyRegistered => hotkeyRegistered;
    public void EmergencyStop() => safety.Trip("Emergency stop");
    public bool Rearm(bool localTest = false)
    {
        if (failed || (!localTest && !hotkeyRegistered) ||
            (Volatile.Read(ref outputExpected) != 0 && safety.Reason is not null)) return false;
        var reason = safety.Reason;
        return reason is null || safety.Acknowledge(reason);
    }
    public void Configure(RuntimeConfig value)
    {
        if (Volatile.Read(ref config) != value) Volatile.Write(ref config, value);
    }
    public RuntimeStatus Read() => Volatile.Read(ref status);
    public bool ReadSample(out MouseSample sample) => samples.Reader.TryRead(out sample);

    private void Run()
    {
        try
        {
            if (stopping) return;
            source = new HwndSource(new HwndSourceParameters("InputForge Raw Input") { ParentWindow = new nint(-3) });
            source.AddHook(OnMessage);
            hotkeyRegistered = RegisterHotKey(source.Handle, 1, 0x4007, EngineSafety.EmergencyKey);
            sessionNotifications = WTSRegisterSessionNotification(source.Handle, 0);
            var devices = new RawDevice[] { new(1, 2, 0x2100, source.Handle), new(1, 6, 0x2100, source.Handle) };
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawDevice>()))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            NativeReportLoop.Run(() => stopping,
                () => activeWindow == 0 && pad is null ? 60 : SupportedRate(Volatile.Read(ref config).Rate), Update);
        }
        catch (Exception ex)
        {
            failed = true;
            safety.Trip($"Input worker failed ({ex.GetType().Name})");
            Volatile.Write(ref status, new(default, $"Input unavailable ({ex.GetType().Name})", "Inactive", 0));
        }
        finally
        {
            Disconnect();
            if (sessionNotifications && source is not null) WTSUnRegisterSessionNotification(source.Handle);
            if (hotkeyRegistered && source is not null) UnregisterHotKey(source.Handle, 1);
            hotkeyRegistered = false;
            source?.Dispose();
        }
    }

    private static int SupportedRate(int rate) => rate is 125 or 250 or 500 ? rate : 125;

    private nint Focus(RuntimeConfig options, out NamedProfile? profile)
    {
        profile = null;
        if (stopping || safety.Reason is not null || !options.CaptureAllowed ||
            (!options.Test && (!options.Enabled || !hotkeyRegistered))) return 0;
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
            cachedName = "";
            nint process = OpenProcess(0x1000, false, pid);
            if (process != 0)
            {
                try
                {
                    var path = new StringBuilder(32768);
                    int length = path.Capacity;
                    if (QueryFullProcessImageName(process, 0, path, ref length))
                    {
                        cachedPath = path.ToString();
                        cachedName = System.IO.Path.GetFileName(cachedPath);
                    }
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
            scopeChanged = true;
            scopeStart = unchecked((uint)Environment.TickCount);
            activeWindow = foreground;
        }
        return foreground != 0;
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == 1)
        {
            EmergencyStop();
            handled = true;
            return 0;
        }
        if ((message == 0x02B1 && (wParam == 7 || wParam == 2 || wParam == 4)) ||
            (message == 0x0218 && (wParam == 4 || wParam == 18)))
        {
            if (config.Enabled || config.Test) safety.Trip("Windows session or power transition");
            input.Clear();
            return 0;
        }
        if (message == 0x00FE)
        {
            input.Clear();
            return 0;
        }
        if (message != 0x00FF || !ApplyScope(Volatile.Read(ref config))) return 0;
        uint messageTime = unchecked((uint)GetMessageTime());
        long uptime = Environment.TickCount64;
        uint age = unchecked((uint)uptime - messageTime);
        if (unchecked((int)(messageTime - scopeStart)) < 0) return 0;
        if (age > 1000)
        {
            input.Clear();
            safety.Trip("Input message backlog exceeded safety limit");
            return 0;
        }
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
            else if (packet.Key != EngineSafety.EmergencyKey) input.SetButton(packet.Key, down);
        }
        if (packet.Header.Type == 0 && bytes >= 48)
        {
            if (config.Test) latestInput = packet.MouseButtons == 0 ? (2, packet.X, packet.Y) : (3, packet.MouseButtons, 0);
            if ((packet.MouseFlags & 1) == 0)
            {
                // GetMessageTime is coarse. Use it to reject old queued packets;
                // use the monotonic high-resolution clock for fresh motion bins.
                long stamp = MotionClock(Stopwatch.GetTimestamp());
                if (age >= 32 || !input.Move(packet.X, packet.Y, stamp, stamp)) rejectedMotion++;
            }
            for (int i = 0; i < 5; i++)
            {
                if ((packet.MouseButtons & (1 << (2 * i))) != 0) input.SetButton(256 + i, true);
                if ((packet.MouseButtons & (2 << (2 * i))) != 0) input.SetButton(256 + i, false);
            }
        }
        return 0;
    }

    private void Update(long now, long missed)
    {
        var options = Volatile.Read(ref config);
        Volatile.Write(ref heartbeat, now);
        float seconds = (float)Stopwatch.GetElapsedTime(lastTick, now).TotalSeconds;
        lastTick = now;
        if (seconds > .75f && pad is not null) safety.Trip("Output paused for more than 750 ms");
        bool scoped = ApplyScope(options);
        if (scopeChanged)
        {
            scopeChanged = false;
            if (pad is not null)
            {
                try { Submit(default); }
                catch { Fault(); scoped = false; }
            }
        }
        Volatile.Write(ref outputExpected, scoped && !options.Test || pad is not null ? 1 : 0);
        if (!scoped || options.Test)
        {
            if (pad is not null) Disconnect();
            connection = safety.Reason ?? (!hotkeyRegistered ? "Emergency shortcut unavailable" :
                options.Test ? "Testing locally" : "Standby");
        }
        else if (pad is null)
        {
            try
            {
                pad = new VirtualXbox();
                connection = "Connected";
            }
            catch { Fault(); scoped = false; }
        }

        var settings = activeProfile?.Settings ?? options.Editing;
        long mappingStart = Stopwatch.GetTimestamp();
        var output = scoped ? input.Update(settings.Bindings, settings.Mouse, seconds, MotionClock(mappingStart)) : default;
        if (scoped)
        {
            long duration = Stopwatch.GetTimestamp() - mappingStart;
            mappingTicks += duration;
            peakTicks = Math.Max(peakTicks, duration);
            mappingSamples++;
        }
        if (scoped && options.Test) samples.Writer.TryWrite(input.LastMouseSample);
        if (pad is not null)
        {
            try
            {
                if (stopping || safety.Reason is not null || GetForegroundWindow() != activeWindow ||
                    !ReferenceEquals(options, Volatile.Read(ref config)))
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
            double microsecondsPerTick = 1_000_000.0 / Stopwatch.Frequency;
            meanMapping = mappingSamples == 0 ? 0 : mappingTicks * microsecondsPerTick / mappingSamples;
            peakMapping = peakTicks * microsecondsPerTick;
            mappingTicks = peakTicks = 0;
            mappingSamples = 0;
            rateStart = now;
        }
        string application = !scoped ? "Inactive" : options.Test ? "InputForge · Testing Mode" : ApplicationName();
        string description = scoped && options.Test ? InputDescription() : "";
        int sentRate = pad is null ? 0 : outputRate, receivedRate = scoped ? inputRate : 0;
        double mean = scoped ? meanMapping : 0, peak = scoped ? peakMapping : 0;
        var current = status;
        if (current.State != output || current.Connection != connection || current.Application != application ||
            current.ReportsPerSecond != sentRate || current.Profile != activeProfile?.Name || current.Input != description ||
            current.InputEventsPerSecond != receivedRate || current.MeanMappingMicroseconds != mean ||
            current.PeakMappingMicroseconds != peak || current.MissedDeadlines != missed ||
            current.RejectedMotion != rejectedMotion || current.Submitted != submitted)
            Volatile.Write(ref status, new(output, connection, application, sentRate, activeProfile?.Name,
                description, receivedRate, mean, peak, missed, rejectedMotion, submitted));
    }

    private string ApplicationName() => cachedPath.Length == 0 ? "Inactive" : cachedName;

    private static long MotionClock(long ticks) => ticks / (Stopwatch.Frequency / 1000);

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
        if (!ControllerReport.TryEncode(state, out var report)) throw new InvalidOperationException("Invalid controller state.");
        pad.Submit(report);
        submitted = report;
    }

    private void Fault()
    {
        safety.Trip("ViGEmBus unavailable");
        Disconnect();
        input.Clear();
        connection = "ViGEmBus unavailable";
    }

    private void Disconnect()
    {
        pad?.Dispose();
        pad = null;
        submitted = null;
    }

    public void Dispose()
    {
        stopping = true;
        watchdog.Dispose();
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

    [DllImport("user32", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [DllImport("user32")]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("user32")]
    private static extern int GetMessageTime();

    [DllImport("wtsapi32", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(nint hwnd, int flags);

    [DllImport("wtsapi32")]
    private static extern bool WTSUnRegisterSessionNotification(nint hwnd);

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
