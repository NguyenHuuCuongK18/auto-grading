using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FileMaster.FileEngine
{
    public class CommonFileHandler
    {
        public void WaitForFileCreation(string filePath, int timeoutInSeconds = 10)
        {
            int elapsedSeconds = 0;
            while (!File.Exists(filePath))
            {
                if (elapsedSeconds >= timeoutInSeconds)
                {
                    throw new TimeoutException($"Tệp {filePath} không được tạo trong thời gian quy định.");
                }

                Thread.Sleep(1000);
                elapsedSeconds += 1;
            }
        }

        public void CreateFolder(string directory, string folderName) => Directory.CreateDirectory(directory + @"\" + folderName);

        public void CopyFile(string source, string destination) => File.Copy(source, destination);
        /// <summary>
        /// Copies all files and subdirectories from source to destination, including empty directories.
        /// </summary>
        /// <param name="sourceDir">Source directory path</param>
        /// <param name="destinationDir">Destination directory path</param>
        public void CopyDirectory(string sourceDir, string destinationDir)
        {
            if (sourceDir == destinationDir)
            {
                return;
            }
            Directory.CreateDirectory(destinationDir);

            // Create all subdirectories in destination, including empty ones
            foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var destinationPath = dirPath.Replace(sourceDir, destinationDir);
                Directory.CreateDirectory(destinationPath);
            }

            // Copy all files from source and its subdirectories
            foreach (var filePath in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                var destinationPath = filePath.Replace(sourceDir, destinationDir);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                File.Copy(filePath, destinationPath, true);
            }

            Console.WriteLine($"Successfully copied directory from {sourceDir} to {destinationDir}");
        }

        public void CopyFile(string source, string destination, string fileName) => File.Copy(Path.Combine(destination, fileName), Path.Combine(destination, fileName));

        public void CreateFolder(string directory) => Directory.CreateDirectory(directory);

        public virtual void AppendToFile(string filePath, string rawData) => File.AppendAllText(filePath, Environment.NewLine + rawData);

        public void WriteToFile(string filePath, string rawData) => File.WriteAllText(filePath, rawData);
        public void ClearSubdirectories(string folderPath)
        {
            try
            {
                if (Directory.Exists(folderPath))
                {
                    foreach (string file in Directory.GetFiles(folderPath))
                    {
                        File.Delete(file);
                    }

                    foreach (string directory in Directory.GetDirectories(folderPath))
                    {
                        Directory.Delete(directory, true);
                    }
                }
                else
                {
                    throw new Exception("File not found!");
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        public void DeleteFile(string filepath)
        {
            if (IsFileExisted(filepath))
                File.Delete(filepath);
            else
                throw new FileNotFoundException();
        }

        public bool IsFileExisted(string filepath)
        {
            return File.Exists(filepath);
        }

        public virtual string ReadFile(string filePath)
        {
            if (!IsFileExisted(filePath))
            {
                throw new FileNotFoundException();
            }
            return File.ReadAllText(filePath);
        }
    }
}
