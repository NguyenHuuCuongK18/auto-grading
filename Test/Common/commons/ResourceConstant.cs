using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common.commons
{
    public class ResourceConstant
    {
        public static readonly string BuildPath = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string ProjectRootRelativePath = @"..\..\..\";
        
        public static readonly string ResourceFolderName = "resources";

        public static readonly string ClientAppSettingsName = "client-appsettings.json";
        public static readonly string ServerAppSettingsName = "server-appsettings.json";
    }
}
