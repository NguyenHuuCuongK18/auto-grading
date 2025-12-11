using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal class program
{
    static void Main(string[] args)
    {
        // 1. Setup server
        int port = 3000;
        TcpListener server = new TcpListener(IPAddress.Parse("127.0.0.1"), port);
        server.Start();
        Console.WriteLine($"Calculator Server is running on 127.0.0.1:{port}");
        Console.WriteLine($"Wating for client connetions...");

        while (true)
        {
            // 2. Accept incoming client connection
            TcpClient client = server.AcceptTcpClient();

            // 3. Handle client in a separate thread
            Thread clientThread = new Thread(HandleClient);
            clientThread.Start(client);
        }
        //while (true)
        //{
        //    TcpClient client = server.AcceptTcpClient();
        //    Console.WriteLine("Client is connecting...");

        //    NetworkStream stream = client.GetStream();

        //    //Nhận thông tin từ client
        //    byte[] data = new byte[1024];
        //    int byteNumber = stream.Read(data, 0, data.Length);
        //    String message = Encoding.UTF8.GetString(data);
        //    Console.WriteLine("Client:" + message);
        //}
    }

    static void HandleClient(object obj)
    {
        TcpClient client = (TcpClient)obj;
        NetworkStream stream = client.GetStream();

        Console.WriteLine($"Client connected from {client.Client.RemoteEndPoint}");

        byte[] buffer = new byte[1024];
        int byteCount;

        try
        {
            while ((byteCount = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                // 4. Read message from client
                string received = Encoding.UTF8.GetString(buffer, 0, byteCount);
                Console.WriteLine($"Recived from {client.Client.RemoteEndPoint}");

                List<string> data = PartCalculator(received);
                string reply = JsonSerializer.Serialize(new CalculationResponse
                {
                    Expression = received,
                    Result = null,
                    Status = "error",
                    Message = "Invalid expression"
                });
                try
                {
                    int number1 = int.Parse(data[0].Trim());
                    int number2 = int.Parse(data[1].Trim());

                    var res = PartCalculator(number1, number2, received);
                    if (res != null)
                    {
                        reply = JsonSerializer.Serialize(new CalculationResponse
                        {
                            Expression = received,
                            Result = res,
                            Status = "success",
                            Message = "Calculation completed successfully"
                        });
                    }
                }
                catch (Exception)
                {
                }

                Console.WriteLine($"Send to {client.Client.RemoteEndPoint} : {reply}");
                // 5. Send reply back
                //string reply = $"Server received: {received}";
                byte[] response = Encoding.UTF8.GetBytes(reply);
                stream.Write(response, 0, response.Length);
            }
        }
        catch (Exception ex)
        {
        }
        finally
        {
            Console.WriteLine($"Client from {client.Client.RemoteEndPoint} disconnetecd");
            stream.Close();
            client.Close();
        }
    }

    static List<string> PartCalculator(string data)
    {
        var list = new List<string>();

        if (data.Contains("+"))
        {
            list = data.Split('+').ToList();
        }
        else if (data.Contains("-"))
        {
            list = data.Split('-').ToList();
        }
        else if (data.Contains("*"))
        {
            list = data.Split('*').ToList();
        }
        else if (data.Contains("/"))
        {
            list = data.Split('/').ToList();
        }

        return list;
    }

    static int? PartCalculator(int number1, int number2, string data)
    {
        int? res = null;

        try
        {
            if (data.Contains("+"))
            {
                res = number1 + number2;
            }
            else if (data.Contains("-"))
            {
                res = number1 - number2;
            }
            else if (data.Contains("*"))
            {
                res = number1 * number2;
            }
            else if (data.Contains("/"))
            {
                res = number1 / number2;
            }
        }
        catch (Exception)
        {

            return res;
        }
        return res;
    }
}

class CalculationResponse
{
    public string Expression { get; set; } = "";
    public int? Result { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}