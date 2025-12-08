# Network Monitor Container for Docker Internal Networking
#
# This container runs tcpdump to capture network traffic between student containers
# when using the sidecar pattern (attached via --net=container).
#
# BUILD COMMAND:
#   docker build -t fptuxaes/network-monitor:latest -f NetworkMonitor.Dockerfile .
#
# RUN REQUIREMENTS:
#   The container MUST be run with --cap-add=NET_ADMIN --cap-add=NET_RAW
#   These capabilities are REQUIRED for tcpdump to capture packets.
#   Without them, tcpdump will fail silently and produce empty pcap files.
#
# Example run command (automatically done by grading system):
#   docker run -d --name ag-monitor-student123 \
#     --net=container:ag-unified-student123 \
#     --cap-add=NET_ADMIN --cap-add=NET_RAW \
#     -v /path/to/output:/capture \
#     fptuxaes/network-monitor:latest \
#     tcpdump -i lo -w /capture/network_capture.pcap
#
# Using Debian slim instead of Alpine to avoid package repository TLS issues
FROM debian:bullseye-slim

# Install tcpdump for packet capture
RUN apt-get update && \
    apt-get install -y tcpdump && \
    rm -rf /var/lib/apt/lists/*

# Create directory for capture files
RUN mkdir -p /capture

WORKDIR /capture

# Default command - will be overridden by grading system
# Format: tcpdump -i lo -w /capture/traffic.pcap
CMD ["tcpdump", "-i", "lo", "-w", "/capture/traffic.pcap"]
