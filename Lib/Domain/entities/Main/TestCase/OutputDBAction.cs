using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;

namespace Domain.Entities.Main
{
    public class OutputDBAction
    {
        public int StepNo { get; set; }
        public string Query { get; set; }
        public DataTable ExpectedData { get; set; }
        public Enum.Status Status { get; set; } = Enum.Status.Not_Run;
        public System.Exception Exception { get; set; }

        // expected data for nosql
        public string ExpectedJsonData { get; set; }   
    }
}
