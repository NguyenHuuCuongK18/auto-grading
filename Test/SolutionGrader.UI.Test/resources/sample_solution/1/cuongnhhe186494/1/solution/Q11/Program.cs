using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Sockets;
using System.Text;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

string ipAddress = configuration["IpAddress"]!;
int port = int.Parse(configuration["Port"]!);

TcpListener listener = new TcpListener(IPAddress.Parse(ipAddress), port);
listener.Start();

Console.WriteLine("Waiting for a connection from client");

while (true)
{
    TcpClient client = listener.AcceptTcpClient();
    var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
    Console.WriteLine($"Client connected");

    NetworkStream stream = client.GetStream();

    try
    {
        byte[] buffer = new byte[1024];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);

        if (bytesRead == 0)
        {
            Console.WriteLine("Client disconnected");
            client.Close();
            continue;
        }

        string input = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();

        // === SIMPLE VALIDATION: MUST BE A X B ===
        var parts = input.Split(' ');
        if (parts.Length != 3)
        {
            Console.WriteLine("Error");
            client.Close();
            continue;
        }

        if (!int.TryParse(parts[0], out int A) ||
            !int.TryParse(parts[2], out int B))
        {
            Console.WriteLine("Error");
            client.Close();
            continue;
        }

        string op = parts[1];

        double result = op switch
        {
            "+" => A + B,
            "-" => A - B,
            "*" => A * B,
            "/" => B == 0 ? double.NaN : (double)A / B,
            _ => double.NaN
        };

        if (double.IsNaN(result))
        {
            Console.WriteLine("Error");
            client.Close();
            continue;
        }

        // Format with 2 decimals by truncating
        double formatted = Math.Truncate(result * 100) / 100;

        byte[] resultBytes = Encoding.UTF8.GetBytes(formatted.ToString());
        stream.Write(resultBytes, 0, resultBytes.Length);
    }
    catch
    {
        Console.WriteLine("Error");
        client.Close();
    }

    Console.WriteLine("Client disconnected");
}
