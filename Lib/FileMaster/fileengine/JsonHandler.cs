using Newtonsoft.Json;
using OfficeOpenXml.Style.XmlAccess;
using System;
using System.Collections.Generic;
using System.IO;

namespace FileMaster.FileEngine
{
    public class JsonHandler<T> : CommonFileHandler
    {
        public override void AppendToFile(string filePath, string JsonData)
        {
            string jsonContent = ReadFile(filePath);
            List<T> objs;
            if (IsFileExisted(filePath) && new FileInfo(filePath).Length > 0)
            {
                objs = JsonConvert.DeserializeObject<List<T>>(jsonContent);
            }
            else
            {
                objs = new List<T>();
            }

            if (!string.IsNullOrEmpty(JsonData))
            {
                List<T> appendData = JsonConvert.DeserializeObject<List<T>>(JsonData);
                objs.AddRange(appendData);
            }
            else
            {
                throw new ArgumentNullException();
            }

            string updatedData = JsonConvert.SerializeObject(objs, Formatting.Indented);
            WriteToFile(filePath, updatedData);
        }

        public void SaveJsonFile(string filePath, T data)
        {
            SaveJsonToFile(filePath, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        public void SaveJsonFile(string filePath, List<T> dataList)
        {
            SaveJsonToFile(filePath, JsonConvert.SerializeObject(dataList, Formatting.Indented));
        }

        private void SaveJsonToFile(string filePath, string jsonData)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? throw new InvalidOperationException("Đường dẫn không hợp lệ."));

                File.WriteAllText(filePath, jsonData);
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

    }
}
