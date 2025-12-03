using System;
using System.IO;
using System.Windows;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI
{
    /// <summary>
    /// Setup window for configuring grading parameters.
    /// 
    /// This window is displayed first and allows the user to:
    /// - Select the Submit folder containing student solutions
    /// - Select the Test Kit folder containing test cases
    /// - Select the save location for grading results
    /// - Configure client/server project names for DLL lookup
    /// 
    /// Port configurations are NOT set here - they are read from each test kit's
    /// Environment.xlsx file to ensure consistency with the test kit's expected
    /// network configuration.
    /// 
    /// After configuration is complete, clicking "Start Grading" opens the
    /// GradingWindow where the actual grading operations take place.
    /// </summary>
    public partial class SetupWindow : Window
    {
        private readonly GradingConfiguration _configuration;

        public SetupWindow()
        {
            InitializeComponent();
            _configuration = new GradingConfiguration();

            // Hook up events AFTER components are created to avoid early invocation
            chkHasClient.Checked += ChkHasClient_CheckedChanged;
            chkHasClient.Unchecked += ChkHasClient_CheckedChanged;
            chkHasServer.Checked += ChkHasServer_CheckedChanged;
            chkHasServer.Unchecked += ChkHasServer_CheckedChanged;

            // Sync initial state
            txtClientProjectName.IsEnabled = chkHasClient.IsChecked == true;
            txtServerProjectName.IsEnabled = chkHasServer.IsChecked == true;
            _configuration.HasClient = chkHasClient.IsChecked == true;
            _configuration.HasServer = chkHasServer.IsChecked == true;
        }

        /// <summary>
        /// Gets the configured grading configuration after setup.
        /// </summary>
        public GradingConfiguration Configuration => _configuration;

        /// <summary>
        /// Gets whether the setup was completed (user clicked Start Grading).
        /// </summary>
        public bool SetupCompleted { get; private set; }

        private void BrowseSubmitFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Submit Folder",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                txtSubmitFolder.Text = dialog.SelectedPath;
                _configuration.SubmitFolderPath = dialog.SelectedPath;
                ValidateConfiguration();
            }
        }

        private void BrowseTestKitFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Test Kit Folder",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                txtTestKitFolder.Text = dialog.SelectedPath;
                _configuration.TestKitFolderPath = dialog.SelectedPath;
                ValidateConfiguration();
            }
        }

        private void BrowseSaveFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Folder to Save Results",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                txtSaveFolder.Text = dialog.SelectedPath;
                _configuration.SaveResultFolderPath = dialog.SelectedPath;
                ValidateConfiguration();
            }
        }

        private void ChkHasClient_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (txtClientProjectName == null || chkHasClient == null) return; // safety
            txtClientProjectName.IsEnabled = chkHasClient.IsChecked == true;
            _configuration.HasClient = chkHasClient.IsChecked == true;
            ValidateConfiguration();
        }

        private void ChkHasServer_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (txtServerProjectName == null || chkHasServer == null) return; // safety
            txtServerProjectName.IsEnabled = chkHasServer.IsChecked == true;
            _configuration.HasServer = chkHasServer.IsChecked == true;
            ValidateConfiguration();
        }

        private void StartGrading_Click(object sender, RoutedEventArgs e)
        {
            // Update configuration with project names
            _configuration.ClientProjectName = txtClientProjectName.Text.Trim();
            _configuration.ServerProjectName = txtServerProjectName.Text.Trim();
            
            // Update configuration with parallel grading settings
            if (int.TryParse(txtMaxParallelStudents.Text.Trim(), out int maxParallel))
            {
                _configuration.MaxParallelStudents = Math.Max(1, maxParallel);
            }
            else
            {
                _configuration.MaxParallelStudents = 1;
            }
            
            if (int.TryParse(txtStartIndex.Text.Trim(), out int startIndex))
            {
                _configuration.StartIndex = Math.Max(0, startIndex);
            }
            else
            {
                _configuration.StartIndex = 0;
            }
            
            if (int.TryParse(txtEndIndex.Text.Trim(), out int endIndex))
            {
                _configuration.EndIndex = endIndex;
            }
            else
            {
                _configuration.EndIndex = -1;
            }

            // Validate configuration
            if (!ValidateConfiguration())
            {
                return;
            }

            // Create save folder if it doesn't exist
            if (!Directory.Exists(_configuration.SaveResultFolderPath))
            {
                try
                {
                    Directory.CreateDirectory(_configuration.SaveResultFolderPath);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Failed to create save folder: {ex.Message}", 
                                    "Error", 
                                    MessageBoxButton.OK, 
                                    MessageBoxImage.Error);
                    return;
                }
            }

            SetupCompleted = true;
            
            // Open the grading window
            var gradingWindow = new GradingWindow(_configuration);
            gradingWindow.Show();
            
            // Close this setup window
            this.Close();
        }

        private bool ValidateConfiguration()
        {
            txtValidation.Text = "";

            // Check Submit folder
            if (string.IsNullOrWhiteSpace(_configuration.SubmitFolderPath))
            {
                txtValidation.Text = "Please select a Submit folder.";
                return false;
            }

            if (!Directory.Exists(_configuration.SubmitFolderPath))
            {
                txtValidation.Text = "Submit folder does not exist.";
                return false;
            }

            // Check Test Kit folder
            if (string.IsNullOrWhiteSpace(_configuration.TestKitFolderPath))
            {
                txtValidation.Text = "Please select a Test Kit folder.";
                return false;
            }

            if (!Directory.Exists(_configuration.TestKitFolderPath))
            {
                txtValidation.Text = "Test Kit folder does not exist.";
                return false;
            }

            // Check Save folder
            if (string.IsNullOrWhiteSpace(_configuration.SaveResultFolderPath))
            {
                txtValidation.Text = "Please select a folder to save results.";
                return false;
            }

            // Check project names if client/server is enabled
            if (_configuration.HasClient && string.IsNullOrWhiteSpace(txtClientProjectName.Text))
            {
                txtValidation.Text = "Please enter a Client Project Name.";
                return false;
            }

            if (_configuration.HasServer && string.IsNullOrWhiteSpace(txtServerProjectName.Text))
            {
                txtValidation.Text = "Please enter a Server Project Name.";
                return false;
            }
            
            // Validate parallel grading settings
            if (!int.TryParse(txtMaxParallelStudents.Text.Trim(), out int maxParallel) || maxParallel < 1)
            {
                txtValidation.Text = "Parallel Students must be a positive integer (minimum 1).";
                return false;
            }
            
            if (!int.TryParse(txtStartIndex.Text.Trim(), out int startIndex) || startIndex < 0)
            {
                txtValidation.Text = "Start Index must be a non-negative integer (0 or greater).";
                return false;
            }
            
            if (!int.TryParse(txtEndIndex.Text.Trim(), out int endIndex) || (endIndex < -1))
            {
                txtValidation.Text = "End Index must be -1 (for all) or a non-negative integer.";
                return false;
            }
            
            if (endIndex != -1 && endIndex < startIndex)
            {
                txtValidation.Text = "End Index must be greater than or equal to Start Index.";
                return false;
            }

            return true;
        }
    }
}
