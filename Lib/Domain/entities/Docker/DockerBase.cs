using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Docker
{
    namespace DockerSupporter.Entity
    {
        public class DockerBase
        {
            public string ImageName { get; set; }
            public string DockerPath { get; set; } 
            public string DockerVolume { get; set; }
            public string ContainerId { get; set; } 
            public string ContainerName { get; set; } 
            public int ContainerPort { get; set; }
            public int HostPort { get; set; }
            public string DockerNetwork { get; set; }
            public string EnvironmentVariable { get; set; } 
            public Dictionary<string, string> EnvironmentVariables { get; set; }
            public string CaptureFilePath { get; set; }
        }
    }

}
