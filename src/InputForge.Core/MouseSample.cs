using System.Numerics;

namespace InputForge.Core;

public readonly record struct MouseSample(float Seconds, Vector2 Raw, Vector2 Target, Vector2 Output);

public readonly record struct ReplayStats(int Samples, int Saturated, float Peak, float Mean)
{
    public static ReplayStats Measure(IReadOnlyList<MouseSample> samples, MouseSettings settings)
    {
        var mapper = new Mapper();
        int saturated = 0;
        float peak = 0, sum = 0;
        foreach (var sample in samples)
        {
            float magnitude = mapper.ProcessMouse(sample.Raw, settings, sample.Seconds).Length();
            if (magnitude >= .995f) saturated++;
            peak = Math.Max(peak, magnitude);
            sum += magnitude;
        }
        return new(samples.Count, saturated, peak, samples.Count == 0 ? 0 : sum / samples.Count);
    }
}
