using System.Numerics;

namespace InputForge.Core;

/// <summary>Relative counts in timestamped 1 ms bins. The output clock never controls the velocity denominator.</summary>
public sealed class MotionWindow
{
    public const int WindowMilliseconds = 16;
    private readonly (long Stamp, long X, long Y)[] bins = new (long, long, long)[32];
    private const int MaxPacketCounts = 1_000_000;

    public void Clear() => Array.Clear(bins);

    public bool Add(int x, int y, long stamp, long now)
    {
        if (stamp < 0 || stamp > now || now - stamp >= WindowMilliseconds ||
            Math.Abs((long)x) > MaxPacketCounts || Math.Abs((long)y) > MaxPacketCounts) return false;
        ref var bin = ref bins[(int)(stamp % bins.Length)];
        if (bin.Stamp != stamp) bin = (stamp, 0, 0);
        bin.X = Math.Clamp(bin.X + x, -1_000_000_000L, 1_000_000_000L);
        bin.Y = Math.Clamp(bin.Y + y, -1_000_000_000L, 1_000_000_000L);
        return true;
    }

    public Vector2 Counts(long now)
    {
        long x = 0, y = 0;
        foreach (var bin in bins)
        {
            long age = now - bin.Stamp;
            if (age < 0 || age >= WindowMilliseconds) continue;
            x += bin.X;
            y += bin.Y;
        }
        return new Vector2(x, y);
    }
}
