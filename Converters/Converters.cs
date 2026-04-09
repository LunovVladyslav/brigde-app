using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GearBoardBridge.Converters;

/// <summary>Bool → Visibility (True = Visible, False = Collapsed)</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Bool → Visibility (True = Collapsed, False = Visible) — inverse</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Null → Collapsed, non-null → Visible</summary>
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Bool (isScanning) → button text</summary>
public class ScanButtonTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "Scanning..." : "Scan for GearBoard";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Bool (isActive) → amber (active step) or muted (inactive step)</summary>
public class StepActiveBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Active   = new(Color.FromRgb(0xE8, 0xA0, 0x20)); // amber
    private static readonly SolidColorBrush Inactive = new(Color.FromRgb(0x3A, 0x3A, 0x55)); // muted

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Active : Inactive;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Bool (available) → green/gray pill background</summary>
public class AvailabilityBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Available   = new(Color.FromRgb(0x2E, 0x7D, 0x32)); // green
    private static readonly SolidColorBrush Unavailable = new(Color.FromRgb(0x2D, 0x2D, 0x4A)); // #2D2D4A

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Available : Unavailable;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Hex color string → SolidColorBrush (MIDI type colors, status dot).</summary>
public class HexColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex)
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        return Brushes.White;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Multi-value: values[0] == values[1] (string equality) → Visible, else Collapsed.
/// Shows "● Connected" badge when device name matches ConnectedDeviceName.
/// </summary>
public class StringEqualityToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is string s1 && values[1] is string s2)
            return s1 == s2 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Multi-value: values[0] != values[1] (string inequality) → Visible, else Collapsed.
/// Shows "Connect" button when device name does NOT match ConnectedDeviceName.
/// </summary>
public class StringInequalityToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is string s1 && values[1] is string s2)
            return s1 != s2 ? Visibility.Visible : Visibility.Collapsed;
        // ConnectedDeviceName is null → nothing connected → always show Connect
        return Visibility.Visible;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
