using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using InputForge.Core;

namespace InputForge.App;

internal sealed class CurveView(MouseSettings settings) : FrameworkElement
{
    private MouseSettings mouse = settings;
    private int selected;
    private bool dragging;
    private Rect Plot => new(38, 12, Math.Max(1, ActualWidth - 53), Math.Max(1, ActualHeight - 44));

    public event Action<int>? PointSelected;
    public event Action<MouseSettings>? Changed;
    public int Selected => selected;
    public MouseSettings Settings => mouse;

    public void UpdateSettings(MouseSettings value)
    {
        mouse = value;
        InvalidateVisual();
    }

    public void MoveSelected(float input, float output)
    {
        if (Changed is null) return;
        var next = CurveKnots.Move(mouse, selected, input, output);
        if (next == mouse) return;
        UpdateSettings(next);
        Changed(next);
    }

    public void Select(int index)
    {
        selected = index;
        PointSelected?.Invoke(index);
        InvalidateVisual();
    }

    private Point At(double x, double y) => new(Plot.Left + x * Plot.Width, Plot.Bottom - y * Plot.Height);
    private Point Handle(int index)
    {
        var custom = CurveKnots.AsCustom(mouse);
        return index == 0 ? At(custom.Input1, custom.Point1) : At(custom.Input2, custom.Point2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var axes = new Pen(MainWindow.Brush("Border"), 1);
        var face = new Typeface("Segoe UI");
        for (int step = 0; step <= 5; step++)
        {
            double mark = step / 5.0;
            dc.DrawLine(axes, At(mark, 0), At(mark, 1));
            dc.DrawLine(axes, At(0, mark), At(1, mark));
            var label = new FormattedText(mark.ToString("0.0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, face, 11, MainWindow.Brush("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, At(mark, 0) + new Vector(-8, 6));
            dc.DrawText(label, At(0, mark) + new Vector(-28, -7));
        }
        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            context.BeginFigure(At(0, 0), false, false);
            for (int i = 1; i <= 120; i++)
            {
                float x = i / 120f;
                context.LineTo(At(x, Mapper.EvaluateCurve(x, mouse)), true, false);
            }
        }
        curve.Freeze();
        var red = MainWindow.Brush("Accent");
        dc.DrawGeometry(null, new Pen(red, 2), curve);
        if (Changed is null) return;
        for (int i = 0; i < 2; i++)
            dc.DrawEllipse(red, i == selected ? new Pen(MainWindow.Brush("Text"), 1.5) : null, Handle(i), 5, 5);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Changed is null) return;
        var at = e.GetPosition(this);
        var first = (at - Handle(0)).Length;
        var second = (at - Handle(1)).Length;
        if (Math.Min(first, second) > 15) return;
        Select(first <= second ? 0 : 1);
        Focus();
        dragging = CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging || !IsMouseCaptured) return;
        var p = e.GetPosition(this);
        MoveSelected((float)((p.X - Plot.Left) / Plot.Width), (float)((Plot.Bottom - p.Y) / Plot.Height));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => dragging = false;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Changed is null) return;
        var custom = CurveKnots.AsCustom(mouse);
        float input = selected == 0 ? custom.Input1 : custom.Input2;
        float output = selected == 0 ? custom.Point1 : custom.Point2;
        const float step = .005f;
        switch (e.Key)
        {
            case Key.Left: input -= step; break;
            case Key.Right: input += step; break;
            case Key.Up: output += step; break;
            case Key.Down: output -= step; break;
            default: return;
        }
        MoveSelected(input, output);
        e.Handled = true;
    }
}
