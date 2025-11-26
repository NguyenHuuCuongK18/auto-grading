using System;
using System.Collections.Generic;

namespace Project11.Models;

public partial class Borrower
{
    public int BorrowerId { get; set; }

    public string Name { get; set; } = null!;

    public string? Email { get; set; }

    public virtual ICollection<BorrowHistory> BorrowHistories { get; set; } = new List<BorrowHistory>();
}
