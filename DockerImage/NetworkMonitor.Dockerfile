# Network Monitor Container for Sidecar Pattern
#
# This container uses SharpPcap/PacketDotNet for real-time packet capture,
# matching the EXACT behavior of MiddlewareSniffPort used for testkit generation.
#
# BUILD COMMAND (from repository root):
#   docker build -t fptuxaes/network-monitor:latest -f DockerImage/NetworkMonitor.Dockerfile .
#
# CRITICAL DESIGN (matching MiddlewareSniffPort):
#   - Uses SharpPcap for real-time packet capture (not tcpdump)
#   - Uses PacketDotNet for packet parsing  
#   - Captures on loopback (lo) to catch localhost traffic inside unified container
#   - Writes structured JSON output (not raw PCAP) for reliable parsing
#   - Flag format: comma-separated (e.g., "FIN, ACK") 
#   - Flag order: FIN, SYN, RST, PSH, ACK, URG
#
# ADVANTAGES OVER TCPDUMP:
#   - Real-time parsing - no buffering/timing issues
#   - Structured output - no PCAP parsing needed
#   - Exact flag format match with testkit expectations
#   - No accumulation issues between test cases
#
# MULTI-STAGE BUILD for smaller image size

# Stage 1: Build the .NET application
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files from Lib/NetworkMonitorSidecar
COPY Lib/NetworkMonitorSidecar/NetworkMonitorSidecar.csproj Lib/NetworkMonitorSidecar/
RUN dotnet restore Lib/NetworkMonitorSidecar/NetworkMonitorSidecar.csproj -r linux-x64

# Copy source and publish
COPY Lib/NetworkMonitorSidecar/ Lib/NetworkMonitorSidecar/
RUN dotnet publish Lib/NetworkMonitorSidecar/NetworkMonitorSidecar.csproj \
    -c Release -r linux-x64 --self-contained -o /app/publish

# Stage 2: Runtime image
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-bookworm-slim

# Install libpcap (required for SharpPcap packet capture)
RUN apt-get update && \
    apt-get install -y --no-install-recommends libpcap0.8 && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy published application from build stage
COPY --from=build /app/publish .

# Create data directory for output files
RUN mkdir -p /data
VOLUME ["/data"]

# Set entrypoint to the network monitor application
ENTRYPOINT ["/app/NetworkMonitorSidecar"]

# Default arguments:
# - Port 4000 (can be overridden)
# - Output to /data/packets.jsonl (JSON lines format for easy parsing)
CMD ["4000", "/data/packets.jsonl"]
