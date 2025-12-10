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
            // 1. Cấu hình Client: Đọc IpAddress và Port từ appsettings.json
            var builder = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            IConfiguration configuration = builder.Build();

            string ipAddress = configuration["IpAddress"];
            int port = int.Parse(configuration["Port"]);

            while (true)
            {
                // 2.1 Hiển thị nhắc lệnh
                Utils.PromptInput();
                string input = Console.ReadLine();

                // 3.1 Xử lý input trống
                if (string.IsNullOrEmpty(input))
                {
                    Console.WriteLine("Input can not be empty");
                    continue;
                }

                // 3.7 Xử lý lệnh exit
                if (input.Trim().ToLower() == "exit")
                {
                    break;
                }

                // Danh sách chứa các lỗi để hiển thị một lượt
                List<string> errorMessages = new List<string>();

                // --- BẮT ĐẦU LOGIC KIỂM TRA ĐA LỖI ---

                // Bước 1: Tách chuỗi "Nghiêm ngặt" (giữ nguyên khoảng trắng) để bắt lỗi Format
                // Ví dụ: " A + B" -> sẽ có phần tử rỗng ở đầu -> sai format
                string[] strictParts = input.Split(' ');

                // Bước 2: Tách chuỗi "Lỏng lẻo" (bỏ khoảng trắng thừa) để lấy dữ liệu kiểm tra Logic
                // Ví dụ: " A + B" -> lấy được ["A", "+", "B"] để kiểm tra tiếp xem A là số hay chữ
                string[] dataParts = input.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                // CHECK 1: Lỗi Format (3.4)
                if (strictParts.Length != 3)
                {
                    errorMessages.Add("A X B is not correct format");
                }

                // CHECK 2 & 3 & 4: Kiểm tra Logic (chỉ thực hiện nếu tách ra được 3 phần dữ liệu)
                if (dataParts.Length == 3)
                {
                    string strA = dataParts[0];
                    string strX = dataParts[1];
                    string strB = dataParts[2];

                    bool isAInt = int.TryParse(strA, out int a);
                    bool isBInt = int.TryParse(strB, out int b);

                    // 3.2 Lỗi không phải số nguyên
                    if (!isAInt || !isBInt)
                    {
                        errorMessages.Add("A or B is not an integer");
                    }

                    // 3.3 Lỗi toán tử không hợp lệ
                    string validOperators = "+-*/";
                    if (strX.Length != 1 || !validOperators.Contains(strX))
                    {
                        errorMessages.Add("X is not a valid operator (+,-,*,/)");
                    }

                    // 3.6 Lỗi chia cho 0
                    if (isBInt && strX == "/" && b == 0)
                    {
                        errorMessages.Add("Divide by 0");
                    }
                }
                // Trường hợp dataParts không đủ 3 phần (VD: nhập "A B"), lỗi format đã bắt ở trên rồi.

                // --- KẾT THÚC KIỂM TRA ---

                // 3.8 Nếu có bất kỳ lỗi nào trong danh sách -> In ra hết và quay lại nhập tiếp
                if (errorMessages.Count > 0)
                {
                    foreach (var error in errorMessages)
                    {
                        Console.WriteLine(error);
                    }
                    continue;
                }

                // 4. Gửi input tới Server (Chỉ chạy khi danh sách lỗi rỗng)
                TcpClient client = new TcpClient();
                try
                {
                    // 4.1 Kết nối tới server
                    client.Connect(ipAddress, port);

                    NetworkStream stream = client.GetStream();
                    StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };
                    StreamReader reader = new StreamReader(stream);

                    // Gửi dữ liệu
                    writer.WriteLine(input);

                    // 4.2 Nhận và hiển thị kết quả
                    string result = reader.ReadLine();
                    Console.WriteLine(result);
                }
                catch (SocketException)
                {
                    // 4.1 Server không hoạt động
                    Console.WriteLine("Server is not running");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
                finally
                {
                    // 5.1 Đóng kết nối
                    client.Close();
                }
            }
        }
    }
}