namespace InputForge.Core;

// A trip remains latched across focus changes, config edits and backend recovery.
public sealed class EngineSafety
{
    public const int EmergencyKey = 0x7A; // F11. Windows reserves F12 for the debugger.
    private string? reason;
    public string? Reason => Volatile.Read(ref reason);
    public void Trip(string message) => Interlocked.CompareExchange(ref reason, message, null);
    public bool Acknowledge(string observed) => ReferenceEquals(Interlocked.CompareExchange(ref reason, null, observed), observed);
}

public sealed class ReportDeadline(long frequency, int rate, long start)
{
    private readonly long interval = Math.Max(1, frequency / rate);
    public long Next { get; private set; } = start;
    public long Missed { get; private set; }

    public bool Take(long now)
    {
        if (now < Next) return false;
        long skipped = (now - Next) / interval;
        Missed += skipped;
        Next += (skipped + 1) * interval;
        return true;
    }
}
