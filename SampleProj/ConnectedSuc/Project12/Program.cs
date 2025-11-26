using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using System.Net.Sockets;
using System.Text;

var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();
string ipAddress = configuration["IpAddress"]!;
int port = int.Parse(configuration["Port"]!);

while (true)
{
    Console.Write("Enter operation (format as A X B):");
    string? input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input))
    {
        Console.WriteLine("Input can not be empty");
        continue;
    }

    if (input.Trim().ToLower() == "exit")
    {
        break;
    }

    var errors = new List<string>();

    var lengthInput = input.Split(" ");
    if (lengthInput.Length != 3)
    {
        errors.Add("A X B is not correct format");
    }

    var tokens = input.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    string a = tokens.Length > 0 ? tokens[0] : "";
    string op = tokens.Length > 1 ? tokens[1] : "";
    string b = tokens.Length > 2 ? tokens[2] : "";



    bool aIsInt = int.TryParse(a, out int A);
    bool bIsInt = int.TryParse(b, out int B);
    if (!aIsInt || !bIsInt)
    {
        errors.Add("A or B is not an integer");
    }

    if (op != "+" && op != "-" && op != "*" && op != "/")
    {
        errors.Add("X is not a valid operator (+,-,*,/)");
    }

    if (op == "/" && bIsInt && B == 0)
    {
        errors.Add("Divide by 0");
    }

    if (errors.Count > 0)
    {
        foreach (var err in errors)
            Console.WriteLine(err);
        continue;
    }

    try
    {
        using TcpClient client = new TcpClient();
        client.Connect(ipAddress, port);

        NetworkStream stream = client.GetStream();
        byte[] data = Encoding.UTF8.GetBytes(input);
        stream.Write(data, 0, data.Length);

        byte[] buffer = new byte[1024];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        Console.WriteLine($"{response}");

    }
    catch
    {
        Console.WriteLine("Server is not running");
    }
}