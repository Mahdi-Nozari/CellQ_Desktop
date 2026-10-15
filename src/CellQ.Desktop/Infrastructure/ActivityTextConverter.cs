using System.Globalization;
using System.Windows.Data;
using CellQ.Desktop.ViewModels;
using CellQ.Protocol;

namespace CellQ.Desktop.Infrastructure;

public sealed class ActivityTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DiagnosticMessage message ? DevicePresentation.Activity(message) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
