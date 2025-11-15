using System;
using System.Collections.Generic;

namespace Project11.Models;

public partial class BookCopy
{
    public int CopyId { get; set; }

    public int BookId { get; set; }

    public string Status { get; set; } = null!;

    public virtual Book Book { get; set; } = null!;

    public virtual ICollection<BorrowHistory> BorrowHistories { get; set; } = new List<BorrowHistory>();
}
