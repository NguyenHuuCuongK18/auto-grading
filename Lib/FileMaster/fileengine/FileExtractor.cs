using EnvironmentBuilder.CommandSupporter;
using Ionic.Zip;
using System;
using Shell32;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace FileMaster.FileEngine
{
    public class FileExtractor
    {
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
            Shell sc = new Shell();
            Folder SrcFlder = sc.NameSpace(sourceFile);
            Folder DestFlder = sc.NameSpace(destination);
            FolderItems items = SrcFlder.Items();
            DestFlder.CopyHere(items, 20);
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
