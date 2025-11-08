using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Project11.Models;
using System.Net;
using System.Text;
using System.Text.Json;

class Program
{
    static async Task Main(string[] args)
    {
        // 1. Đọc cấu hình từ appsettings.json
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        string ipAddress = config["IpAddress"];
        string port = config["Port"];
        string serverUrl = $"{ipAddress}:{port}/";

        // 2. Khởi tạo HttpListener
        var listener = new HttpListener();
        listener.Prefixes.Add(serverUrl);
        listener.Start();
        Console.WriteLine($"Server started.");
        while (true)
        {
            var context = await listener.GetContextAsync();
            var request = context.Request;
            var response = context.Response;

            var path = request.Url.AbsolutePath.ToLower();
            var method = request.HttpMethod;

            Console.WriteLine($"{method} {path}");

            using var db = new LibraryContext(); // EF DbContext

            // ===========================
            // 1. GET /books
            // ===========================
            if (path == "/books" && method == "GET")
            {
                var books = await db.Books
                                    .Include(b => b.Genre)
                                    .Select(b => new
                                    {
                                        b.BookId,
                                        b.Title,
                                        b.PublicationYear,
                                        Genre = new
                                        {
                                            b.Genre.GenreId,
                                            b.Genre.GenreName
                                        }
                                    }).ToListAsync();


                await WriteJsonResponse(response, books);
            }
            // ===========================
            // 1.5. GET /books/{genreId} (Lấy sách theo ID)
            // ===========================
            else if (path.StartsWith("/books/") && method == "GET")
            {
                // Lấy phần ID từ path: /books/1 -> "1"
                var idSegment = path.Substring("/books/".Length);

                if (int.TryParse(idSegment, out int genreId))
                {
                    // Lọc theo GenreId
                    var booksDto = await db.Books.Where(b => b.GenreId == genreId)
                                    .Include(b => b.Genre)
                                    .Select(b => new
                                    {
                                        b.BookId,
                                        b.Title,
                                        b.PublicationYear,
                                        b.Genre.GenreName
                                    }).ToListAsync();

                    await WriteJsonResponse(response, booksDto);
                }
            }
            // ===========================
            // 2. POST /books
            // ===========================
            else if (path == "/books" && method == "POST")
            {
                using var reader = new StreamReader(request.InputStream);
                var body = await reader.ReadToEndAsync();

                var newBook = JsonSerializer.Deserialize<Book>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (newBook != null && !string.IsNullOrWhiteSpace(newBook.Title))
                {
                    db.Books.Add(newBook);
                    await db.SaveChangesAsync();

                    response.StatusCode = 201;
                    await WriteJsonResponse(response, newBook);
                }
                else
                {
                    response.StatusCode = 400;
                }
            }
            // ===========================
            // 3. PUT /books/{id}
            // ===========================
            else if (path.StartsWith("/books/") && method == "PUT")
            {
                if (int.TryParse(path.Replace("/books/", ""), out int id))
                {
                    var book = await db.Books.FirstOrDefaultAsync(b => b.BookId == id);
                    if (book == null)
                    {
                        response.StatusCode = 404;
                    }
                    else
                    {
                        using var reader = new StreamReader(request.InputStream);
                        var body = await reader.ReadToEndAsync();

                        var updatedBook = JsonSerializer.Deserialize<Book>(body, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                        if (updatedBook != null)
                        {
                            if (!string.IsNullOrWhiteSpace(updatedBook.Title))
                                book.Title = updatedBook.Title;

                            if (updatedBook.PublicationYear.HasValue)
                                book.PublicationYear = updatedBook.PublicationYear;

                            if (updatedBook.GenreId.HasValue)
                                book.GenreId = updatedBook.GenreId;

                            await db.SaveChangesAsync();

                            response.StatusCode = 200;
                            await WriteJsonResponse(response, book);
                        }
                        else
                        {
                            response.StatusCode = 400;
                        }
                    }
                }
                else
                {
                    response.StatusCode = 400;
                }
            }
            // ===========================
            // 4. DELETE /books/{id}
            // ===========================
            else if (path.StartsWith("/books/") && method == "DELETE")
            {
                if (int.TryParse(path.Replace("/books/", ""), out int id))
                {
                    var book = await db.Books.FirstOrDefaultAsync(b => b.BookId == id);
                    if (book == null)
                    {
                        response.StatusCode = 404;
                    }
                    else
                    {
                        db.Books.Remove(book);
                        await db.SaveChangesAsync();

                        response.StatusCode = 200;
                        await WriteJsonResponse(response, new { message = "Deleted successfully" });
                    }
                }
                else
                {
                    response.StatusCode = 400;
                }
            }
            // ===========================
            // 5. GET /books/{id}
            // ===========================
            else if (path.StartsWith("/books/") && method == "GET")
            {
                if (int.TryParse(path.Replace("/books/", ""), out int id))
                {
                    var book = await db.Books
                        .Include(b => b.Genre)
                        .FirstOrDefaultAsync(b => b.BookId == id);

                    if (book == null)
                    {
                        response.StatusCode = 404;
                    }
                    else
                    {
                        response.StatusCode = 200;
                        await WriteJsonResponse(response, book);
                    }
                }
                else
                {
                    response.StatusCode = 400;
                }
            }
            else
            {
                response.StatusCode = 404;
            }

            response.Close();
        }
    }

    private static async Task WriteJsonResponse(HttpListenerResponse response, object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        var buffer = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json";
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
    }
}
