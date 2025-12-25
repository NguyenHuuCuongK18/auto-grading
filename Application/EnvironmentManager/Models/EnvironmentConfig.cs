using System.Collections.Generic;

namespace EnvironmentManager.Models
{
    /// <summary>
    /// Configuration for environment setup.
    /// Contains all the settings needed to set up Docker containers for grading.
    /// </summary>
    public class EnvironmentConfig
    {
        // Docker settings
        public string DockerNetwork { get; set; } = "ag-network";
        public string CodeImageName { get; set; } = "";
        public string DatabaseImageName { get; set; } = "mcr.microsoft.com/mssql/server:2022-latest";
        
        // Container names
        public string CodeContainerName { get; set; } = "";
        public string DatabaseContainerName { get; set; } = "";
        
        // Port settings
        public int CodeContainerInternalPort { get; set; } = 5000;
        public int CodeContainerHostPort { get; set; } = 5000;
        public int DatabaseContainerInternalPort { get; set; } = 1433;
        public int DatabaseContainerHostPort { get; set; } = 1433;
        
        // Database settings
        public string DatabaseName { get; set; } = "";
        public string DatabaseUsername { get; set; } = "sa";
        public string DatabasePassword { get; set; } = "YourStrong@Passw0rd";
        public string? SqlScriptPath { get; set; }
        
        // File paths
        public string? ServerDllPath { get; set; }
        public string? ClientDllPath { get; set; }
        
        // Additional settings
        public Dictionary<string, string> AdditionalConfigs { get; set; } = new();
    }
}
