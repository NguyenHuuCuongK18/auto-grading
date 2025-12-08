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
#     fptuxaes/network-monitor:latest
#
# CRITICAL DESIGN:
#   - Captures on loopback (-i lo) to catch localhost traffic inside unified container
#   - NO port filtering - captures ALL traffic to detect student mistakes
#   - Packet-buffered mode (-U) writes immediately, safe if container crashes
#   - Uses Alpine for minimal image size (with retry logic for package installation)
#
# Use the lightest base image possible
FROM alpine:latest

# Install tcpdump (no cache to keep image small)
# Add retry logic to handle transient TLS errors with Alpine package repositories
RUN for i in 1 2 3 4 5; do \
        apk add --no-cache tcpdump && break || \
        (echo "Retry $i failed, waiting..." && sleep 10); \
    done && \
    apk list | grep tcpdump || (echo "tcpdump installation failed" && exit 1)

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
