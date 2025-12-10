using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Project12
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var builder = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            IConfiguration configuration = builder.Build();

            string ipAddress = configuration["IpAddress"];
            int port = int.Parse(configuration["Port"]);

            Utils.PromptInput();
            string input = Console.ReadLine();
            TcpClient client = new TcpClient();
            client.Connect(ipAddress, port);

            NetworkStream stream = client.GetStream();
            // set a read timeout so Read won't block indefinitely
            stream.ReadTimeout = 5000;

            StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };
            StreamReader reader = new StreamReader(stream);

            // Gửi dữ liệu
            writer.WriteLine(input);

            // Read available data without waiting for a terminating newline
            char[] buffer = new char[4096];
            int charsRead = 0;
            try
            {
                charsRead = reader.Read(buffer, 0, buffer.Length);
            }
            catch (IOException)
            {
                // Read timed out or other IO error; treat as no data
                charsRead = 0;
            }

            string result = charsRead > 0 ? new string(buffer, 0, charsRead) : string.Empty;
            Console.WriteLine(result);
            client.Close();
            Utils.PromptInput();
            input = Console.ReadLine();

        }
    }
}