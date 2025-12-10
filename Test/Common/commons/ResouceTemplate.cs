using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common.commons
{
    public class ResouceTemplate
    {
        public static readonly string AppSettingsTemplate = """
        {
          "IpAddress": "{0}",
          "Port": "{1}"
        }
        """;

        public static readonly string AppSettingsWithDBTemplate = """
        {
          "IpAddress": "{0}",
          "Port": "{1}",
          "ConnectionStrings": {
            "MyCnn" : "{2}"
          }
        }
        """;

        public static readonly string AppSettingsWithDBTemplateNoCnn = """
        {
          "IpAddress": "{0}",
          "Port": "{1}",
          "ConnectionStrings": {}
        }
        """;
    }
}
