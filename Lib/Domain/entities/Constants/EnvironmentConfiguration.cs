using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Constants
{
    public class EnvironmentConfiguration
    {
        // docker
        public const string EnvironmentType = "Environment_Type";
        public const string DockerNetwork = "Docker_Network";

        // common & runtime configurations
        public const string DatabaseName = "Database_Name"; // name of the db, has the same value to student question name. May remove later ex: q1_test170000
        public const string StudentQuestionName = "Student_Question_Name"; // name of the question. ex: q1_test170000
        public const string StudentQuestionPath = "Student_Question_Path"; // path to war/publish file

        // code
        public const string CodeFilePath = "Code_File_Path"; // represent tomcat war/dotnet publish file
        public const string CodeImageName = "Code_Image_Name"; // represent dotnet/tomcat image name
        public const string CodeContainerName = "Code_Container_Name"; // represent dotnet/tomcat container name
        public const string CodeContainerInternalPort = "Code_Container_Internal_Port"; // code container port
        public const string CodeContainerHostPort = "Code_Container_Host_Port"; // host port that maps to container port. ex: -p hostport:containerport
        // given code project (web service)
        public const string GivenApiUrl = "Given_API_URL";
        public const string GivenApiFilePath = "Given_API_File_Path"; // given api publish file
        public const string GivenApiAppName = "Given_API_App_Name";
        public const string GivenApiImageName = "Given_API_Image_Name";
        public const string GivenApiContainerName = "Given_API_Container_Name";
        public const string GivenApiContainerInternalPort = "Given_API_Internal_Port";
        public const string GivenApiContainerHostPort = "Given_API_Host_Port";

        // database
        public const string DatabaseImageName = "Database_Image_Name"; // represent sql/mongodb image name
        public const string DatabaseContainerName = "Database_Container_Name"; // represent sql/mongodb container name
        public const string DatabaseContainerInternalPort = "Database_Container_Internal_Port"; // db container port
        public const string DatabaseContainerHostPort = "Database_Container_Host_Port"; // host port that maps to container port. ex: -p hostport:containerport
        public const string DatabaseUsername = "Database_Username";
        public const string DatabasePassword = "Database_Password";
        public const string DatabaseManagementSystem = "Database_Management_System";

        // scripts/files
        public const string DefaultDatabaseName = "Default_Database_Name"; // represent database name in script
        public const string DefaultDatabaseFilePath = "Default_Database_File_Path"; // replace SQL_File
        public const string DatabaseConnectionFilePath = "Database_Connection_File_Path"; // replace ConnectDb_File
        public const string ConnectDbFile = "ConnectDb_File"; // path to Connection.properties file (only appear in java jsp for now). ex: Meta\Connection.properties
        public const string RuntimesFolder = "Runtimes_Folder";

        // optional given console apps (client/server) in Meta/Given
        public const string Client = "Client";
        public const string Server = "Server";

        // bổ sung cho signalr và apptype
        public const string SignalRHub = "SignalR_Hub";
        public const string AppType = "App_Type";

    }
}
