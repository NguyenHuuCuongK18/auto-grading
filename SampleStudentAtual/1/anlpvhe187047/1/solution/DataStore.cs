using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
namespace Q1
{
    public static class DataStore
    {
        public static List<Student> Students { get; } = new List<Student>
    {
        new Student
        {
            StudentId = "S001",
            Name = "John Smith",
            Major = "Computer Science",
            Year = 3,
            GPA = 3.5
        },
        new Student
        {
            StudentId = "S002",
            Name = "Jane Doe",
            Major = "Business Administration",
            Year = 2,
            GPA = 3.8
        },
        new Student
        {
             StudentId = "S003",
            Name = "Bob Wilson",
            Major = "Electrical Engineering",
            Year = 4,
            GPA = 3.2
        },
        new Student
        {
            StudentId = "S004",
            Name = "Alice Johnson",
            Major = "Mathematics",
            Year = 1,
            GPA = 3.9
        },
         new Student
        {
            StudentId = "S005",
            Name = "Charlie Brown",
            Major = "Physics",
            Year = 3,
            GPA = 3.4
        }
    };

    }
    public class Student
    {
        public string? StudentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Major { get; set; } = string.Empty;
        public int Year { get; set; }
        public Double? GPA { get; set; }
    }
}
