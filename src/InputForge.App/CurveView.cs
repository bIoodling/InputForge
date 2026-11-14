using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using InputForge.Core;

namespace InputForge.App;

internal sealed class CurveView(MouseSettings settings, Action<int, float> onChanged) : FrameworkElement
{
    private MouseSettings mouse = settings;
    private int point = -1;
    private Rect Plot => new(22,18,Math.Max(1,ActualWidth-44),Math.Max(1,ActualHeight-36));

    private Point At(double x, double y) => new(Plot.Left+x*Plot.Width,Plot.Bottom-y*Plot.Height);
    private Point Handle(int index) => At((index+1)/3.0,index == 0 ? mouse.Point1 : mouse.Point2);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent,null,new Rect(RenderSize));
        var axes = new Pen(MainWindow.Brush("Border"),1);
        dc.DrawLine(axes,At(0,1),At(0,0));
        dc.DrawLine(axes,At(0,0),At(1,0));
        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            context.BeginFigure(At(0,0),false,false);
            for (int i = 1; i <= 80; i++)
            {
                float x = i / 80f;
                context.LineTo(At(x,Mapper.EvaluateCurve(x,mouse)),true,false);
            }
        }
        curve.Freeze();
        var red = MainWindow.Brush("Accent");
        dc.DrawGeometry(null,new Pen(red,1.5),curve);
        if (mouse.Curve == ResponseCurve.Custom)
        {
            dc.DrawEllipse(red,null,Handle(0),5,5);
            dc.DrawEllipse(red,null,Handle(1),5,5);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (mouse.Curve != ResponseCurve.Custom) return;
        var at = e.GetPosition(this);
        for (int i=0; i<2; i++)
        {
            if ((at-Handle(i)).Length > 14) continue;
            point = i;
            CaptureMouse();
            e.Handled = true;
            break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (point < 0 || !IsMouseCaptured) return;
        float value = (float)Math.Clamp((Plot.Bottom-e.GetPosition(this).Y)/Plot.Height,0,1);
        mouse = point == 0 ? mouse with { Point1 = value } : mouse with { Point2 = value };
        onChanged(point,value);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        point = -1;
        ReleaseMouseCapture();
        e.Handled = true;
    }
}
