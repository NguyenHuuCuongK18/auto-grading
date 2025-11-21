using Domain.Entities.Enum;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Main.Question
{
    public class HeaderQ
    {
        public string QuestionCode { get; set; }
        public Dictionary<string, string> FormatPattern { get; set; }
        public string QuestionMarkPath { get; set; }
    }
}
