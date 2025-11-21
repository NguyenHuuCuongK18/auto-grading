using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Domain.Entities.Main;
using Domain.Entities.Docker.DockerSupporter.Entity;

namespace FileHandler
{
    public class EnvFileHandler
    {
        /// <summary>
        /// Load environment configuration from an Excel file (environment.xlsx)
        /// Expected sheets:
        /// - Config: two columns Key/Value
        /// - Action: Category | Action | Description (optional)
        /// - Run: Step | Keyword action
        /// </summary>
        /// <param name="excelPath">Full path to environment.xlsx</param>
        /// <returns>Domain Environment object with Configs and combined Steps</returns>
        public static Domain.Entities.Main.Environment LoadEnvironment(string excelPath)
        {
            if (string.IsNullOrWhiteSpace(excelPath)) throw new ArgumentNullException(nameof(excelPath));
            if (!File.Exists(excelPath)) throw new FileNotFoundException("Environment excel not found", excelPath);

            var env = new Domain.Entities.Main.Environment
            {
                Configs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Steps = new List<string>()
            };

            using var wb = new XLWorkbook(excelPath);

            // Read Config sheet (Key | Value)
            var wsConfig = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Config", StringComparison.OrdinalIgnoreCase))
                           ?? wb.Worksheet(1);
            int startRow = 1;
            if (wsConfig.Cell(1, 1).GetString().Trim().Equals("Key", StringComparison.OrdinalIgnoreCase)) startRow = 2;
            for (int r = startRow; r <= wsConfig.RowCount(); r++)
            {
                var key = wsConfig.Cell(r, 1).GetString().Trim();
                var val = wsConfig.Cell(r, 2).GetString().Trim();
                if (string.IsNullOrEmpty(key)) continue;
                env.Configs[key] = val ?? string.Empty;
            }

            // Read Run sheet (Step | Keyword action)
            var wsRun = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Run", StringComparison.OrdinalIgnoreCase));
            if (wsRun != null)
            {
                int headerRow = 1;
                var c1 = wsRun.Cell(1, 1).GetString().Trim();
                var c2 = wsRun.Cell(1, 2).GetString().Trim();
                if (c1.Equals("Step", StringComparison.OrdinalIgnoreCase) ||
                    c2.Equals("Keyword action", StringComparison.OrdinalIgnoreCase))
                {
                    headerRow = 2;
                }

                for (int r = headerRow; r <= wsRun.RowCount(); r++)
                {
                    var keyword = wsRun.Cell(r, 2).GetString().Trim();
                    if (!string.IsNullOrWhiteSpace(keyword))
                    {
                        env.Steps.Add(keyword);
                    }
                }
            }

            return env;
        }

        /// <summary>
        /// Create a DockerBase for the code container from environment Configs
        /// </summary>
        public static DockerBase CreateCodeDocker(Domain.Entities.Main.Environment environment)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            var c = environment.Configs;

            return new DockerBase
            {
                ImageName = Get(c, "Code_Image_Name"),
                ContainerName = Get(c, "Code_Container_Name"),
                ContainerPort = ToInt(Get(c, "Code_Container_Internal_Port"), 0),
                HostPort = ToInt(Get(c, "Code_Container_Host_Port"), 0),
                DockerNetwork = Get(c, "Docker_Network"),
                EnvironmentVariables = BuildCodeEnvVars(c),
                DockerPath = string.Empty,
                DockerVolume = string.Empty,
                ContainerId = string.Empty,
                CaptureFilePath = string.Empty
            };
        }

        /// <summary>
        /// Create a DockerBase for the database container from environment Configs
        /// </summary>
        public static DockerBase CreateDatabaseDocker(Domain.Entities.Main.Environment environment)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            var c = environment.Configs;

            // Common defaults for SQL Server on Linux
            var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ACCEPT_EULA"] = "Y"
            };
            var pwd = Get(c, "Database_Password");
            if (!string.IsNullOrWhiteSpace(pwd)) envVars["SA_PASSWORD"] = pwd;

            return new DockerBase
            {
                ImageName = Get(c, "Database_Image_Name"),
                ContainerName = Get(c, "Database_Container_Name"),
                ContainerPort = ToInt(Get(c, "Database_Container_Internal_Port"), 0),
                HostPort = ToInt(Get(c, "Database_Container_Host_Port"), 0),
                DockerNetwork = Get(c, "Docker_Network"),
                EnvironmentVariables = envVars,
                DockerPath = string.Empty,
                DockerVolume = string.Empty,
                ContainerId = string.Empty,
                CaptureFilePath = string.Empty
            };
        }

        private static string Get(Dictionary<string, string> cfg, string key, string def = "")
            => cfg != null && cfg.TryGetValue(key, out var v) ? v ?? def : def;

        private static int ToInt(string s, int def = 0)
            => int.TryParse(s, out var i) ? i : def;

        private static Dictionary<string, string> BuildCodeEnvVars(Dictionary<string, string> c)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Optional app settings to pass via env
            var appType = Get(c, "App_Type");
            if (!string.IsNullOrWhiteSpace(appType)) dict["APP_TYPE"] = appType;

            var runtimeFolder = Get(c, "Runtimes_Folder");
            if (!string.IsNullOrWhiteSpace(runtimeFolder)) dict["RUNTIMES_FOLDER"] = runtimeFolder;

            // Database connection hints (for appsettings generation inside container if needed)
            var dbUser = Get(c, "Database_Username");
            var dbPwd = Get(c, "Database_Password");
            var dbName = Get(c, "Default_Database_Name");
            if (!string.IsNullOrWhiteSpace(dbUser)) dict["DB_USER"] = dbUser;
            if (!string.IsNullOrWhiteSpace(dbPwd)) dict["DB_PASSWORD"] = dbPwd;
            if (!string.IsNullOrWhiteSpace(dbName)) dict["DB_NAME"] = dbName;

            return dict;
        }
    }
}
