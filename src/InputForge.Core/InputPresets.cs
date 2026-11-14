namespace InputForge.Core;

public enum InputPreset { Default, Precision, Smooth, Responsive, Competitive, Custom }

public static class InputPresets
{
    public static MouseSettings Create(InputPreset preset) => preset switch
    {
        InputPreset.Default => new(Filter: Smoothing.Off),
        InputPreset.Precision => new(.8f, .8f, .065f, .12f, Smoothing.Exponential, ResponseCurve.Precision),
        InputPreset.Smooth => new(.95f, .95f, .08f, .16f, Smoothing.LowPass, ResponseCurve.Smooth),
        InputPreset.Responsive => new(1.12f, 1.12f, .045f, .1f, Smoothing.Adaptive, ResponseCurve.Aggressive),
        InputPreset.Competitive => new(1, 1, .05f, 0, Smoothing.Off, Saturation: .92f),
        InputPreset.Custom => new(Curve: ResponseCurve.Custom, InterpolatedKnots: true),
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };
}
