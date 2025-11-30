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
            txtClientProjectName.IsEnabled = chkHasClient.IsChecked == true;
            _configuration.HasClient = chkHasClient.IsChecked == true;
            ValidateConfiguration();
        }

        private void ChkHasServer_CheckedChanged(object sender, RoutedEventArgs e)
        {
            txtServerProjectName.IsEnabled = chkHasServer.IsChecked == true;
            _configuration.HasServer = chkHasServer.IsChecked == true;
            ValidateConfiguration();
        }

        private void StartGrading_Click(object sender, RoutedEventArgs e)
        {
            // Update configuration with project names
            _configuration.ClientProjectName = txtClientProjectName.Text.Trim();
            _configuration.ServerProjectName = txtServerProjectName.Text.Trim();

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

            return true;
        }
    }
}
