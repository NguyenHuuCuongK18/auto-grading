namespace SolutionGrader.Core.Services;

using ClosedXML.Excel;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using System.Globalization;
using System.IO;

public sealed class ExcelSuiteLoader : ITestSuiteLoader
{
    public SuiteDefinition Load(string suitePathOrHeaderXlsx, bool useInnerTestCaseEnvironment = false)
    {
        var headerPath = ResolveHeaderPath(suitePathOrHeaderXlsx);
        var suiteRoot = Path.GetDirectoryName(headerPath)!;
        
        // Read protocol from Config sheet in header.xlsx
        var protocol = ReadProtocolFromHeader(headerPath);
        
        // Read database config from header.xlsx (legacy support)
        var dbConfig = ReadDatabaseConfigFromHeader(headerPath);
        
        // Read marks from QuestionMark sheet in header.xlsx
        var marks = ReadMarksFromHeader(headerPath);
        
        // Read datetime format from DataPattern sheet in header.xlsx
        var dateTimeFormat = ReadDateTimeFormatFromHeader(headerPath);
        
        // CRITICAL: Read Grade_Content from outer Header.xlsx
        // This determines whether students provide Server or Client
        var suiteGradeContent = ReadGradeContentFromHeader(headerPath);
        // NOTE: Logging removed - no console output in library code
        // UI and CLI handle their own logging through OnProgress callbacks
        
        // Read environment configuration from outermost environment.xlsx
        var envConfig = ReadEnvironmentConfig(suiteRoot);
        // NOTE: Logging removed - no console output in library code
        // UI and CLI handle their own logging through OnProgress callbacks
        
        // Build test cases from directories
        var cases = BuildCasesFromDirectory(suiteRoot, marks, envConfig, useInnerTestCaseEnvironment, suiteGradeContent);
        
        return new SuiteDefinition
        {
            HeaderPath = headerPath,
            Protocol = protocol,
            DatabaseConfig = dbConfig,
            Environment = envConfig,
            DateTimeFormat = dateTimeFormat,
            Cases = cases
        };
    }

    private static string ResolveHeaderPath(string input)
    {
        if (File.Exists(input) && Path.GetFileName(input).Equals(FileKeywords.FileName_Header, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(input);

        if (Directory.Exists(input))
        {
            // Try both Header.xlsx and header.xlsx (case-insensitive search)
            var candidate = Path.Combine(input, FileKeywords.FileName_Header);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            
            candidate = Path.Combine(input, "header.xlsx");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException("Could not find Header.xlsx from: " + input);
    }

    private static string ReadProtocolFromHeader(string headerPath)
    {
        try
        {
            using var wb = new XLWorkbook(headerPath);
            // NEW: Look for Config sheet first (new format)
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Config", StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Header", StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheet(1);

            // Look for Protocol in key-value pairs
            int startRow = 1;
            var firstRowCol1 = ws.Cell(1, 1).GetString().Trim();
            if (firstRowCol1.Equals("Key", StringComparison.OrdinalIgnoreCase))
            {
                startRow = 2; // Skip header row
            }

            // First pass: look for "Protocol" key (higher priority)
            for (int r = startRow; r <= Math.Min(50, ws.RowCount()); r++)
            {
                var key = ws.Cell(r, 1).GetString().Trim();
                if (key.Equals("Protocol", StringComparison.OrdinalIgnoreCase))
                {
                    var val = ws.Cell(r, 2).GetString().Trim();
                    if (!string.IsNullOrEmpty(val)) return val.ToUpperInvariant();
                }
            }
            
            // Second pass: fallback to "Type" key for backward compatibility
            for (int r = startRow; r <= Math.Min(50, ws.RowCount()); r++)
            {
                var key = ws.Cell(r, 1).GetString().Trim();
                if (key.Equals("Type", StringComparison.OrdinalIgnoreCase))
                {
                    var val = ws.Cell(r, 2).GetString().Trim();
                    if (!string.IsNullOrEmpty(val)) return val.ToUpperInvariant();
                }
            }
        }
        catch { }

        return "HTTP"; // default
    }

    private static Domain.Models.DatabaseConfiguration? ReadDatabaseConfigFromHeader(string headerPath)
    {
        try
        {
            using var wb = new XLWorkbook(headerPath);
            // Look for Config worksheet first, then Header, then fall back to first worksheet
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Config", StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Header", StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheet(1);

            var config = new Domain.Models.DatabaseConfiguration();
            bool foundAnyConfig = false;

            // Read key-value pairs from Header.xlsx (skip row 1 if it's a header row)
            int startRow = 1;
            // Check if row 1 looks like a header
            var firstRowCol1 = ws.Cell(1, 1).GetString().Trim();
            if (firstRowCol1.Equals("Key", StringComparison.OrdinalIgnoreCase))
            {
                startRow = 2; // Skip header row
            }

            for (int r = startRow; r <= Math.Min(50, ws.RowCount()); r++)
            {
                var key = ws.Cell(r, 1).GetString().Trim();
                var value = ws.Cell(r, 2).GetString().Trim();

                if (string.IsNullOrEmpty(key)) continue;

                if (key.Equals("Type", StringComparison.OrdinalIgnoreCase))
                {
                    config.Type = value.ToUpperInvariant();
                    foundAnyConfig = true;
                }
                else if (key.Equals("Sql Server", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
                {
                    config.SqlServer = value;
                    foundAnyConfig = true;
                }
                else if (key.Equals("Database", StringComparison.OrdinalIgnoreCase))
                {
                    config.Database = value;
                    foundAnyConfig = true;
                }
                else if (key.Equals("Username", StringComparison.OrdinalIgnoreCase))
                {
                    config.Username = value;
                    foundAnyConfig = true;
                }
                else if (key.Equals("Password", StringComparison.OrdinalIgnoreCase))
                {
                    config.Password = value;
                    foundAnyConfig = true;
                }
            }

            return foundAnyConfig ? config : null;
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_DATABASE_CONFIG} {string.Format(LoggingKeywords.MSG_DB_CONFIG_ERROR, ex.Message)}");
            return null;
        }
    }

    private static Dictionary<string, double> ReadMarksFromHeader(string headerPath)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var wb = new XLWorkbook(headerPath);
            // NEW: Look for QuestionMark sheet (new format)
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("QuestionMark", StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Header", StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheet(1);

            // Find the row that contains "Cases" and "Mark" headers (new format)
            // or "TestCase" and "Mark" headers (old format)
            int headerRow = -1, tcCol = -1, markCol = -1;
            for (int r = 1; r <= Math.Min(100, ws.RowCount()); r++)
            {
                var row = ws.Row(r);
                var cells = row.CellsUsed().ToList();
                if (cells.Count == 0) continue;

                for (int c = 1; c <= Math.Min(50, ws.ColumnCount()); c++)
                {
                    var text = ws.Cell(r, c).GetString().Trim();
                    if (text.Equals("TestCase", StringComparison.OrdinalIgnoreCase) ||
                        text.Equals("Cases", StringComparison.OrdinalIgnoreCase)) tcCol = c;
                    if (text.Equals("Mark", StringComparison.OrdinalIgnoreCase)) markCol = c;
                }

                if (tcCol > 0 && markCol > 0) { headerRow = r; break; }
                tcCol = markCol = -1;
            }

            if (headerRow < 0) return result; // none found; marks default to 0

            // Read until a blank TestCase cell
            for (int r = headerRow + 1; r <= ws.RowCount(); r++)
            {
                var tc = ws.Cell(r, tcCol).GetString().Trim();
                if (string.IsNullOrEmpty(tc)) break;

                var markStr = ws.Cell(r, markCol).GetString().Trim();
                if (!double.TryParse(markStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var mark))
                    mark = 0;

                result[tc] = mark;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} Warning: Could not read marks from header: {ex.Message}");
        }

        return result;
    }

    private static string? ReadGradeContentFromHeader(string headerPath)
    {
        try
        {
            using var wb = new XLWorkbook(headerPath);
            // Look in the first sheet (Config/Header sheet)
            var ws = wb.Worksheets.FirstOrDefault();
            if (ws == null) return null;

            // Look for Grade_Content key in key-value pairs
            for (int r = 1; r <= Math.Min(50, ws.RowCount()); r++)
            {
                var key = ws.Cell(r, 1).GetString().Trim();
                if (key.Equals("Grade_Content", StringComparison.OrdinalIgnoreCase))
                {
                    var value = ws.Cell(r, 2).GetString().Trim();
                    return string.IsNullOrEmpty(value) ? null : value;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not read Grade_Content from header: {ex.Message}");
            return null;
        }
    }

    private static string? ReadDateTimeFormatFromHeader(string headerPath)
    {
        try
        {
            using var wb = new XLWorkbook(headerPath);
            // Look for DataPattern sheet
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("DataPattern", StringComparison.OrdinalIgnoreCase));
            if (ws == null) return null;

            // Find the row with "Data Type" and "Pattern" headers
            int headerRow = -1, typeCol = -1, patternCol = -1;
            for (int r = 1; r <= Math.Min(10, ws.RowCount()); r++)
            {
                for (int c = 1; c <= Math.Min(10, ws.ColumnCount()); c++)
                {
                    var text = ws.Cell(r, c).GetString().Trim();
                    if (text.Equals("Data Type", StringComparison.OrdinalIgnoreCase) ||
                        text.Equals("DataType", StringComparison.OrdinalIgnoreCase)) typeCol = c;
                    if (text.Equals("Pattern", StringComparison.OrdinalIgnoreCase)) patternCol = c;
                }

                if (typeCol > 0 && patternCol > 0) { headerRow = r; break; }
                typeCol = patternCol = -1;
            }

            if (headerRow < 0) return null;

            // Find the DateTime row
            for (int r = headerRow + 1; r <= Math.Min(50, ws.RowCount()); r++)
            {
                var dataType = ws.Cell(r, typeCol).GetString().Trim();
                if (dataType.Equals("DateTime", StringComparison.OrdinalIgnoreCase) ||
                    dataType.Equals("Date Time", StringComparison.OrdinalIgnoreCase))
                {
                    var pattern = ws.Cell(r, patternCol).GetString().Trim();
                    if (!string.IsNullOrWhiteSpace(pattern))
                    {
                        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} DateTime format from header: {pattern}");
                        return pattern;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} Warning: Could not read datetime format from header: {ex.Message}");
        }

        return null;
    }

    private static EnvironmentConfiguration? ReadEnvironmentConfig(string suiteRoot)
    {
        // Try both lowercase and uppercase file names (case sensitivity varies by OS)
        var envPath = Path.Combine(suiteRoot, FileKeywords.FileName_Environment);
        if (!File.Exists(envPath))
        {
            // Try uppercase "Environment.xlsx" as fallback
            envPath = Path.Combine(suiteRoot, "Environment.xlsx");
            if (!File.Exists(envPath)) return null;
        }

        try
        {
            using var wb = new XLWorkbook(envPath);
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Config", StringComparison.OrdinalIgnoreCase));
            if (ws == null) return null;

            var config = new EnvironmentConfiguration();
            
            // Read key-value pairs
            int startRow = 1;
            var firstRowCol1 = ws.Cell(1, 1).GetString().Trim();
            if (firstRowCol1.Equals("Key", StringComparison.OrdinalIgnoreCase))
            {
                startRow = 2; // Skip header row
            }

            for (int r = startRow; r <= Math.Min(100, ws.RowCount()); r++)
            {
                var key = ws.Cell(r, 1).GetString().Trim();
                var value = ws.Cell(r, 2).GetString().Trim();

                if (string.IsNullOrEmpty(key)) continue;
                
                // Debug: Log all keys and values being parsed
                if (key.Contains("Database", StringComparison.OrdinalIgnoreCase) || 
                    key.Contains("Port", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[Environment] Parsing: {key} = {value}");
                }

                // MonitorPort: Legacy configuration from environment.xlsx
                // NOTE: GraderPort from GradingConfig is now the primary port configuration
                // MonitorPort in environment.xlsx is kept for backward compatibility only
                if (key.Equals("MonitorPort", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Monitor_Port", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var port)) 
                    {
                        config.MonitorPort = port;
                        Console.WriteLine($"[Environment] MonitorPort set to {port} (note: GraderPort in GradingConfig takes precedence)");
                    }
                }
                // REMOVED: Legacy middleware port config - no longer used (kept for backward compatibility)
                #pragma warning disable CS0618 // Type or member is obsolete
                else if (key.Equals("Port_DEPRECATED_Middleware", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var port)) config.MonitorPort = port;
                }
                else if (key.Equals("Port Server", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var port)) config.ServerPort = port;
                }
                #pragma warning restore CS0618
                else if (key.Equals("Default_Database_File_Path", StringComparison.OrdinalIgnoreCase))
                {
                    config.DatabaseFilePath = value;
                }
                else if (key.Equals("Default_Database_Name", StringComparison.OrdinalIgnoreCase))
                {
                    config.DatabaseName = value;
                }
                else if (key.Equals("Database_Username", StringComparison.OrdinalIgnoreCase))
                {
                    config.DatabaseUsername = value;
                }
                else if (key.Equals("Database_Password", StringComparison.OrdinalIgnoreCase))
                {
                    config.DatabasePassword = value;
                }
                else if (key.Equals("Database_Container_Host_Port", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var port))
                    {
                        config.DatabaseHostPort = port;
                        Console.WriteLine($"[Environment] DatabaseHostPort set to {port}");
                    }
                }
                else if (key.Equals("Database_Container_Internal_Port", StringComparison.OrdinalIgnoreCase))
                {
                    // Just log, we use the host port for connection
                    Console.WriteLine($"[Environment] DatabaseContainerInternalPort (info only): {value}");
                }
            }

            // Look for Given server/client paths in Meta directory
            var metaDir = Path.Combine(suiteRoot, "Meta", "Given");
            if (Directory.Exists(metaDir))
            {
                // Try standard naming first (Server/Client)
                var serverDir = Path.Combine(metaDir, FileKeywords.Pattern_ServerName);
                var clientDir = Path.Combine(metaDir, FileKeywords.Pattern_ClientName);
                
                if (Directory.Exists(serverDir))
                {
                    var serverExe = Directory.GetFiles(serverDir, "*.exe").FirstOrDefault();
                    if (serverExe != null) config.GivenServerPath = serverExe;
                }
                
                if (Directory.Exists(clientDir))
                {
                    var clientExe = Directory.GetFiles(clientDir, "*.exe").FirstOrDefault();
                    if (clientExe != null) config.GivenClientPath = clientExe;
                }
                
                // If not found, search all subdirectories for executables
                if (string.IsNullOrEmpty(config.GivenServerPath) || string.IsNullOrEmpty(config.GivenClientPath))
                {
                    var allExes = Directory.GetFiles(metaDir, "*.exe", SearchOption.AllDirectories).ToList();
                    
                    // Heuristic: Look for common server/client naming patterns
                    if (string.IsNullOrEmpty(config.GivenServerPath))
                    {
                        // Look for executables with "Server", "Project11", or in a Server-like directory
                        var serverExe = allExes.FirstOrDefault(e => 
                            Path.GetFileNameWithoutExtension(e).Contains(FileKeywords.Pattern_ServerName, StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileNameWithoutExtension(e).Contains("Project11", StringComparison.OrdinalIgnoreCase) ||
                            e.Contains(Path.DirectorySeparatorChar + FileKeywords.Pattern_ServerName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        );
                        if (serverExe != null) config.GivenServerPath = serverExe;
                    }
                    
                    if (string.IsNullOrEmpty(config.GivenClientPath))
                    {
                        // Look for executables with "Client", "Project12", or in a Client-like directory
                        var clientExe = allExes.FirstOrDefault(e => 
                            Path.GetFileNameWithoutExtension(e).Contains(FileKeywords.Pattern_ClientName, StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileNameWithoutExtension(e).Contains("Project12", StringComparison.OrdinalIgnoreCase) ||
                            e.Contains(Path.DirectorySeparatorChar + FileKeywords.Pattern_ClientName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        );
                        if (clientExe != null) config.GivenClientPath = clientExe;
                    }
                }
            }

            return config;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} Warning: Could not read environment config: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<TestCaseDefinition> BuildCasesFromDirectory(string root, Dictionary<string, double> marks, EnvironmentConfiguration? suiteEnv, bool useInnerTestCaseEnvironment, string? suiteGradeContent)
    {
        var list = new List<TestCaseDefinition>();

        foreach (var dir in Directory.EnumerateDirectories(root)
                     .Where(p => {
                         var name = Path.GetFileName(p);
                         return !name.Equals("mismatches", StringComparison.OrdinalIgnoreCase) &&
                                !name.Equals("Meta", StringComparison.OrdinalIgnoreCase);
                     }))
        {
            var name = Path.GetFileName(dir);
            
            // Check for Detail.xlsx (case-insensitive)
            var detail = Path.Combine(dir, FileKeywords.FileName_Detail);
            if (!File.Exists(detail))
            {
                detail = Path.Combine(dir, "detail.xlsx");
                if (!File.Exists(detail)) continue;
            }

            marks.TryGetValue(name, out var mark);
            
            // CRITICAL: Read Grade_Content with fallback hierarchy
            // 1. Per-test-case Header.xlsx (if exists, overrides suite level)
            // 2. Suite-level Header.xlsx (passed from outer context)
            var tcHeaderPath = Path.Combine(dir, "header.xlsx");
            string? gradeContent = suiteGradeContent; // Default to suite level
            EnvironmentConfiguration? tcEnv = null;
            
            if (File.Exists(tcHeaderPath))
            {
                try
                {
                    using var wb = new XLWorkbook(tcHeaderPath);
                    var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Testcase_Property", StringComparison.OrdinalIgnoreCase));
                    if (ws != null)
                    {
                        // Look for Grade_Content (overrides suite level if found)
                        for (int r = 1; r <= Math.Min(20, ws.RowCount()); r++)
                        {
                            var key = ws.Cell(r, 1).GetString().Trim();
                            if (key.Equals("Grade_Content", StringComparison.OrdinalIgnoreCase))
                            {
                                var tcGradeContent = ws.Cell(r, 2).GetString().Trim();
                                if (!string.IsNullOrEmpty(tcGradeContent))
                                {
                                    gradeContent = tcGradeContent;
                                    Console.WriteLine($"[TestCase {name}] Grade_Content override from test case header: {gradeContent}");
                                }
                                break;
                            }
                        }
                    }
                }
                catch { }
                
                // Read test case specific environment.xlsx only if flag is enabled
                if (useInnerTestCaseEnvironment)
                {
                    var tcEnvPath = Path.Combine(dir, FileKeywords.FileName_Environment);
                    if (File.Exists(tcEnvPath))
                    {
                        tcEnv = ReadTestCaseEnvironment(tcEnvPath, suiteEnv);
                        Console.WriteLine($"[Suite] Using inner test case environment for {name}: {tcEnvPath}");
                    }
                }
            }
            
            list.Add(new TestCaseDefinition
            {
                Name = name,
                Mark = mark,
                DirectoryPath = dir,
                DetailPath = detail,
                InnerHeaderPath = File.Exists(tcHeaderPath) ? tcHeaderPath : null,
                GradeContent = gradeContent,
                Environment = tcEnv ?? suiteEnv
            });
        }

        if (list.Count == 0)
            throw new InvalidDataException("No test cases found under: " + root);

        return list;
    }

    private static EnvironmentConfiguration? ReadTestCaseEnvironment(string envPath, EnvironmentConfiguration? suiteEnv)
    {
        try
        {
            using var wb = new XLWorkbook(envPath);
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Config", StringComparison.OrdinalIgnoreCase));
            if (ws == null) return suiteEnv;

            // Start with suite environment or create new
            var config = new EnvironmentConfiguration
            {
                MonitorPort = suiteEnv?.MonitorPort,
                ServerPort = suiteEnv?.ServerPort,
                GivenServerPath = suiteEnv?.GivenServerPath,
                GivenClientPath = suiteEnv?.GivenClientPath,
                DatabaseFilePath = suiteEnv?.DatabaseFilePath,
                DatabaseName = suiteEnv?.DatabaseName,
                DatabaseUsername = suiteEnv?.DatabaseUsername,
                DatabasePassword = suiteEnv?.DatabasePassword
            };
            
            // Override with test case specific values
            int startRow = 1;
            var firstRowCol1 = ws.Cell(1, 1).GetString().Trim();
            if (firstRowCol1.Equals("Key", StringComparison.OrdinalIgnoreCase))
            {
                startRow = 2;
            }

            for (int r = startRow; r <= Math.Min(100, ws.RowCount()); r++)
            {
                var key = ws.Cell(r, 1).GetString().Trim();
                var value = ws.Cell(r, 2).GetString().Trim();

                if (string.IsNullOrEmpty(key)) continue;

                if (key.Equals("Default_Database_File_Path", StringComparison.OrdinalIgnoreCase))
                {
                    config.DatabaseFilePath = value;
                }
                else if (key.Equals("Default_Database_Name", StringComparison.OrdinalIgnoreCase))
                {
                    // Skip overriding if value is "database" - this is a placeholder value
                    // commonly used in test case environment.xlsx files to indicate "use suite default".
                    // Test cases should only override this when they need a specific different database.
                    if (!string.IsNullOrWhiteSpace(value) && 
                        !value.Equals("database", StringComparison.OrdinalIgnoreCase))
                    {
                        config.DatabaseName = value;
                    }
                }
            }

            return config;
        }
        catch
        {
            return suiteEnv;
        }
    }
}
