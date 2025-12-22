using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Q1Client
{
    public class BorrowerRecord
    {
        public string ReaderID { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Email { get; set; } = "";
        public DateTime BorrowDate { get; set; }
        public DateTime? ReturnDate { get; set; }
        public string Status { get; set; } = "";
    }

    public class ServerResponse
    {
        public bool BookExists { get; set; }
        public string BookTitle { get; set; } = "";
        public List<BorrowerRecord> BorrowerRecords { get; set; } = new();
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            while (true)
            {
                Console.Write("Enter Book ID (or press Enter to exit): ");
                string? input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                {
                    Console.WriteLine("Goodbye! Book tracking client is shutting down.");
                    break;
                }

                if (!int.TryParse(input, out int bookId) || bookId <= 0)
                {
                    Console.WriteLine("Invalid input! Please enter a valid Book ID (positive integer).");
                    Console.WriteLine();
                    continue;
                }

                ServerResponse? response = null;

                try
                {
                    using TcpClient client = new TcpClient();
                    client.Connect("127.0.0.1", 4000);

                    using NetworkStream stream = client.GetStream();
                    byte[] sendBytes = Encoding.UTF8.GetBytes(bookId.ToString());
                    stream.Write(sendBytes, 0, sendBytes.Length);

                    using MemoryStream ms = new MemoryStream();
                    byte[] buffer = new byte[1024];
                    int bytesRead;
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ms.Write(buffer, 0, bytesRead);
                    }

                    string json = Encoding.UTF8.GetString(ms.ToArray());

                    response = JsonSerializer.Deserialize<ServerResponse>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch
                {
                    Console.WriteLine("Book tracking server is not running. Please try again later.");
                    Console.WriteLine();
                    continue;
                }

                if (response == null)
                {
                    Console.WriteLine("Received invalid response from server.");
                    Console.WriteLine();
                    continue;
                }


                if (!response.BookExists)
                {
                    Console.WriteLine($"Book with ID {bookId} does not exist.");
                    Console.WriteLine();
                    continue;
                }

                if (response.BorrowerRecords == null || response.BorrowerRecords.Count == 0)
                {
                    Console.WriteLine($"No borrower records found for Book ID {bookId}.");
                    Console.WriteLine();
                    continue;
                }

                Console.WriteLine($"=== Borrower History for Book ID: {bookId} ===");

                foreach (var record in response.BorrowerRecords)
                {
                    Console.WriteLine($"Reader ID: {record.ReaderID}");
                    Console.WriteLine($"Full Name: {record.FullName}");
                    Console.WriteLine($"Email: {record.Email}");
                    Console.WriteLine($"Borrow Date: {record.BorrowDate:yyyy-MM-dd}");
                    Console.WriteLine($"Return Date: {(record.ReturnDate.HasValue ? record.ReturnDate.Value.ToString("yyyy-MM-dd") : "Not returned yet")}");
                    Console.WriteLine($"Status: {record.Status}");
                    Console.WriteLine("---");
                }

                Console.WriteLine();
            }
        }
    }
}