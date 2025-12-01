using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace FileMaster.FileEngine
{
    public class XmlHandler : CommonFileHandler
    {
        public override void AppendToFile(string filePath, string xmlData)
        {
            XmlDocument xmlDocument = new XmlDocument();
            using (StreamReader reader = new StreamReader(filePath, System.Text.Encoding.UTF8))
            {
                xmlDocument.Load(reader);
            }
            XmlDocument newDoc = new XmlDocument();
            newDoc.LoadXml(xmlData);

            XmlNode newNode = newDoc.DocumentElement;
            XmlNode importedNode = xmlDocument.ImportNode(newNode, true);

            if (importedNode.Attributes != null)
            {
                importedNode.Attributes.RemoveNamedItem("xmlns:xsd");
                importedNode.Attributes.RemoveNamedItem("xmlns:xsi");
            }

            xmlDocument.DocumentElement.AppendChild(importedNode);
            xmlDocument.Save(filePath);
        }
    }
}
