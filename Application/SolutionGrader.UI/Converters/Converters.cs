using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Converters
{
    /// <summary>
    /// Converts GradingStatus to a background color for display.
    /// </summary>
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is GradingStatus status)
            {
                return status switch
                {
                    GradingStatus.Not_Run => System.Windows.Media.Brushes.Transparent,
                    GradingStatus.InProgress => new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 243, 205)), // Light yellow
                    GradingStatus.Paused => new SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 236, 241)), // Light cyan
                    GradingStatus.Success => new SolidColorBrush(System.Windows.Media.Color.FromRgb(212, 237, 218)), // Light green
                    GradingStatus.Failed => new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 215, 218)), // Light red
                    GradingStatus.Disposed => new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 230, 230)), // Light gray
                    _ => System.Windows.Media.Brushes.Transparent
                };
            }
            return System.Windows.Media.Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts boolean to visibility.
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                bool invert = parameter?.ToString()?.ToLower() == "invert";
                return (boolValue ^ invert) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }
            return System.Windows.Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts progress percent to progress bar width.
    /// </summary>
    public class ProgressToWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int progress && parameter is double maxWidth)
            {
                return (progress / 100.0) * maxWidth;
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
