using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Main.TestCase
{
    public class Testcase
    {
        public List<TestcaseStage> Stages { get; set; }
        public double Mark { get; set; }
        public Entities.Enum.Status Status { get; set; } = Enum.Status.Not_Run;
        public System.Exception Exception { get; set; }

        // add percentage mark for test case
        public double PercentageMark { get; set; }


    }
}
