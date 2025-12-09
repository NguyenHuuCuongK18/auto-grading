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

        public static readonly string ClientResourceFolderName = "client";
        public static readonly string ServerResourceFolderName = "server";

        public static readonly string AppSettingsName = "appsettings.json";
    }
}
