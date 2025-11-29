using EnvironmentBuilder.CommandSupporter;
using Ionic.Zip;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace FileMaster.FileEngine
{
    /// <summary>
    /// Provides file extraction utilities for zip files.
    /// Uses DotNetZip (Ionic.Zip) for cross-platform compatibility.
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

            // Use DotNetZip for extraction (cross-platform compatible)
            using (ZipFile zips = new ZipFile(sourceFile))
            {
                zips.ExtractAll(destination, ExtractExistingFileAction.OverwriteSilently);
            }
            
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

            using (ZipFile zips = new ZipFile(zipPath))
            {
                zips.ExtractAll(destinationPath, ExtractExistingFileAction.OverwriteSilently);
            }
            Console.WriteLine($"Extracted {zipPath} to {destinationPath}");
        }

    }
}
