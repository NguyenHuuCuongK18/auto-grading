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
          "IpAddress": "127.0.0.1",
          "Port": "5000"
        }
        """;

        public static readonly string AppSettingsWithDBTemplate = """
        {
          "IpAddress": "127.0.0.1",
          "Port": "5000",
          "ConnectionString": {
            "MyCnn" : "licmaballs"
          }
        }
        """;
    }
}
