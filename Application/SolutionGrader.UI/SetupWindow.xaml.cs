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
    /// - Validate Docker images (ensures correct image is built from Dockerfile.unified)
    /// - Select the Submit folder containing student solutions
    /// - Select the Test Kit folder containing test cases
    /// - Select the save location for grading results
    /// - Configure project names and their roles (client/server) for DLL lookup
    /// 
    /// Docker Validation:
    /// - CRITICAL: Validates that the Docker image has the correct entrypoint
    /// - Detects when users have built with old Dockerfile instead of Dockerfile.unified
    /// - Prevents the error: "exec /scripts/unified-entrypoint.sh: no such file or directory"
    /// 
    /// Project Configuration:
    /// - Users can specify 1 or 2 project names (e.g., Q1, Q2, Project11, Project12)
    /// - If only 1 project is specified, it's assumed to be both client and server (or the only component)
    /// - If 2 projects are specified, users must use radio buttons to indicate which is client and which is server
    /// - This flexible structure handles various student submission formats
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
        private readonly DockerImageValidator _dockerValidator;
        private bool _dockerValidationPassed = false;

        public SetupWindow()
        {
            InitializeComponent();
            _configuration = new GradingConfiguration();
            _dockerValidator = new DockerImageValidator();

            // Initialize with default values - but roles are now flexible
            // Default to Project 1 being server (typically Q1 is server in many scenarios)
            // and Project 2 being client (typically Q2 is client)
            rbProject1Server.IsChecked = true;
            rbProject2Client.IsChecked = true;
            
            // Initially hide role toggles until projects are specified
            UpdateRoleToggleVisibility();
            
            // Validate Docker images on startup
            _ = ValidateDockerImagesAsync();
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

        /// <summary>
        /// Handles text changes in project name fields to update role toggle visibility.
        /// Role toggles are only shown when both projects are specified.
        /// </summary>
        private void ProjectName_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateRoleToggleVisibility();
            ValidateConfiguration();
        }

        /// <summary>
        /// Updates the visibility of role toggle buttons based on project name inputs.
        /// - If both project names are filled: Show both role toggle panels (user must specify which is client/server)
        /// - If only one project is filled: Hide both role toggle panels (that project serves both roles)
        /// - If no projects are filled: Hide both role toggle panels
        /// </summary>
        private void UpdateRoleToggleVisibility()
        {
            if (txtProject1Name == null || txtProject2Name == null || 
                pnlProject1Role == null || pnlProject2Role == null)
                return; // Safety check during initialization

            bool hasProject1 = !string.IsNullOrWhiteSpace(txtProject1Name.Text);
            bool hasProject2 = !string.IsNullOrWhiteSpace(txtProject2Name.Text);
            
            // Show role toggles only when BOTH projects are specified
            bool showToggles = hasProject1 && hasProject2;
            pnlProject1Role.Visibility = showToggles ? Visibility.Visible : Visibility.Collapsed;
            pnlProject2Role.Visibility = showToggles ? Visibility.Visible : Visibility.Collapsed;
        }

        private void StartGrading_Click(object sender, RoutedEventArgs e)
        {
            // Update configuration with project names and roles
            // CRITICAL FIX: Set ALL properties BEFORE the automatic UpdateLegacyProperties() mapping
            // to ensure the mapping has complete information
            _configuration.Project1Name = txtProject1Name.Text.Trim();
            _configuration.Project2Name = txtProject2Name.Text.Trim();
            
            // Update role flags based on radio buttons
            _configuration.Project1IsClient = rbProject1Client.IsChecked == true;
            _configuration.Project2IsClient = rbProject2Client.IsChecked == true;
            
            // CRITICAL FIX: Explicitly map to legacy properties for backward compatibility
            // This ensures LoadStudents() in GradingWindow can find student DLLs correctly
            bool hasProject1 = !string.IsNullOrWhiteSpace(_configuration.Project1Name);
            bool hasProject2 = !string.IsNullOrWhiteSpace(_configuration.Project2Name);
            
            if (hasProject1 && hasProject2)
            {
                // Both projects specified - map based on roles
                _configuration.ClientProjectName = _configuration.Project1IsClient 
                    ? _configuration.Project1Name 
                    : _configuration.Project2Name;
                _configuration.ServerProjectName = _configuration.Project1IsClient 
                    ? _configuration.Project2Name 
                    : _configuration.Project1Name;
            }
            else if (hasProject1)
            {
                // Only project1 specified - it handles both roles
                _configuration.ClientProjectName = _configuration.Project1Name;
                _configuration.ServerProjectName = _configuration.Project1Name;
            }
            else if (hasProject2)
            {
                // Only project2 specified - it handles both roles
                _configuration.ClientProjectName = _configuration.Project2Name;
                _configuration.ServerProjectName = _configuration.Project2Name;
            }
            // else: No projects specified - keep defaults (will be caught by validation)
            
            // Determine HasClient and HasServer flags based on project configuration
            if (hasProject1 && hasProject2)
            {
                // Both projects specified - determine which roles are present
                // Validation ensures they have different roles (one client, one server)
                
                // HasClient is true if at least one project is marked as client
                _configuration.HasClient = _configuration.Project1IsClient || _configuration.Project2IsClient;
                
                // HasServer is true if at least one project is marked as server (i.e., NOT client)
                // If Project1 is server (not client): !Project1IsClient = true
                // If Project2 is server (not client): !Project2IsClient = true
                // HasServer = (!Project1IsClient) OR (!Project2IsClient)
                _configuration.HasServer = !_configuration.Project1IsClient || !_configuration.Project2IsClient;
            }
            else if (hasProject1 || hasProject2)
            {
                // Only one project specified - it serves both roles (or is the only component)
                _configuration.HasClient = true;
                _configuration.HasServer = true;
            }
            else
            {
                // No projects specified - this should be caught by validation
                _configuration.HasClient = false;
                _configuration.HasServer = false;
            }
            
            // Log the final configuration for debugging
            System.Diagnostics.Debug.WriteLine($"[SetupWindow] Final configuration:");
            System.Diagnostics.Debug.WriteLine($"  Project1: {_configuration.Project1Name} (IsClient: {_configuration.Project1IsClient})");
            System.Diagnostics.Debug.WriteLine($"  Project2: {_configuration.Project2Name} (IsClient: {_configuration.Project2IsClient})");
            System.Diagnostics.Debug.WriteLine($"  ClientProjectName: {_configuration.ClientProjectName}");
            System.Diagnostics.Debug.WriteLine($"  ServerProjectName: {_configuration.ServerProjectName}");
            System.Diagnostics.Debug.WriteLine($"  HasClient: {_configuration.HasClient}, HasServer: {_configuration.HasServer}");

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

            // Check project names
            bool hasProject1 = !string.IsNullOrWhiteSpace(txtProject1Name.Text);
            bool hasProject2 = !string.IsNullOrWhiteSpace(txtProject2Name.Text);

            if (!hasProject1 && !hasProject2)
            {
                txtValidation.Text = "Please enter at least one project name.";
                return false;
            }

            // If both projects are specified, validate that roles are properly configured
            if (hasProject1 && hasProject2)
            {
                bool project1IsClient = rbProject1Client.IsChecked == true;
                bool project2IsClient = rbProject2Client.IsChecked == true;
                
                // Both projects cannot have the same role
                if (project1IsClient == project2IsClient)
                {
                    txtValidation.Text = "When two projects are specified, one must be client and one must be server.";
                    return false;
                }
            }

            return true;
        }
    }
}
