using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace FileMaster.Utils
{
    public class Converter
    {
        public static string ConvertToJson<T>(T data)
        {
            return JsonConvert.SerializeObject(data, Formatting.Indented);
        }

        public static T ConvertJsonToObject<T>(string jsonData)
        {
            return JsonConvert.DeserializeObject<T>(jsonData);
        }

        public static string ConvertToXML<T>(T data)
        {
            XmlSerializer xmlSerializer = new XmlSerializer(typeof(T));
            using (StringWriter textWriter = new StringWriter())
            {
                xmlSerializer.Serialize(textWriter, data);
                return textWriter.ToString();
            }
        }

        public static T ConvertXMLToObject<T>(string xmlData)
        {
            XmlSerializer xmlSerializer = new XmlSerializer(typeof(T));
            using (StringReader reader = new StringReader(xmlData))
            {
                return (T)xmlSerializer.Deserialize(reader);
            }
        }
    }
}
