using System;
using System.Collections.Generic;

namespace Server;

public partial class User
{
    public int Id { get; set; }

    public string FullName { get; set; } = null!;

    public string? Address { get; set; }

    public int? Gender { get; set; }

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();
}
