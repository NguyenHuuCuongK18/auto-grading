namespace Client
{

    public class UserDto
    {
        public int Id { get; set; }
        public string? FullName { get; set; }
        public string? Address { get; set; }
        public string? Gender { get; set; }
        public List<OrderDto>? Orders { get; set; }
    }

    public class OrderDto
    {
        public int Id { get; set; }
        public DateTime OrderDate { get; set; }
        public string? Status { get; set; }
        public List<OrderDetailDto>? Details { get; set; }
    }

    public class OrderDetailDto
    {
        public string? ProductName { get; set; }
        public int Quantity { get; set; }
    }
}
