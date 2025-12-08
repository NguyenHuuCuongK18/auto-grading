#!/bin/bash
# Manual test to debug network monitor container

set -e

echo "========================================"
echo "Network Monitor Debug Test"
echo "========================================"
echo ""

# Clean up
echo "Cleaning up any existing containers..."
docker rm -f test-unified test-monitor 2>/dev/null || true

# Create output directory
OUTPUT_DIR="/tmp/network-monitor-test"
mkdir -p "$OUTPUT_DIR"

echo ""
echo "Step 1: Create unified test container..."
docker run -d --name test-unified \
    fptuxaes/aes-dotnet8-console:latest

echo "✓ Unified container created"
echo ""

echo "Step 2: Create network monitor container attached to unified container..."
docker run -d --name test-monitor \
    --net=container:test-unified \
    --cap-add=NET_ADMIN \
    --cap-add=NET_RAW \
    -v "$OUTPUT_DIR:/data" \
    fptuxaes/network-monitor:latest \
    -i lo -n -U -v -w /data/test_capture.pcap

echo "✓ Network monitor created"
echo ""

echo "Step 3: Wait for monitor to initialize..."
sleep 3

echo "Step 4: Check if monitor container is running..."
docker ps | grep test-monitor && echo "✓ Monitor is running" || echo "✗ Monitor is NOT running"

echo ""
echo "Step 5: Generate some loopback traffic in unified container..."
docker exec test-unified bash -c "
    # Install ping if needed (may not be available)
    apt-get update -qq 2>/dev/null || true
    apt-get install -y iputils-ping netcat-openbsd 2>/dev/null || true
    
    # Generate loopback traffic
    echo 'Generating loopback traffic...'
    nc -l 127.0.0.1 8080 &
    sleep 1
    echo 'TEST' | nc 127.0.0.1 8080 &
    sleep 2
    pkill nc || true
    
    echo 'Traffic generation complete'
"

echo ""
echo "Step 6: Check monitor logs..."
docker logs test-monitor 2>&1 | head -20

echo ""
echo "Step 7: Stop monitor to flush pcap buffer..."
docker stop test-monitor

echo ""
echo "Step 8: Check if pcap file was created on host..."
ls -lh "$OUTPUT_DIR/" || echo "Directory is empty"

if [ -f "$OUTPUT_DIR/test_capture.pcap" ]; then
    echo ""
    echo "✓ PCAP file created successfully!"
    echo "File size: $(stat -f%z "$OUTPUT_DIR/test_capture.pcap" 2>/dev/null || stat -c%s "$OUTPUT_DIR/test_capture.pcap") bytes"
    
    # Try to read the pcap file
    echo ""
    echo "Reading pcap file..."
    tcpdump -r "$OUTPUT_DIR/test_capture.pcap" -nn -c 10 2>/dev/null || echo "No packets in pcap file"
else
    echo ""
    echo "✗ PCAP file NOT found - debugging needed"
    echo ""
    echo "Checking Docker logs..."
    docker logs test-monitor
fi

echo ""
echo "Step 9: Cleanup..."
docker rm -f test-unified test-monitor 2>/dev/null || true

echo ""
echo "Test complete!"
