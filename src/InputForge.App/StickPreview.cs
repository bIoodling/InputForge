using System.Numerics;
using System.Windows;
using System.Windows.Media;

namespace InputForge.App;

internal sealed class StickPreview : FrameworkElement
{
    private Vector2 value;

    public void Update(Vector2 next)
    {
        if (value == next) return;
        value = next;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width < 16 || height < 16) return;
        var pen = new Pen(MainWindow.Brush("Border"), 1);
        context.DrawRoundedRectangle(MainWindow.Brush("Background"), pen, new Rect(.5,.5,width-1,height-1), 8, 8);
        context.DrawLine(pen, new Point(width / 2, 0), new Point(width / 2, height));
        context.DrawLine(pen, new Point(0, height / 2), new Point(width, height / 2));
        context.DrawEllipse(MainWindow.Brush("Accent"), null,
            new Point(width / 2 + value.X * (width / 2 - 7), height / 2 - value.Y * (height / 2 - 7)), 5, 5);
    }
}
