using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
//using System.Runtime.Remoting.Messaging;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace Domain.Entities.Main.Question
{
    public class QuestionKit
    {
        public string QuestionKitCode { get; set; }
        public HeaderQ HeaderQ { get; set; }
        public List<TestCase.TestcaseKit> TestcaseKits { get; set; }
        public Environment Environment { get; set; }
    }
}
