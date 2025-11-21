using Domain.Entities.Enum;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Main.TestCase
{
    public class TestcaseKit
    {
        public int No { get; set; }
        public string TestcaseId { get; set; }
        public string TestDesc { get; set; }
        public bool IsRun { get; set; }
        public Dictionary<string, string> HeaderTC { get; set; }
        public Testcase TestCaseDetail { get; set; }
        public Environment Environment { get; set; }
        public List<BrowserConfig> BrowserConfigs { get; set; }
        public List<Config> Configs { get; set; } = new List<Config>(); 
    }

}
