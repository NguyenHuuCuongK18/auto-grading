using Domain.Entities.Enum;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Main.TestCase
{
    public class TestcaseStage
    {
        public int StageNo { get; set; }
        public List<InputStep> Input { get; set; }
        public List<OutputUIAction> OutputUi { get; set; }
        public List<OutputDBAction> OutputDb { get; set; }
        public Status Status { get; set; }  
    }
}
