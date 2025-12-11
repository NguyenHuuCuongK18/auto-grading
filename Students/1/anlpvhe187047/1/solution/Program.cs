using System.Net.Sockets;
using System.Net;
using Q1;
using System.Text.Json;
using System.Text;

public class Program
{
    public static async Task Main(string[] args)
    {
        TcpListener server = null;
        try
        {
            IPAddress ipAddress = IPAddress.Parse("127.0.0.1");
            int port = 4000;
            server = new TcpListener(ipAddress, port);
            server.Start();
            Console.WriteLine("Student Information Server is running on 127.0.0.1:4000");
            while (true)
            {
                TcpClient client = await server.AcceptTcpClientAsync();
                Console.WriteLine($"Client connected from {ipAddress} : {port}");
                Task.Run(() => HandleClientAsync(client));
            }
        }
        catch (Exception ex2)
        {
            Exception ex = ex2;
            Console.WriteLine("Server error: " + ex.Message);
        }
        finally
        {
            server?.Stop();
        }
    }
    private static async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[1024];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            if (!string.IsNullOrEmpty(request))
            {
                var students = (from ep in DataStore.Students
                                where ep.StudentId == request.ToString()
                                select new { ep.StudentId, ep.Name, ep.Major, ep.Year,ep.GPA }).ToList();
                string jsonResponse = JsonSerializer.Serialize(students, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                byte[] responseBytes = Encoding.UTF8.GetBytes(jsonResponse);
                await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                await stream.FlushAsync();
                Console.WriteLine($"Sent students for student ");
            }
            else
            {
                string error = JsonSerializer.Serialize(new
                {
                    Error = "Invalid employee ID"
                });
                byte[] errorBytes = Encoding.UTF8.GetBytes(error);
                await stream.WriteAsync(errorBytes, 0, errorBytes.Length);
                await stream.FlushAsync();
            }
        }
        catch (Exception ex2)
        {
            Exception ex = ex2;
            Console.WriteLine("Client error: " + ex.Message);
        }
        finally
        {
            client.Close();
        }
    }
}