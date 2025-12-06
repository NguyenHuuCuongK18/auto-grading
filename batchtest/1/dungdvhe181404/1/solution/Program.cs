using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using static System.Reflection.Metadata.BlobBuilder;



    

    public class ServerResponse
    {
        public bool StudentExist { get; set; }
        public List<Student> students { get; set; } = new List<Student>();
    }

   
    public class Student
{
    public string StudentId { get; set; }
    public string Name { get; set; }
    public string Major {  get; set; }
    public int Year { get; set; }
    public decimal  GPA { get; set; }
    
}

    public class Server
    {
        private static List<Student> students = new List<Student>();

        static async Task Main(string[] args)
        {
            InitializeSampleData();

            TcpListener server = new TcpListener(IPAddress.Parse("127.0.0.1"), 4000);
            server.Start();

            Console.WriteLine("Student Information Server is running on 127.0.0.1:4000");
            Console.WriteLine("Waiting for client connections...");

            while (true)
            {
                try
                {
                    TcpClient client = await server.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClient(client));
                    Console.WriteLine($"Client connected from {"127.0.0.1"}:{client}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error accepting client: {ex.Message}");
                }
            }
        }

        private static async Task HandleClient(TcpClient client)
        {
            NetworkStream stream = client.GetStream();

            try
            {
                
                byte[] buffer = new byte[1024];
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                string studentID = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                Console.WriteLine($"Received Student ID: {studentID}");

                if (int.TryParse(studentID, out int studentId))
                {
                    var response = GetStudentResponse(studentId);
                    string jsonResponse = JsonSerializer.Serialize(response);

                    byte[] responseBytes = Encoding.UTF8.GetBytes(jsonResponse);
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length);

                    if (response.StudentExist)
                    {
                        var student = response.students.FirstOrDefault(b => b.StudentId.Equals(studentId));
                        Console.WriteLine($"studentId: {student.StudentId},\n" +
                            $"name: {student.Name},\n" +
                            $"major: {student.Major},\n" +
                            $"gpa: {student.GPA},\n" +
                            $"status: success,\n" +
                            $"messsage: Student information retrieved successfully");
                    }
                    else
                    {
                        Console.WriteLine($"studentId: {studentId},\n" +
                        $"name: null,\n" +
                        $"major: null,\n" +
                        $"gpa: null,\n" +
                        $"status: error,\n" +
                        $"messsage: Student not found");
                }
                }
                else
                {
                    var response = new ServerResponse { StudentExist = false, students = new List<Student>() };
                    string emptyResponse = JsonSerializer.Serialize(response);
                    byte[] responseBytes = Encoding.UTF8.GetBytes(emptyResponse);
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling client: {ex.Message}");
            }
            finally
            {
                client.Close();
            }
        }

        private static ServerResponse GetStudentResponse(int studentId)
        {
            var response = new ServerResponse();
           var student = students.FirstOrDefault(b => b.StudentId.Equals(studentId));
            response.StudentExist = student != null;

            if (!response.StudentExist)
            {
                return response; 
            }
            return response;
        }

        private static void InitializeSampleData()
        {
            
            students.AddRange(new[]
            {
                new Student { StudentId="S001" , Name="John Smith", Major="Computer Science", Year=3, GPA=3.5M },
                new Student { StudentId="S001" , Name="John Smith", Major="Computer Science", Year=3, GPA=3.8M },
                new Student { StudentId="S001" , Name="John Smith", Major="Computer Science", Year=3, GPA=3.2M },
                new Student { StudentId="S001" , Name="John Smith", Major="Computer Science", Year=3, GPA=3.9M },
                new Student { StudentId="S001" , Name="John Smith", Major="Computer Science", Year=3, GPA=3.4M }
            });



            Console.WriteLine("Sample data initialized successfully!");
            Console.WriteLine($"Students: {students.Count}");
        }
    }

