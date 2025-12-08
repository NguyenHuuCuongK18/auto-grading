# Network Monitor Container for Sidecar Pattern
#
# This container runs tcpdump to capture network traffic on the loopback interface
# when attached to a unified container via --net=container.
#
# BUILD COMMAND:
#   docker build -t fptuxaes/network-monitor:latest -f NetworkMonitor.Dockerfile .
#
# CRITICAL DESIGN:
#   - Captures on loopback (-i lo) to catch localhost traffic inside unified container
#   - NO port filtering - captures ALL traffic to detect student mistakes
#   - Packet-buffered mode (-U) writes immediately, safe if container crashes
#   - Uses Debian (Alpine has persistent TLS issues with package repositories in CI)
#
# Use Debian slim for reliability (Alpine has TLS certificate issues)
FROM debian:bullseye-slim

# Install tcpdump (minimal dependencies, reliable package repository)
RUN apt-get update && \
    apt-get install -y --no-install-recommends tcpdump && \
    rm -rf /var/lib/apt/lists/*

# Create a directory for the output files
WORKDIR /data

# Define a volume so you don't forget to mount it
VOLUME ["/data"]

# ENTRYPOINT ensures 'tcpdump' is always the executable
ENTRYPOINT ["tcpdump"]

# CMD sets the default arguments:
# -i lo  : Listen on Loopback (CRITICAL for your Fat Container)
# -w ... : Save to file named 'capture.pcap'
# -U     : 'Packet-Buffered' mode (writes to file immediately, safer if container crashes)
CMD ["-i", "lo", "-U", "-w", "capture.pcap"]
