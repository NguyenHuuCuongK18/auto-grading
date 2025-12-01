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
        public void CopyDirectory(string sourceDir, string destinationDir)
        {
            if (sourceDir == destinationDir)
            {
                return;
            }
            Directory.CreateDirectory(destinationDir);

            //foreach (var filePath in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            //{
            //    var destinationPath = filePath.Replace(sourceDir, destinationDir);
            //    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
            //    File.Copy(filePath, destinationPath, true);
            //}
            // Tạo lại tất cả thư mục con trong thư mục đích, bao gồm cả thư mục rỗng
            foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var destinationPath = dirPath.Replace(sourceDir, destinationDir);
                Directory.CreateDirectory(destinationPath); // Tạo thư mục đích
            }

            // Sao chép tất cả các file từ thư mục gốc và thư mục con
            foreach (var filePath in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                var destinationPath = filePath.Replace(sourceDir, destinationDir);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)); // Đảm bảo thư mục tồn tại
                File.Copy(filePath, destinationPath, true); // Sao chép file, ghi đè nếu file đã tồn tại
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
