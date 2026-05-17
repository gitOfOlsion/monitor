using System.Globalization;
using System.Windows.Data;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Client.Wpf.Converters;

/// <summary>
/// Returns true if agent status is Pending (for enabling Approve button)
/// </summary>
public class IsPendingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is AgentStatus status && status == AgentStatus.Pending;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns true if agent status is NOT Approved (for enabling Delete button)
/// </summary>
public class IsNotApprovedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is AgentStatus status && status != AgentStatus.Approved;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Optional: Convert status enum to color for UI
/// </summary>
public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            AgentStatus.Approved => System.Windows.Media.Brushes.Green,
            AgentStatus.Pending => System.Windows.Media.Brushes.Orange,
            AgentStatus.Rejected => System.Windows.Media.Brushes.Red,
            AgentStatus.Offline => System.Windows.Media.Brushes.Gray,
            _ => System.Windows.Media.Brushes.Black
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}