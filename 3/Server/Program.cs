using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Server;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Serialization;

//server TCP

IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
string ipAddress = config["IpAddress"]!;
int port = int.Parse(config["Port"]!);

var listener = new TcpListener(IPAddress.Parse(ipAddress), port);
listener.Start();
Console.WriteLine($"Server is running");
Console.WriteLine("Waiting for client connections...");
while (true)
{
    TcpClient client = listener.AcceptTcpClient();
    Task.Run(() => HandleClient(client));
}



void HandleClient(TcpClient client)
{
    using NetworkStream stream = client.GetStream();
    var dbContext = new TcpServerClientContext();
    byte[] buffer = new byte[1024];
    bool disconnected = false;

    try
    {
        while (!disconnected)
        {
            int byteCount;
            try
            {
                byteCount = stream.Read(buffer, 0, buffer.Length);
            }
            catch (IOException)
            {
                disconnected = true;
                break;
            }
            catch (ObjectDisposedException)
            {
                disconnected = true;
                break;
            }

            if (byteCount == 0)
            {
                disconnected = true;
                break;
            }

            string name = Encoding.UTF8.GetString(buffer, 0, byteCount).Trim();
            Console.WriteLine($"Client request: {name}");

            var user = dbContext.Users
                .Include(u => u.Orders).ThenInclude(o => o.OrderDetails).ThenInclude(od => od.Product)
                .FirstOrDefault(u => u.FullName.ToLower() == name.ToLower());

            var xml = new XmlSerializer(typeof(UserDto));
            var dto = user == null
                ? new UserDto()
                : new UserDto
                {
                    Id = user.Id,
                    FullName = user.FullName,
                    Address = user.Address,
                    Gender = user.Gender == 1 ? "Male" : "Female",
                    Orders = user.Orders.Select(o => new OrderDto
                    {
                        Id = o.Id,
                        OrderDate = o.OrderDate,
                        Status = o.Status == 1 ? "Done" : o.Status == 2 ? "Cancel" : "Processing",
                        Details = o.OrderDetails.Select(d => new OrderDetailDto
                        {
                            ProductName = d.Product.Name,
                            Quantity = d.Quantity
                        }).ToList()
                    }).ToList()
                };

            try
            {
                xml.Serialize(stream, dto);
            }
            catch (Exception)
            {
                // Nếu client đóng khi đang gửi, chỉ ngắt mà không log thêm
                disconnected = true;
                break;
            }
        }
    }
    catch (Exception ex)
    {
    }
    finally
    {
        if (!disconnected)
        {
            Console.WriteLine($"Client disconnected.");
        }
    }
}

