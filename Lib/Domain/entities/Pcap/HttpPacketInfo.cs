using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Domain.Entities.Pcap
{
    public class HttpPacketInfo
    {
        public DateTime Timestamp { get; set; }
        public DateTime? ResponseTimestamp { get; set; }
        public string SourceIP { get; set; }
        public string DestinationIP { get; set; }
        public int SourcePort { get; set; }
        public int DestinationPort { get; set; }
        public bool IsRequest { get; set; }

        public string StatusLine { get; set; }
        public string Headers { get; set; }
        public string Body { get; set; }

        public string RelativeUrl { get; set; }
        public int? StatusCode { get; set; }
        public int? ResponseStatusCode { get; set; }
        public string ResponseHeaders { get; set; }
        public string ResponseBody { get; set; }
    }
}
