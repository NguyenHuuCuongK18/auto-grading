# Network Monitor Container for Docker Internal Networking
#
# This container runs tcpdump to capture network traffic between student containers
# when using Docker internal networking mode (no port mappings).
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
#     --network auto-grading-network \
#     --cap-add=NET_ADMIN --cap-add=NET_RAW \
#     -v /path/to/output:/capture \
#     fptuxaes/network-monitor:latest \
#     tcpdump -i any -w /capture/network_capture.pcap "tcp port 8000"
#
# The grading system will automatically create and manage this container when
# UseDockerInternalNetworking = true in DockerGradingConfig.
#
FROM alpine:latest

# Install tcpdump for packet capture
RUN apk add --no-cache tcpdump

# Create directory for capture files
RUN mkdir -p /capture

WORKDIR /capture

# Default command - will be overridden by grading system
# Format: tcpdump -i any -w /capture/traffic.pcap "tcp port {PORT}"
CMD ["tcpdump", "-i", "any", "-w", "/capture/traffic.pcap"]
