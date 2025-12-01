using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FileMaster.FileEngine
{
    /// <summary>
    /// Utility class for file extraction operations.
    /// Cross-platform support: Uses Shell32 COM on Windows for robust extraction,
    /// falls back to System.IO.Compression on Linux/Mac.
    /// </summary>
    public class FileExtractor
    {
        /// <summary>
        /// Unzips a zip file to a destination folder.
        /// On Windows, uses Shell32 COM for robust extraction (handles edge cases).
        /// On Linux/Mac, uses System.IO.Compression.
        /// </summary>
        /// <param name="sourceFile">The source zip file path.</param>
        /// <param name="destination">The destination folder. If null, uses the source file's directory.</param>
        public static void Unzip(string sourceFile, string destination)
        {
            if (string.IsNullOrEmpty(sourceFile))
                throw new ArgumentNullException("First param in method unzip must not be empty!");

            if (string.IsNullOrEmpty(destination))
                destination = Path.GetDirectoryName(sourceFile) ?? ".";

            // First try with DotNetZip (Ionic.Zip)
            try
            {
                using (Ionic.Zip.ZipFile zips = new Ionic.Zip.ZipFile(sourceFile))
                {
                    zips.ExtractAll(destination, Ionic.Zip.ExtractExistingFileAction.OverwriteSilently);
                }
                Console.WriteLine($"[FileExtractor] Extracted {sourceFile} to {destination} using DotNetZip");
                return;
            }
            catch (Exception dotNetZipEx)
            {
                Console.WriteLine($"[FileExtractor] DotNetZip failed: {dotNetZipEx.Message}. Trying fallback...");
            }

            // Fallback: On Windows use Shell32 COM, on Linux use System.IO.Compression
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    ExtractWithShell32(sourceFile, destination);
                    Console.WriteLine($"[FileExtractor] Extracted {sourceFile} to {destination} using Shell32");
                }
                catch (Exception shellEx)
                {
                    Console.WriteLine($"[FileExtractor] Shell32 failed: {shellEx.Message}. Trying System.IO.Compression...");
                    ExtractWithSystemIOCompression(sourceFile, destination);
                }
            }
            else
            {
                // Linux/Mac fallback using System.IO.Compression
                ExtractWithSystemIOCompression(sourceFile, destination);
                Console.WriteLine($"[FileExtractor] Extracted {sourceFile} to {destination} using System.IO.Compression");
            }
        }

        /// <summary>
        /// Extracts using Shell32 COM (Windows only).
        /// This handles some edge cases that other libraries may miss.
        /// </summary>
        private static void ExtractWithShell32(string sourceFile, string destination)
        {
            // Dynamic COM invocation to avoid compile-time dependency on Shell32
            // This allows the code to compile on Linux but only execute on Windows
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                throw new InvalidOperationException("Shell.Application COM type not available");

            // Ensure destination directory exists before Shell32 can use it
            Directory.CreateDirectory(destination);

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic srcFolder = shell.NameSpace(sourceFile);
            dynamic destFolder = shell.NameSpace(destination);

            if (srcFolder == null)
                throw new InvalidOperationException($"Could not open source zip: {sourceFile}");
            if (destFolder == null)
                throw new InvalidOperationException($"Could not open destination folder: {destination}");

            dynamic items = srcFolder.Items();
            // 20 = don't display progress dialog, auto-yes to all prompts
            destFolder.CopyHere(items, 20);
        }

        /// <summary>
        /// Extracts using System.IO.Compression (cross-platform).
        /// </summary>
        private static void ExtractWithSystemIOCompression(string sourceFile, string destination)
        {
            Directory.CreateDirectory(destination);
            System.IO.Compression.ZipFile.ExtractToDirectory(sourceFile, destination, true);
        }

        /// <summary>
        /// Extracts a zip file to a specified destination path.
        /// </summary>
        /// <param name="zipPath">The path to the zip file.</param>
        /// <param name="destinationPath">The destination path. If null, uses the zip file's directory.</param>
        public static void ExtractDestination(string zipPath, string destinationPath)
        {
            if (destinationPath == null)
            {
                destinationPath = Path.GetDirectoryName(zipPath) ?? ".";
            }

            // Try DotNetZip first
            try
            {
                using (Ionic.Zip.ZipFile zips = new Ionic.Zip.ZipFile(zipPath))
                {
                    zips.ExtractAll(destinationPath, Ionic.Zip.ExtractExistingFileAction.OverwriteSilently);
                }
                Console.WriteLine($"Extracted {zipPath} to {destinationPath}");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileExtractor] DotNetZip failed for ExtractDestination: {ex.Message}. Using fallback...");
            }

            // Fallback
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    ExtractWithShell32(zipPath, destinationPath);
                }
                catch (Exception)
                {
                    ExtractWithSystemIOCompression(zipPath, destinationPath);
                }
            }
            else
            {
                ExtractWithSystemIOCompression(zipPath, destinationPath);
            }
            Console.WriteLine($"Extracted {zipPath} to {destinationPath}");
        }
    }
}
