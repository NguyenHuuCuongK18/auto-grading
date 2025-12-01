using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Main
{
    public class InputStep
    {
        public int StepNo { get; set; }
        public string Attribute { get; set; }
        public string AttributeValue { get; set; }
        public string ValueType { get; set; }
        public string Value { get; set; }
        public string InputAction { get; set; }
        public Entities.Enum.Status Status { get; set; } = Enum.Status.Not_Run;
        public System.Exception Exception { get; set; }

        public int Browser { get; set; } = 1;
    }
}
