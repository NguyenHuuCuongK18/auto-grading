using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SolutionGrader.UI.Models;
using SolutionGrader.UI.ViewModels;

namespace SolutionGrader.UI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// Auto Grading System - Main application window
    /// 
    /// Features:
    /// - Load student submissions from Submit folder (PaperNo/StudentCode/1/solution structure)
    /// - Configure client/server project names for DLL lookup
    /// - Filter by paper number for batch grading
    /// - Start/Pause/Resume grading operations
    /// - Real-time progress tracking and logging
    /// - Per-student logging with Log_StudentCode_Date folder format
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            
            // Handle window closing to properly dispose resources
            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Clean up resources
            if (DataContext is MainViewModel viewModel)
            {
                // Cancel any running operations
                // Dispose logging service if needed
            }
        }
    }

    /// <summary>
    /// Converts GradingStatus to a background color for display in the DataGrid.
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
}