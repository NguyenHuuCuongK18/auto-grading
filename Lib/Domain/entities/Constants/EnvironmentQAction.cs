using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Constants
{
    public class EnvironmentQAction
    {
        // new actions
        public const string CopyEssentialFilesAndFolders = "copy_essential_files_and_folders";
        public const string GenerateDatabaseScript = "generate_database_script";
        public const string GenerateConnectionFile = "generate_connection_file";

        //add new step for dotnet webapp calling web services
        public const string ModifyGivenApiUrl = "modify_given_api_url";
        public const string CopyGivenApiPublish = "copy_given_api_publish";


        public const string ModifyGivenIp = "modify_given_ip";
    }
}

