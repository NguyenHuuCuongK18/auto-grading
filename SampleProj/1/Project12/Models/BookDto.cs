using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Project12.Models
{
    public class BookDto
    {
        public int BookId { get; set; }
        public string Title { get; set; }
        public int PublicationYear { get; set; }
        public string GenreName { get; set; }
    }
}
