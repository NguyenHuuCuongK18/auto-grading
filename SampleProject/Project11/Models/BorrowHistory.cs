using System;
using System.Collections.Generic;

namespace Project11.Models;

public partial class BorrowHistory
{
    public int BorrowId { get; set; }

    public int CopyId { get; set; }

    public int BorrowerId { get; set; }

    public DateOnly BorrowDate { get; set; }

    public DateOnly? ReturnDate { get; set; }

    public virtual Borrower Borrower { get; set; } = null!;

    public virtual BookCopy Copy { get; set; } = null!;
}
