using System;
using System.IO;

namespace FileMaster.Utils
{
    public static class LogWriter
    {
        public static void WriteLog(string text, string path = @"D:\Temps\")
        {
            Directory.CreateDirectory(path);

            string fileName = $"log_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()}.txt";
            string fullPath = System.IO.Path.Combine(path, fileName);

            using (StreamWriter outputFile = new StreamWriter(fullPath, true))
            {
                outputFile.WriteLine(text);
            }
        }
    }
}
