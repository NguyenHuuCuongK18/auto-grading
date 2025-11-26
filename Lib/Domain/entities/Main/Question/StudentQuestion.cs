using Domain.Entities.Enum;
using Domain.Entities.Main.TestCase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Main.Question
{
    public class StudentQuestion
    {
        public string QuestionCode { get; set; }
        public string SolutionPath { get; set; }
        public string QuestionKitPath { get; set; }
        public double Mark { get; set; }
        public Status Status { get; set; }
        public QuestionKit QuestionKit { get; set; }
    }
}
