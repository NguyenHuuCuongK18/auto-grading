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
    /// Utility class for file extraction operations.
    /// </summary>
    public class FileExtractor
    {
        /// <summary>
        /// Unzips a zip file to a destination folder.
        /// </summary>
        /// <param name="sourceFile">The source zip file path.</param>
        /// <param name="destination">The destination folder. If null, uses the source file's directory.</param>
        public static void Unzip(string sourceFile, string destination)
        {
            if (string.IsNullOrEmpty(sourceFile))
                throw new ArgumentNullException("First param in method unzip must not be empty!");

            if (string.IsNullOrEmpty(destination))
                destination = Path.GetDirectoryName(sourceFile) ?? ".";

            // Use DotNetZip's extract functionality
            using (ZipFile zips = new ZipFile(sourceFile))
            {
                zips.ExtractAll(destination, ExtractExistingFileAction.OverwriteSilently);
            }
            
            Console.WriteLine($"[FileExtractor] Extracted {sourceFile} to {destination}");
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

            using (ZipFile zips = new ZipFile(zipPath))
            {
                zips.ExtractAll(destinationPath, ExtractExistingFileAction.OverwriteSilently);
            }
            Console.WriteLine($"Extracted {zipPath} to {destinationPath}");
        }

    }
}
