using System.Windows;
using System.Windows.Media;
using InputForge.Core;

namespace InputForge.App;

internal sealed class SignalMonitor : FrameworkElement
{
    private readonly MouseSample[] samples = new MouseSample[180];
    private int next, count;

    public void Append(MouseSample sample)
    {
        samples[next] = sample;
        next = (next + 1) % samples.Length;
        count = Math.Min(count + 1, samples.Length);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width < 12 || height < 12) return;
        dc.DrawRoundedRectangle(MainWindow.Brush("Background"), new Pen(MainWindow.Brush("Border"), 1),
            new Rect(.5,.5,width-1,height-1), 8, 8);
        if (count < 2) return;
        float maxRaw = 1;
        for (int i = 0; i < count; i++) maxRaw = Math.Max(maxRaw, samples[i].Raw.Length());
        var colors = new[] { "Muted", "Text", "Accent" };
        for (int lane = 0; lane < colors.Length; lane++)
        {
            var line = new StreamGeometry();
            using (var path = line.Open())
            for (int index = 0; index < count; index++)
            {
                var sample = samples[(next + samples.Length - count + index) % samples.Length];
                double magnitude = lane switch { 0 => sample.Raw.Length() / maxRaw, 1 => sample.Target.Length(), _ => sample.Output.Length() };
                var point = new Point(6 + (width-12) * index/(count-1), height-6 - (height-12)*Math.Clamp(magnitude, 0, 1));
                if (index == 0) path.BeginFigure(point, false, false); else path.LineTo(point, true, false);
            }
            line.Freeze();
            dc.DrawGeometry(null, new Pen(MainWindow.Brush(colors[lane]), 1.3), line);
        }
    }
}
