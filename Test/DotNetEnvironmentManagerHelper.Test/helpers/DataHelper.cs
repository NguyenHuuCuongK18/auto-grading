using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DotNetEnvironmentManagerHelper.Test.helpers
{
    public class DataHelper
    {
        private DataHelper() { }

        public static string? ReadConnString(JObject json)
        {
            var connStrings = json["ConnectionStrings"]?["MyCnn"]?.ToString();
            if (!string.IsNullOrEmpty(connStrings)) return connStrings;
            return json["ConnectionString"]?["MyCnn"]?.ToString();
        }
    }
}
