using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Linq;
using SolutionGrader.Core.Exceptions;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Service for executing database queries and verifying results.
    /// Based on test-grader DBService pattern for database output grading.
    /// </summary>
    public class DatabaseQueryService
    {
        /// <summary>
        /// Executes a SQL query and returns the results as a DataTable.
        /// Connects directly to the database container via exposed port using ADO.NET.
        /// </summary>
        /// <param name="databaseManagementSystem">Database type (e.g., "mssql", "postgresql")</param>
        /// <param name="connectionString">Connection string to student database</param>
        /// <param name="query">SQL query to execute</param>
        /// <returns>DataTable with query results</returns>
        public static DataTable GetActualData(string databaseManagementSystem, string connectionString, string query)
        {
            DataTable dataTable = new DataTable();
            switch (databaseManagementSystem.ToLowerInvariant())
            {
                case "mssql":
                case "sqlserver":
                    dataTable = GetMssqlTableData(query, connectionString);
                    break;
                case "postgresql":
                case "postgres":
                    // PostgreSQL support can be added later with Npgsql
                    throw new NotSupportedException("PostgreSQL support not yet implemented. Add Npgsql NuGet package and implement GetPostgresTableData.");
                case "mysql":
                    throw new NotSupportedException("MySQL support not yet implemented.");
                default:
                    throw new NotSupportedException($"Database management system '{databaseManagementSystem}' is not supported");
            }
            return dataTable;
        }

        /// <summary>
        /// Executes query on MSSQL database and returns results.
        /// Uses SqlConnection for direct connection to container via exposed port.
        /// </summary>
        private static DataTable GetMssqlTableData(string query, string connectionString)
        {
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        using (SqlDataAdapter adapter = new SqlDataAdapter(command))
                        {
                            DataTable dataTable = new DataTable();
                            adapter.Fill(dataTable);
                            return dataTable;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to connect to database with connection string: {connectionString}\nError: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Verifies that actual data matches expected data.
        /// Compares row count, column count, and cell-by-cell values.
        /// </summary>
        /// <param name="actualData">DataTable with actual query results</param>
        /// <param name="expectedData">DataTable with expected results from testkit</param>
        /// <param name="formatPatterns">Optional format patterns for type-specific comparisons (e.g., double precision)</param>
        public static void VerifyTableData(
            DataTable actualData, 
            DataTable expectedData, 
            Dictionary<string, string> formatPatterns = null)
        {
            if (actualData == null)
                throw new DataMismatchException("Actual data is null");
            
            if (expectedData == null)
                throw new DataMismatchException("Expected data is null");

            if (actualData.Rows.Count != expectedData.Rows.Count)
                throw new DataMismatchException(
                    $"Row count mismatch: Expected {expectedData.Rows.Count} rows, but got {actualData.Rows.Count} rows");

            if (actualData.Columns.Count != expectedData.Columns.Count)
                throw new DataMismatchException(
                    $"Column count mismatch: Expected {expectedData.Columns.Count} columns, but got {actualData.Columns.Count} columns");

            // Compare each cell
            for (int i = 0; i < actualData.Rows.Count; i++)
            {
                for (int j = 0; j < expectedData.Columns.Count; j++)
                {
                    try
                    {
                        CompareCellData(
                            actualData.Rows[i][j], 
                            expectedData.Rows[i][j], 
                            formatPatterns ?? new Dictionary<string, string>());
                    }
                    catch (DataMismatchException ex)
                    {
                        throw new DataMismatchException(
                            $"Data mismatch at row {i + 1}, column {j + 1} ('{expectedData.Columns[j].ColumnName}'): {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Compares two cell values with type-aware comparison.
        /// Handles double, int, DateTime, decimal, and string types.
        /// </summary>
        private static void CompareCellData(
            object actualCell, 
            object expectedCell, 
            Dictionary<string, string> formatPatterns)
        {
            // Handle null cases
            if (actualCell == null || actualCell == DBNull.Value)
            {
                if (expectedCell == null || expectedCell == DBNull.Value)
                    return; // Both null, match
                else
                    throw new DataMismatchException($"Expected '{expectedCell}', but got NULL");
            }

            if (expectedCell == null || expectedCell == DBNull.Value)
                throw new DataMismatchException($"Expected NULL, but got '{actualCell}'");

            // Type-specific comparisons
            switch (actualCell)
            {
                // Double comparison with optional precision
                case double d1:
                    if (!double.TryParse(expectedCell.ToString(), out double d2))
                        throw new DataMismatchException($"Cannot parse '{expectedCell}' to double");

                    string formatPattern = formatPatterns.ContainsKey("double") 
                        ? formatPatterns["double"] 
                        : "{0:0.##}";
                    
                    string formatted1 = string.Format(CultureInfo.InvariantCulture, formatPattern, d1);
                    string formatted2 = string.Format(CultureInfo.InvariantCulture, formatPattern, d2);
                    
                    if (!formatted1.Equals(formatted2))
                        throw new DataMismatchException($"Expected '{formatted2}' (double), but got '{formatted1}'");
                    break;

                // Decimal comparison
                case decimal dec1:
                    if (!decimal.TryParse(expectedCell.ToString(), out decimal dec2))
                        throw new DataMismatchException($"Cannot parse '{expectedCell}' to decimal");

                    string decimalPattern = formatPatterns.ContainsKey("decimal") 
                        ? formatPatterns["decimal"] 
                        : "{0:0.##}";
                    
                    string formattedDec1 = string.Format(CultureInfo.InvariantCulture, decimalPattern, dec1);
                    string formattedDec2 = string.Format(CultureInfo.InvariantCulture, decimalPattern, dec2);
                    
                    if (!formattedDec1.Equals(formattedDec2))
                        throw new DataMismatchException($"Expected '{formattedDec2}' (decimal), but got '{formattedDec1}'");
                    break;

                // Integer comparison
                case int i1:
                    if (!int.TryParse(expectedCell.ToString(), out int i2))
                        throw new DataMismatchException($"Cannot parse '{expectedCell}' to int");
                    
                    if (i1 != i2)
                        throw new DataMismatchException($"Expected '{i2}' (int), but got '{i1}'");
                    break;

                // Long comparison
                case long l1:
                    if (!long.TryParse(expectedCell.ToString(), out long l2))
                        throw new DataMismatchException($"Cannot parse '{expectedCell}' to long");
                    
                    if (l1 != l2)
                        throw new DataMismatchException($"Expected '{l2}' (long), but got '{l1}'");
                    break;

                // DateTime comparison
                case DateTime dt1:
                    if (!DateTime.TryParse(expectedCell.ToString(), out DateTime dt2))
                        throw new DataMismatchException($"Cannot parse '{expectedCell}' to DateTime");

                    string datePattern = formatPatterns.ContainsKey("datetime") 
                        ? formatPatterns["datetime"] 
                        : "yyyy-MM-dd HH:mm:ss";
                    
                    string formattedDt1 = dt1.ToString(datePattern, CultureInfo.InvariantCulture);
                    string formattedDt2 = dt2.ToString(datePattern, CultureInfo.InvariantCulture);
                    
                    if (!formattedDt1.Equals(formattedDt2))
                        throw new DataMismatchException($"Expected '{formattedDt2}' (DateTime), but got '{formattedDt1}'");
                    break;

                // Boolean comparison
                case bool b1:
                    if (!bool.TryParse(expectedCell.ToString(), out bool b2))
                        throw new DataMismatchException($"Cannot parse '{expectedCell}' to bool");
                    
                    if (b1 != b2)
                        throw new DataMismatchException($"Expected '{b2}' (bool), but got '{b1}'");
                    break;

                // Default: string comparison
                default:
                    string str1 = actualCell.ToString();
                    string str2 = expectedCell.ToString();
                    
                    if (!str1.Equals(str2, StringComparison.Ordinal))
                        throw new DataMismatchException($"Expected '{str2}' (string), but got '{str1}'");
                    break;
            }
        }
    }
}
