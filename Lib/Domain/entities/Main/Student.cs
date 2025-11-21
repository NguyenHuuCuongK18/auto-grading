using Domain.Entities.Enum;
using Domain.Entities.Main.Question;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Main
{
    public class Student
    {
        public List<StudentQuestion> StudentQuestions { get; set; } = new List<StudentQuestion>();
        public string PaperCode { get; set; }
        public string StudentCode { get; set; }
        public Status Status { get; set; } = Status.Not_Run;
        public double Mark { get; set; }
        public DateTime? StartDate { get; set; } = null;
        public DateTime? EndDate { get; set; } = null;
    }
}
