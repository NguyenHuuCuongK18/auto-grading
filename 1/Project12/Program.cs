using Microsoft.Extensions.Configuration;
using Project12;
using System.Net.Http;
using System.Text.Json;
using Project12.Models;

// Tương đương với cấu trúc dữ liệu JSON trả về từ Server
// Cần khớp với object ẩn danh trong Server



class Program
{
    private static readonly HttpClient client = new HttpClient();

    static async Task Main(string[] args)
    {
        // 1. Đọc cấu hình từ appsettings.json
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        string ipAddress = config["IpAddress"];
        string port = config["Port"];
        string baseUrl = $"{ipAddress}:{port}/";

        Utils.StartMessage();
        while (true)
        {
            Utils.Prompt();
            string input = Console.ReadLine();

            if (string.IsNullOrEmpty(input))
            {
                Console.WriteLine("Exiting client application...");
                break;
            }

            if (!int.TryParse(input, out int genreId))
            {
                Console.WriteLine("Invalid input! Please enter a valid integer.");
                continue;
            }

            try
            {
                // Thực hiện GET request
                var response = await client.GetAsync($"{baseUrl}books/{genreId}");

                if (response.IsSuccessStatusCode)
                {
                    string jsonResponse = await response.Content.ReadAsStringAsync();

                    // Deserialize JSON sang List<BookDetailDto>
                    var books = JsonSerializer.Deserialize<List<BookDto>>(
                        jsonResponse,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );

                    if (books != null && books.Any())
                    {
                        foreach (var book in books)
                        {
                            Console.WriteLine(Utils.Stringify(book));
                        }
                    }
                    else
                    {
                        Console.WriteLine($"No books found by genre ID {genreId}");
                    }
                }
                else
                {
                    // Xử lý lỗi HTTP (ví dụ: 404 Not Found, 400 Bad Request)
                    Console.WriteLine($"\n Server returned an error: {response.StatusCode} - {response.ReasonPhrase}");
                    // Lỗi 404 có thể xảy ra nếu Genre ID không tồn tại (tùy thuộc vào cách Server xử lý)
                }
            }
            catch (HttpRequestException ex)
            {
                // Lỗi kết nối (Server không chạy hoặc URL sai)
                Console.WriteLine("Server is not running. Please try again later.");
            }
            catch (JsonException)
            {
                // Lỗi khi Deserialize JSON (định dạng phản hồi sai)
                Console.WriteLine("\n\u274C Error parsing server response (Invalid JSON format).\n");
            }

        }
    }
}