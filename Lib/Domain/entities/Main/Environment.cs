using Domain.Entities.Enum;
using System;
using System.Collections.Generic;

namespace Domain.Entities.Main
{
    public class Environment
    {
        public Dictionary<string, string> Configs { get; set; }
        public List<string> Steps { get; set; }
    }
}

