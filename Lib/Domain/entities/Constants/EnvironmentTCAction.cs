using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Constants
{
    public class EnvironmentTcAction
    {
        // actions
        public const string ResetDatabase = "reset_database";

        // dotnet specific step for webapp calling webapi
        public const string ResetGivenAPI = "reset_given_api";
    }
}
