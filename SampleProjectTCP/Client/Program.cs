using Client;
using Microsoft.Extensions.Configuration;
using System.Net.Sockets;
using System.Text;
using System.Xml.Serialization;

// Đọc cấu hình từ appsettings.json
IConfiguration config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

string ipAddress = config["IpAddress"]!;
int port = int.Parse(config["Port"]!);

while (true)
{
    Console.Write("Enter customer name (or press 0 to exit): ");
    string? name = Console.ReadLine();

    if (name == "0")
        break;

    if (string.IsNullOrWhiteSpace(name))
    {
        Console.WriteLine("Invalid input! Please enter a valid name.");
        continue;
    }

    try
    {
        // Kết nối TCP đến server
        using TcpClient client = new TcpClient();
        client.Connect(ipAddress, port);
        using NetworkStream stream = client.GetStream();

        // Gửi tên khách hàng
        byte[] data = Encoding.UTF8.GetBytes(name);
        stream.Write(data, 0, data.Length);

        // Nhận XML từ server
        byte[] buffer = new byte[8192];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        string xmlData = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        XmlSerializer serializer = new XmlSerializer(typeof(UserDto));
        using var reader = new StringReader(xmlData);
        var user = (UserDto?)serializer.Deserialize(reader);

        // Kiểm tra dữ liệu
        if (user == null || string.IsNullOrEmpty(user.FullName))
        {
            Console.WriteLine("User not found in the database.");
            continue;
        }

        // Hiển thị thông tin user
        //Console.WriteLine($"Name: {user.FullName}");
        //Console.WriteLine($"Gender: {user.Gender}");
        //Console.WriteLine($"Address: {user.Address}");

        //if (user.Orders != null && user.Orders.Any())
        //{
        //    foreach (var order in user.Orders)
        //    {
        //        Console.WriteLine($"Order #{order.Id} - Date: {order.OrderDate:yyyy-MM-dd} - Status: {order.Status}");
        //        foreach (var detail in order.Details ?? new List<OrderDetailDto>())
        //        {
        //            Console.WriteLine($"   + {detail.ProductName} - Qty: {detail.Quantity}");
        //        }
        //    }
        //}
        //else
        //{
        //    Console.WriteLine("\nNo orders found for this user.");
        //}
        Console.WriteLine(Helper.Stringify<UserDto>(user));
    }
    catch (Exception ex)
    {
        Console.WriteLine("Server is not running.Please try again later");
    }
}
