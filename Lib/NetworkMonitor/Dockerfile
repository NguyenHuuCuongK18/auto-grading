# Network Monitor - SharpPcap-based packet capture
#
# This container runs a .NET application using SharpPcap/PacketDotNet for real-time
# packet capture, matching the exact behavior of MiddlewareSniffPort.
#
# BUILD COMMAND (from repository root):
#   docker build -t fptuxaes/network-monitor:latest -f Lib/NetworkMonitor/Dockerfile .
#
# OR use the main Dockerfile in DockerImage folder:
#   docker build -t fptuxaes/network-monitor:latest -f DockerImage/NetworkMonitor.Dockerfile .
#
# CRITICAL DESIGN (matching MiddlewareSniffPort):
#   - Uses SharpPcap for real-time packet capture (not tcpdump)
#   - Uses PacketDotNet for packet parsing
#   - Captures on loopback (-i lo) to catch localhost traffic
#   - Writes structured JSON output (not raw PCAP)
#   - Flag format: comma-separated (e.g., "FIN, ACK")
#   - Flag order: FIN, SYN, RST, PSH, ACK, URG
#
# MULTI-STAGE BUILD:
#   Stage 1: Build the .NET application
#   Stage 2: Create minimal runtime image with libpcap

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY Lib/NetworkMonitor/NetworkMonitor.csproj Lib/NetworkMonitor/
RUN dotnet restore Lib/NetworkMonitor/NetworkMonitor.csproj -r linux-x64

# Copy source code and publish
COPY Lib/NetworkMonitor/ Lib/NetworkMonitor/
RUN dotnet publish Lib/NetworkMonitor/NetworkMonitor.csproj \
    -c Release -r linux-x64 --self-contained -o /app/publish

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-bookworm-slim

# Install libpcap (required for SharpPcap)
RUN apt-get update && \
    apt-get install -y --no-install-recommends libpcap0.8 && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy published application
COPY --from=build /app/publish .

# Create data directory for output
RUN mkdir -p /data
VOLUME ["/data"]

# Set the entrypoint to our network monitor
ENTRYPOINT ["/app/NetworkMonitor"]

# Default arguments: port 4000, output to /data/packets.jsonl
CMD ["4000", "/data/packets.jsonl"]
