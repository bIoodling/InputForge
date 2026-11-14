using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using InputForge.Core;

namespace InputForge.App;

internal sealed class CurveNames : IValueConverter
{
    public static DataTemplate Template
    {
        get
        {
            var visual = new FrameworkElementFactory(typeof(TextBlock));
            visual.SetBinding(TextBlock.TextProperty, new Binding { Converter = new CurveNames() });
            return new DataTemplate { VisualTree = visual };
        }
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ResponseCurve.Aggressive ? "Responsive" : value.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
