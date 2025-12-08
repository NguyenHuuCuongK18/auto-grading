using System.Net.Sockets;
using System.Text.Json;
using System.Text;

public class Program
{
    static void Main(string[] args)
    {
        string server = "127.0.0.1";
        int port = 4000;
        ConnectServer(server, port);

    }

    private static void ConnectServer(string server, int port)
    {
        try
        {
            TcpClient client = new TcpClient(server, port);
            NetworkStream stream = null;
            while (true)
            {
                Console.WriteLine("Enter Student ID (or press Enter to exit): ");
                string message = Console.ReadLine();
                if (string.IsNullOrEmpty(message))
                {
                    return;
                }
                if(message.Length != 3 ) 
                {
                    Console.WriteLine("Invalid StudentId");
                }
                byte[] data = Encoding.UTF8.GetBytes(message);
                stream = client.GetStream();
                stream.Write(data, 0, data.Length);

                byte[] buffer = new byte[1024];
                int bytes = stream.Read(buffer, 0, buffer.Length);
                string responseData = Encoding.UTF8.GetString(buffer, 0, bytes);
                Console.WriteLine(responseData);

                ShowServerResponse(message, responseData);
            }
        }
        catch (SocketException)
        {
            Console.WriteLine(" server is not running. Please try again later.");
        }
    }

    private static void ShowServerResponse(string message , string responseData)
    {
        if (string.IsNullOrEmpty(responseData))
        {
            Console.WriteLine($"Book with ID {message} does not exist.");
        }
        try
        {
            var record = JsonSerializer.Deserialize<ServerResponse>(responseData);



            if (record == null)
            {
                Console.WriteLine($"No borrower records found for Book ID {message}.");
                return;
            }

            Console.WriteLine($"Borrower History for Book ID: {message}");
            foreach (var r in record.BorrowerRecords)
            {
                Console.WriteLine($"Student ID: {r.StudentId}");
                Console.WriteLine($"Name: {r.Name}");
                Console.WriteLine($"Major: {r.Major}");
                Console.WriteLine($"Year: {r.Year}");
                Console.WriteLine($"GPA: {r.GPA}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }
    }

    public class ServerResponse
    {
        public List<Student> BorrowerRecords { get; set; }
    }
    public class Student
    {
        public string StudentId { get; set; }
        public string Name { get; set; }
        public string Major {  get; set; }
        public int Year { get; set; }
        public decimal GPA { get; set; }
    }
}
