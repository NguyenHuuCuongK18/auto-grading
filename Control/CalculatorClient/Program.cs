using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

class CalculatorClient
{
    private const string HOST = "127.0.0.1";
    private const int PORT = 3000;

    static async Task Main(string[] args)
    {
        var client = new CalculatorClient();
        await client.RunAsync();
    }

    public async Task RunAsync()
    {
        Console.WriteLine("Calculator Client");
        Console.WriteLine("=================");
        Console.WriteLine($"Connecting to server at {HOST}:{PORT}");
        Console.WriteLine("Enter mathematical expressions (e.g., '5 + 3')");
        Console.WriteLine("Press Enter (empty input) to exit\n");

        while (true)
        {
            Console.Write("Enter expression: ");
            string? input = Console.ReadLine();

            // Exit if user presses Enter without input
            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("Exiting...");
                break;
            }

            await SendExpressionAsync(input);
        }
    }

    private async Task SendExpressionAsync(string expression)
    {
        try
        {
            using (TcpClient client = new TcpClient())
            {
                await client.ConnectAsync(HOST, PORT);

                using (NetworkStream stream = client.GetStream())
                {
                    // Send expression to server
                    byte[] data = Encoding.UTF8.GetBytes(expression);
                    await stream.WriteAsync(data, 0, data.Length);

                    // Receive response from server
                    byte[] buffer = new byte[1024];
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // Parse and display JSON response
                    DisplayResponse(response);
                }
            }
        }
        catch (SocketException)
        {
            Console.WriteLine("Error: Server is not running. Please try again later.\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}\n");
        }
    }

    private void DisplayResponse(string jsonResponse)
    {
        try
        {
            var response = JsonSerializer.Deserialize<CalculationResponse>(jsonResponse);

            if (response == null)
            {
                Console.WriteLine("Error: Invalid response from server\n");
                return;
            }

            Console.WriteLine("\n--- Server Response ---");
            Console.WriteLine($"Expression: {response.Expression}");
            Console.WriteLine($"Status: {response.Status}");

            if (response.Status == "success")
            {
                Console.WriteLine($"Result: {response.Result}");
            }
            else
            {
                Console.WriteLine($"Error Message: {response.Message}");
            }

            Console.WriteLine($"Message: {response.Message}");
            Console.WriteLine("----------------------\n");
        }
        catch (JsonException)
        {
            Console.WriteLine("Error: Failed to parse server response\n");
            Console.WriteLine($"Raw response: {jsonResponse}\n");
        }
    }
}

class CalculationResponse
{
    public string Expression { get; set; } = "";
    public int? Result { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}