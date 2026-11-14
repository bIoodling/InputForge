using System.Windows;
using System.Windows.Controls;
using InputForge.Core;

namespace InputForge.App;

public partial class MainWindow
{
    private SignalMonitor? calibrationMonitor;
    private TextBlock? calibrationReadout;
    private int calibrationSamples;
    private float calibrationPeak;

    private void CalibrateMouse()
    {
        if (!options.Capture || recording || !runtime.Rearm(localTest: true))
        {
            ShowDarkDialog("Mouse calibration", Text(recording ? "Stop your recording first." :
                !options.Capture ? "Enable scoped capture in Settings first." : "The input engine is unavailable."), "Done");
            return;
        }
        bool previous = testing;
        var candidate = preferences.Mouse;
        string[] steps = ["Confirm mouse detection", "Tune low-speed response", "Set the noise threshold",
            "Set minimum stick output", "Balance horizontal and vertical movement", "Choose smoothing",
            "Set output saturation", "Verify and save"];
        string[] hints = ["Move the mouse and check that samples arrive.", "Move slowly and compare the raw and output lines.",
            "Hold the mouse still. Increase the threshold only if unwanted input appears.",
            "Try small deliberate movements. Raise the minimum output only if needed.",
            "Move along each axis and compare the resulting stick coordinates.",
            "Compare quick changes with and without filtering.", "Try fast movement and watch when full output is reached.",
            "Try slow and fast movement. Save the result to the selected profile when satisfied."];
        int step = 0;
        var title = Section("");
        var hint = Text("", 13, "Muted");
        var fields = new StackPanel();
        var monitor = calibrationMonitor = new SignalMonitor { Height = 100 };
        var readout = calibrationReadout = Text("Move the mouse to begin.", 13, "Muted");
        calibrationSamples = 0;
        calibrationPeak = 0;
        void Preview(MouseSettings value)
        {
            candidate = value;
            runtime.Configure(Config() with { Test = true, Editing = preferences with { Mouse = candidate } });
        }
        var back = new Button { Content = "Back" };
        var next = new Button { Content = "Next" };
        back.Click += (_, _) => { step--; Render(); };
        next.Click += (_, _) => { step++; Render(); };
        void Render()
        {
            title.Text = $"{step + 1} / {steps.Length}  ·  {steps[step]}";
            hint.Text = hints[step];
            fields.Children.Clear();
            back.IsEnabled = step > 0;
            next.IsEnabled = step < steps.Length - 1;
            switch (step)
            {
                case 1: fields.Children.Add(Combo("Response curve", Enum.GetValues<ResponseCurve>(), candidate.Curve,
                    value => Preview(candidate with { Curve = value }))); break;
                case 2: fields.Children.Add(SliderFor("Input deadzone", 0, .35, candidate.Deadzone, value => Preview(candidate with { Deadzone = value }))); break;
                case 3: fields.Children.Add(SliderFor("Minimum stick output", 0, .35, candidate.AntiDeadzone, value => Preview(candidate with { AntiDeadzone = value }))); break;
                case 4:
                    fields.Children.Add(SliderFor("Horizontal scale", .1, 4, candidate.Horizontal, value => Preview(candidate with { Horizontal = value })));
                    fields.Children.Add(SliderFor("Vertical scale", .1, 4, candidate.Vertical, value => Preview(candidate with { Vertical = value })));
                    break;
                case 5: fields.Children.Add(Combo("Smoothing", Enum.GetValues<Smoothing>(), candidate.Filter,
                    value => Preview(candidate with { Filter = value }))); break;
                case 6: fields.Children.Add(SliderFor("Saturation", .5, 1, candidate.Saturation,
                    value => Preview(candidate with { Saturation = value }))); break;
            }
        }
        bool save;
        try
        {
            testing = true;
            Preview(candidate);
            Render();
            save = ShowDarkDialog("Precision calibration", Column(title, hint, monitor, readout, fields,
                Buttons(back, next)), "Save to profile", () => step == steps.Length - 1);
        }
        finally
        {
            calibrationMonitor = null;
            calibrationReadout = null;
            testing = previous;
            Apply();
        }
        if (save) { ChangeMouse(candidate); Navigate(page); }
    }

    private void UpdateCalibration(MouseSample sample)
    {
        if (calibrationReadout is null) return;
        calibrationMonitor?.Append(sample);
        if (sample.Raw != default) calibrationSamples++;
        calibrationPeak = Math.Max(calibrationPeak, sample.Raw.Length());
        calibrationReadout.Text = $"{calibrationSamples} moving samples · peak {calibrationPeak:F0} counts\nStick X {sample.Output.X:F2}  Y {sample.Output.Y:F2}";
    }
}
