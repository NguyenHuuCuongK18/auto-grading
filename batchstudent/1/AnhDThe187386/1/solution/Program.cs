using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

class Program
{
    private static List<Student> students = new List<Student>();
    public class Student
    {
        public string Studentid { get; set; }
        public string Name { get; set; }
        public string Major { get; set; }
        public int Year { get; set; }
        public double GPA { get; set; }
    }

    class StudentResponse
    {
        public string StudentId { get; set; } = "";
        public string? Name { get; set; }
        public string? Major { get; set; }
        public int? Year { get; set; }
        public decimal? GPA { get; set; }
        public string Status { get; set; } = "";
        public string Message { get; set; } = "";
    }
    static async Task Main(string[] args)
    {
        InitializeSampleData();
        try
        {

       
        TcpListener server = new TcpListener(IPAddress.Parse("127.0.0.1"), 4000);
        server.Start();
        Console.WriteLine("Student Information Server is running on 127.0.0.1:4000\"");

        while (true)
        {
            var client = await server.AcceptTcpClientAsync();
            Console.WriteLine($"Client connected from 127.0.0.1:4000");

            using var stream = client.GetStream();
            byte[] buffer = new byte[4096];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            foreach (var item in students)
            {
                if (!request.StartsWith("S") || (request.Length != 4))
                {
                    var studentResponse = new StudentResponse
                    {
                        StudentId = request,
                        Name = null,
                        Major = null,
                        Year = null,
                        GPA = null,
                        Status = "error",
                        Message = "Invalid student ID format. Expected: S followed by 3 digits"
                    };
                    string json = JsonSerializer.Serialize(studentResponse);
                    byte[] data = Encoding.UTF8.GetBytes(json);
                    await stream.WriteAsync(data, 0, data.Length);
                    Console.WriteLine("Sent students to client.");
                    break;
                }
                else if (request.Equals(item.Studentid))
                {
                    var studentResponse = new StudentResponse
                    {
                        StudentId = item.Studentid,
                        Name = item.Name,
                        Major = item.Major,
                        Year = item.Year,
                        GPA = (decimal)item.GPA,
                        Status = "success",
                        Message = "Student information retrieved successfully"
                    };
                    string json = JsonSerializer.Serialize(studentResponse);
                    byte[] data = Encoding.UTF8.GetBytes(json);
                    await stream.WriteAsync(data, 0, data.Length);
                    Console.WriteLine("Sent students to client.");
                    break;
                }
                else
                {
                    var studentResponse = new StudentResponse
                    {
                        StudentId = request,
                        Name = null,
                        Major = null,
                        Year = null,
                        GPA = null,
                        Status = "error",
                        Message = "Student not found"
                    };
                    string json = JsonSerializer.Serialize(studentResponse);
                    byte[] data = Encoding.UTF8.GetBytes(json);
                    await stream.WriteAsync(data, 0, data.Length);
                    Console.WriteLine("Sent students to client.");
                    break;
                }
            }

            client.Close();
        }
        }
        catch
        {
        }
    }
    private static void InitializeSampleData()
    {
        students.AddRange(new[]
            {
                new Student { Studentid = "S001", Name = "John Smith", Major = "Computer Science", Year = 3, GPA = 3.5 },
                new Student { Studentid = "S002", Name = "Jane Doe", Major = "Business Administration,", Year = 2, GPA = 3.8 },
                new Student { Studentid = "S003", Name = "Bob Johnson", Major = "Electrical Engineering", Year = 4, GPA = 3.2 },
                new Student { Studentid = "S004", Name = "Alice Brown", Major = "Mathematics", Year = 1, GPA = 3.9 },
                new Student { Studentid = "S005", Name = "Charlie Brown", Major = "Physics", Year = 3, GPA = 3.4 }
            });
    }
}
    