using System;
using System.IO;
using System.Windows;
using SolutionGrader.UI.Models;
using SolutionGrader.UI.Services;

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
        private readonly LoggingService _logger;
        private readonly TestKitDiscoveryService _testKitDiscovery;
        private readonly StudentDiscoveryService _studentDiscovery;

        public SetupWindow()
        {
            InitializeComponent();
            _configuration = new GradingConfiguration();
            
            // Initialize services for validation
            _logger = new LoggingService(Path.GetTempPath());
            _testKitDiscovery = new TestKitDiscoveryService(_logger);
            _studentDiscovery = new StudentDiscoveryService(_logger);

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
                
                // Load mapping from Mapping.xlsx
                LoadTestKitMapping(dialog.SelectedPath);
                
                ValidateConfiguration();
            }
        }

        /// <summary>
        /// Loads the paper-to-testkit mapping from Mapping.xlsx in the testkit folder.
        /// </summary>
        private void LoadTestKitMapping(string testKitFolder)
        {
            var mapping = _testKitDiscovery.LoadMappingFromExcel(testKitFolder);
            
            if (mapping.Count == 0)
            {
                txtValidation.Text = "Warning: No paper-to-testkit mapping found in Mapping.xlsx. Will try folder conventions (Q1, Q2...).";
            }
            else
            {
                _configuration.PaperToTestKitMapping = mapping;
                txtValidation.Text = $"Loaded {mapping.Count} paper mapping(s) from Mapping.xlsx.";
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

            // Validate configuration
            if (!ValidateConfiguration())
            {
                return;
            }
            
            // Validate mapping for all discovered papers
            if (!ValidateTestKitMapping())
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

        /// <summary>
        /// Validates testkit mappings and warns about unmapped papers.
        /// Allows proceeding with missing testkits - students for those papers will be skipped during grading.
        /// </summary>
        private bool ValidateTestKitMapping()
        {
            // Discover students to get all paper numbers
            var students = _studentDiscovery.DiscoverStudents(_configuration.SubmitFolderPath);
            
            if (students.Count == 0)
            {
                txtValidation.Text = "No student submissions found in the Submit folder.";
                return false;
            }

            // Get all unique paper numbers
            var paperNumbers = new System.Collections.Generic.HashSet<string>();
            foreach (var student in students)
            {
                paperNumbers.Add(student.PaperNo);
            }

            // Check each paper has a corresponding testkit
            var unmappedPapers = _testKitDiscovery.ValidateMappings(
                _configuration.TestKitFolderPath, 
                _configuration.PaperToTestKitMapping, 
                paperNumbers);

            if (unmappedPapers.Count > 0)
            {
                // Store unmapped papers in configuration so GradingWindow can skip them
                _configuration.UnmappedPapers = unmappedPapers.ToHashSet();
                
                // Show warning but allow proceeding - students for these papers will be skipped
                txtValidation.Text = $"Warning: No testkit for paper(s): {string.Join(", ", unmappedPapers)}. Students for these papers will be skipped during grading.";
                txtValidation.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 165, 0)); // Orange warning
            }
            else
            {
                _configuration.UnmappedPapers = new System.Collections.Generic.HashSet<string>();
            }

            return true;
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
