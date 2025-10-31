using System;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Project12.Models;
using Project12; // dùng lại Utils đã upload

class Program
{
    static async Task Main(string[] args)
    {
        // 1. Đọc cấu hình
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        string ipAddress = config["IPAddress"];
        string port = config["Port"];
        string baseUrl = $"{ipAddress}{port}/";

        using var client = new HttpClient();
        while (true)
        {
            Console.Write("Please enter integer number : ");
            string? input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("Exiting client application. . .");
                break;
            }

            if (!int.TryParse(input, out int genreId))
            {
                Console.WriteLine("Invalid input! Please enter a valid integer");
                continue;
            }

            string requestUrl = $"{baseUrl}books/{genreId}";

            try
            {
                HttpResponseMessage response = await client.GetAsync(requestUrl);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine("Server is not running. Please try again later.");
                    continue;
                }

                string json = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var books = JsonSerializer.Deserialize<List<BookDto>>(json, options);

                if (books == null || books.Count == 0)
                {
                    Console.WriteLine($"No books found by genre ID {genreId}");
                }
                else
                {
                    foreach (var book in books)
                    {
                        Console.WriteLine(Utils.Stringify(book));
                    }
                }
            }
            catch (HttpRequestException)
            {
                Console.WriteLine("Server is not running. Please try again later.");
            }
        }
    }
}
