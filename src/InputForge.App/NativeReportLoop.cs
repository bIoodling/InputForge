using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using InputForge.Core;

namespace InputForge.App;

internal static class NativeReportLoop
{
    public static void Run(Func<bool> stopping, Func<int> rate, Action<long, long> tick)
    {
        using var timer = CreateWaitableTimerEx(0, null, 2, 0x00100002);
        var handles = new[] { timer.DangerousGetHandle() };
        int currentRate = rate();
        var deadlines = new ReportDeadline(Stopwatch.Frequency, currentRate, Stopwatch.GetTimestamp());
        while (!stopping())
        {
            // Never let an input-message flood starve neutralisation or report deadlines.
            for (int i = 0; i < 128 && PeekMessage(out var message, 0, 0, 0, 1); i++)
            {
                if (message.Id == 0x12 || stopping()) return;
                DispatchMessage(ref message);
                if (Stopwatch.GetTimestamp() >= deadlines.Next) break;
            }
            int desiredRate = rate();
            long now = Stopwatch.GetTimestamp();
            if (currentRate != desiredRate)
            {
                currentRate = desiredRate;
                deadlines = new ReportDeadline(Stopwatch.Frequency, currentRate, now);
            }

            if (deadlines.Take(now))
            {
                tick(now, deadlines.Missed);
                continue;
            }

            long remaining = deadlines.Next - now;
            uint timeout = (uint)Math.Max(1, Math.Ceiling(1000.0 * remaining / Stopwatch.Frequency));
            long due = -Math.Max(1, remaining * 10_000_000 / Stopwatch.Frequency);
            bool armed = !timer.IsInvalid && SetWaitableTimer(timer, ref due, 0, 0, 0, false);
            uint result = MsgWaitForMultipleObjectsEx(armed ? 1u : 0u, handles, armed ? 250u : timeout, 0x04FF, 4);
            if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period,
        nint callback, nint context, bool resume);
    [DllImport("user32", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, nint[] handles, uint milliseconds, uint mask, uint flags);
    [DllImport("user32", EntryPoint = "PeekMessageW")]
    private static extern bool PeekMessage(out Message message, nint window, uint first, uint last, uint remove);
    [DllImport("user32", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref Message message);
}
