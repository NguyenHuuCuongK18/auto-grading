using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Pcap
{
    public class RequestResponseLog
    {
        public string RequestPath { get; set; }
        public string RequestTimestamp { get; set; }
        public string ResponseTimestamp { get; set; }
        public int ResponseStatusCode { get; set; }
        public string Status { get; set; }
        public double Duration { get; set; }
    }
}
