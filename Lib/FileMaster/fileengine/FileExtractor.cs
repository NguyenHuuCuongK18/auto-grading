using Ionic.Zip;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace FileMaster.FileEngine
{
    /// <summary>
    /// Cross-platform file extraction utility.
    /// Supports both Windows (with Shell32 fallback) and Linux (with native zip).
    /// </summary>
    public class FileExtractor
    {
        /// <summary>
        /// Determines if running on Windows platform.
        /// </summary>
        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        /// Extracts a zip file to the destination folder.
        /// Uses Ionic.Zip as primary method, with Shell32 fallback on Windows for corrupted zips.
        /// </summary>
        /// <param name="sourceFile">Path to the zip file</param>
        /// <param name="destination">Destination folder (defaults to source file directory)</param>
        public static void Unzip(string sourceFile, string destination)
        {
            if (string.IsNullOrEmpty(sourceFile))
                throw new ArgumentNullException(nameof(sourceFile), "Source file path must not be empty!");

            if (!File.Exists(sourceFile))
                throw new FileNotFoundException($"Source file not found: {sourceFile}");

            if (string.IsNullOrEmpty(destination))
                destination = Path.GetDirectoryName(sourceFile) ?? Directory.GetCurrentDirectory();

            // Create destination directory if it doesn't exist
            Directory.CreateDirectory(destination);

            try
            {
                // Primary method: Use Ionic.Zip (cross-platform)
                using (Ionic.Zip.ZipFile zips = new Ionic.Zip.ZipFile(sourceFile))
                {
                    zips.ExtractAll(destination, ExtractExistingFileAction.OverwriteSilently);
                }
                Console.WriteLine($"[FileExtractor] Successfully extracted {sourceFile} to {destination} using Ionic.Zip");
            }
            catch (Exception ionicEx)
            {
                Console.WriteLine($"[FileExtractor] Ionic.Zip failed: {ionicEx.Message}, trying fallback...");
                
                // Fallback: Try System.IO.Compression (built-in, cross-platform)
                try
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(sourceFile, destination, overwriteFiles: true);
                    Console.WriteLine($"[FileExtractor] Successfully extracted {sourceFile} using System.IO.Compression");
                }
                catch (Exception sysEx)
                {
                    Console.WriteLine($"[FileExtractor] System.IO.Compression failed: {sysEx.Message}");
                    
                    // Windows-only fallback: Use Shell32 for corrupted/special zips
                    if (IsWindows)
                    {
                        try
                        {
                            ExtractUsingShell32(sourceFile, destination);
                            Console.WriteLine($"[FileExtractor] Successfully extracted {sourceFile} using Shell32");
                        }
                        catch (Exception shellEx)
                        {
                            throw new Exception($"All extraction methods failed. Last error: {shellEx.Message}", shellEx);
                        }
                    }
                    else
                    {
                        // Linux fallback: Use unzip command
                        try
                        {
                            ExtractUsingUnzipCommand(sourceFile, destination);
                            Console.WriteLine($"[FileExtractor] Successfully extracted {sourceFile} using unzip command");
                        }
                        catch (Exception cmdEx)
                        {
                            throw new Exception($"All extraction methods failed. Last error: {cmdEx.Message}", cmdEx);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Extracts a zip file to a destination path.
        /// </summary>
        /// <param name="zipPath">Path to the zip file</param>
        /// <param name="destinationPath">Destination folder</param>
        public static void ExtractDestination(string zipPath, string destinationPath)
        {
            if (string.IsNullOrEmpty(destinationPath))
            {
                destinationPath = Path.GetDirectoryName(zipPath) ?? Directory.GetCurrentDirectory();
            }

            Unzip(zipPath, destinationPath);
        }

        /// <summary>
        /// Windows-only: Extract using Shell32 COM object.
        /// Useful for corrupted or specially formatted zip files.
        /// </summary>
        private static void ExtractUsingShell32(string sourceFile, string destination)
        {
            if (!IsWindows)
                throw new PlatformNotSupportedException("Shell32 extraction is only available on Windows");

            // Dynamic COM interop for Shell32
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            dynamic srcFolder = shell.NameSpace(sourceFile);
            dynamic destFolder = shell.NameSpace(destination);
            
            if (srcFolder == null)
                throw new Exception($"Shell32 could not open source: {sourceFile}");
            if (destFolder == null)
                throw new Exception($"Shell32 could not open destination: {destination}");

            // 20 = No progress dialog, don't confirm overwrites
            destFolder.CopyHere(srcFolder.Items(), 20);
        }

        /// <summary>
        /// Linux: Extract using the unzip command line tool.
        /// </summary>
        private static void ExtractUsingUnzipCommand(string sourceFile, string destination)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "unzip",
                Arguments = $"-o \"{sourceFile}\" -d \"{destination}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new Exception("Failed to start unzip process");

            process.WaitForExit(30000); // 30 second timeout

            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                throw new Exception($"unzip command failed with exit code {process.ExitCode}: {error}");
            }
        }
    }
}
