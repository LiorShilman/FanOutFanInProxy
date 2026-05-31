using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TcpProxy.Dashboard
{
    public sealed class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b)
                ? Color.FromRgb(0x4C, 0xAF, 0x50)   // green
                : Color.FromRgb(0xF4, 0x43, 0x36);  // red

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
