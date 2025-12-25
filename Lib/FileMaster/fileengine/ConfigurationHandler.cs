using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using Domain.Entities.Main.TestCase;
using Newtonsoft.Json.Linq;
namespace FileMaster.FileEngine
{
    public class ConfigurationHandler
    {
        private string FilePath;
        private string Content;
        private readonly JObject _config;

        public ConfigurationHandler(string path)
        {
            try
            {
                FilePath = path;
                if (!File.Exists(FilePath))
                {
                    throw new FileNotFoundException($"Configuration file {FilePath} not found.");
                }

                string jsonContent = File.ReadAllText(FilePath);
                _config = JObject.Parse(jsonContent);
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }


        public string GetValue(string key)
        {
            var tokens = key.Split(':');
            JToken currentToken = _config;

            foreach (var token in tokens)
            {
                if (currentToken[token] != null)
                {
                    currentToken = currentToken[token];
                }
                else
                {
                    return null;
                }
            }
            return currentToken.ToString();
        }

    }
}
