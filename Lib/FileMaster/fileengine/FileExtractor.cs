using System;
using System.IO;
using System.IO.Compression;

namespace FileMaster.FileEngine
{
    /// <summary>
    /// Provides file extraction utilities for zip files.
    /// Uses System.IO.Compression.ZipFile for secure cross-platform compatibility.
    /// </summary>
    public class FileExtractor
    {
        /// <summary>
        /// Extracts a zip file to the specified destination.
        /// If destination is not specified, extracts to the source file's directory.
        /// </summary>
        /// <param name="sourceFile">Path to the zip file</param>
        /// <param name="destination">Destination directory (optional)</param>
        public static void Unzip(string sourceFile, string destination)
        {
            if (string.IsNullOrEmpty(sourceFile))
                throw new ArgumentNullException(nameof(sourceFile), "Source file path must not be empty!");

            if (string.IsNullOrEmpty(destination))
                destination = Path.GetDirectoryName(sourceFile) ?? ".";

            // Ensure destination directory exists
            Directory.CreateDirectory(destination);
            
            // Use System.IO.Compression for extraction (built-in, no vulnerability)
            ZipFile.ExtractToDirectory(sourceFile, destination, overwriteFiles: true);
            
            Console.WriteLine($"Extracted {sourceFile} to {destination}");
        }
        
        /// <summary>
        /// Extracts a zip file to the specified destination path.
        /// </summary>
        /// <param name="zipPath">Path to the zip file</param>
        /// <param name="destinationPath">Destination directory</param>
        public static void ExtractDestination(string zipPath, string destinationPath)
        {
            if (string.IsNullOrEmpty(destinationPath))
            {
                destinationPath = Path.GetDirectoryName(zipPath) ?? ".";
            }

            // Ensure destination directory exists
            Directory.CreateDirectory(destinationPath);
            
            // Use System.IO.Compression for extraction (built-in, no vulnerability)
            ZipFile.ExtractToDirectory(zipPath, destinationPath, overwriteFiles: true);
            
            Console.WriteLine($"Extracted {zipPath} to {destinationPath}");
        }
    }
}
