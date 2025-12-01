using EnvironmentBuilder.CommandSupporter;
using Ionic.Zip;
using System;
// Shell32 is Windows-only and removed for cross-platform compatibility
// using Shell32;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace FileMaster.FileEngine
{
    public class FileExtractor
    {
        /// <summary>
        /// Unzips a file to the destination folder.
        /// Note: Shell32 functionality (Windows Shell extraction) has been removed for cross-platform compatibility.
        /// Uses Ionic.Zip for extraction instead.
        /// </summary>
        public static void Unzip(string sourceFile, string destination)
        {
            if (string.IsNullOrEmpty(sourceFile))
                throw new ArgumentNullException("First param in method unzip must not be empty!");

            if (string.IsNullOrEmpty(destination))
                destination = Path.GetDirectoryName(sourceFile);

            using (ZipFile zips = new ZipFile(sourceFile))
            {
                zips.ExtractAll(destination);
            }   
            // Shell32 functionality removed for cross-platform compatibility
            // The Ionic.Zip extraction above should be sufficient
        }
        public static void ExtractDestination(string zipPath, string destinationPath)
        {
            if (destinationPath == null)
            {
                destinationPath = Path.GetDirectoryName(zipPath);
            }

            using (ZipFile zips = new ZipFile(zipPath))
            {
                zips.ExtractAll(destinationPath);
            }
            Console.WriteLine($"Extracted {zipPath} to {destinationPath}");
        }

    }
}
