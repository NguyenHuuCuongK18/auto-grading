using System;
using System.Collections.Generic;

namespace Project11.Models;

public partial class Book
{
    public int BookId { get; set; }

    public string Title { get; set; } = null!;

    public int? PublicationYear { get; set; }

    public int? GenreId { get; set; }

    public virtual ICollection<BookCopy> BookCopies { get; set; } = new List<BookCopy>();

    public virtual Genre? Genre { get; set; }

    public virtual ICollection<Author> Authors { get; set; } = new List<Author>();
}
