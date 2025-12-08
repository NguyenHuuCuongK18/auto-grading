# Network Monitor Container for Sidecar Pattern
#
# This container runs tcpdump to capture network traffic on the loopback interface
# when attached to a unified container via --net=container.
#
# BUILD COMMAND:
#   docker build -t fptuxaes/network-monitor:latest -f NetworkMonitor.Dockerfile .
#
# RUN REQUIREMENTS:
#   - --cap-add=NET_ADMIN --cap-add=NET_RAW (REQUIRED for packet capture)
#   - --net=container:{unified-container-name} (attaches to student container's network namespace)
#   - -v {host-path}:/data (bind mount for pcap output)
#
# Example:
#   docker run -d --name ag-monitor-student123 \
#     --net=container:ag-unified-student123 \
#     --cap-add=NET_ADMIN --cap-add=NET_RAW \
#     -v /tmp/student123:/data \
#     fptuxaes/network-monitor:latest \
#     -w network_capture.pcap
#
# CRITICAL DESIGN:
#   - Captures on loopback (-i lo) to catch localhost traffic inside unified container
#   - NO port filtering - captures ALL traffic to detect student mistakes
#   - Packet-buffered mode (-U) writes immediately, safe if container crashes
#   - Uses Debian (not Alpine) to avoid package repository TLS issues
#
FROM debian:bullseye-slim

# Install tcpdump (minimal dependencies)
RUN apt-get update && \
    apt-get install -y --no-install-recommends tcpdump && \
    rm -rf /var/lib/apt/lists/*

# Create directory for capture files
RUN mkdir -p /data

# Define volume so users don't forget to mount
VOLUME ["/data"]

WORKDIR /data

# ENTRYPOINT ensures tcpdump is always the executable
# Grading system can override CMD to change output filename
ENTRYPOINT ["tcpdump"]

# CMD sets default arguments:
#   -i lo : Listen on Loopback (CRITICAL for Fat Container traffic)
#   -U    : Packet-buffered mode (writes immediately, safer if crash)
#   -w    : Output file (can be overridden by grading system)
CMD ["-i", "lo", "-U", "-w", "capture.pcap"]
