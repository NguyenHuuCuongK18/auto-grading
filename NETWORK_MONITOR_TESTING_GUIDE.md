# Network Monitor Container Testing Guide

## Problem Statement
Students are failing and Docker network monitor containers are not capturing any traffic. All pcap files are 0 bytes.

## Root Cause
The network monitor containers were missing the required Linux capabilities (`NET_ADMIN` and `NET_RAW`) needed for tcpdump to capture packets.

## Solution
Added `--cap-add=NET_ADMIN --cap-add=NET_RAW` to the network monitor container creation command in `DockerGradingService.cs`.

## Architecture

### Docker Internal Networking Mode (UseDockerInternalNetworking = true)

```
┌──────────────────────────────────────────────────────────────┐
│ Docker Bridge Network (auto-grading-network)                 │
│                                                               │
│  ┌─────────────────┐      ┌─────────────────┐               │
│  │  ag-server-     │      │  ag-client-     │               │
│  │  student123     │◄─────┤  student123     │               │
│  │  Port: 8000     │      │  connects to:   │               │
│  │                 │      │  ag-server-*:   │               │
│  └─────────────────┘      │  8000           │               │
│          ▲                └─────────────────┘               │
│          │                                                   │
│          │ captures traffic                                 │
│          │                                                   │
│  ┌───────┴─────────┐                                        │
│  │ ag-monitor-     │                                        │
│  │ student123      │                                        │
│  │                 │                                        │
│  │ tcpdump -i any  │                                        │
│  │ --cap-add NET_  │                                        │
│  │ ADMIN/NET_RAW   │                                        │
│  └─────────────────┘                                        │
│         │                                                    │
└─────────┼────────────────────────────────────────────────────┘
          │
          ├─ Writes to: /capture/network_capture.pcap
          │
          └─ Mounted from host: {StudentDir}/NetworkCapture/
```

**Key Points:**
- NO port mappings (`-p`) - eliminates Docker NAT proxy behavior
- Client connects via container name (direct container-to-container)
- Traffic never reaches Windows host
- Monitor container runs tcpdump on the same Docker network
- **Requires NET_ADMIN and NET_RAW capabilities for packet capture**

## Testing the Fix

### Prerequisites
1. Network monitor image must be built:
   ```bash
   cd /home/runner/work/auto-grading/auto-grading
   docker build -t fptuxaes/network-monitor:latest -f DockerImage/NetworkMonitor.Dockerfile DockerImage/
   ```

2. Or for testing, use Alpine with inline tcpdump install (slower but works):
   ```bash
   docker run -d --name test-monitor \
     --network test-net \
     --cap-add=NET_ADMIN --cap-add=NET_RAW \
     -v /tmp/capture:/capture \
     alpine:latest \
     sh -c "apk add --no-cache tcpdump && tcpdump -i any -w /capture/test.pcap 'tcp port 8000'"
   ```

### Manual Test Procedure

#### Step 1: Create Test Network
```bash
docker network create test-grading-net
```

#### Step 2: Create Capture Directory
```bash
mkdir -p /tmp/test-capture
```

#### Step 3: Start Network Monitor Container
```bash
docker run -d --name test-monitor \
  --network test-grading-net \
  --cap-add=NET_ADMIN \
  --cap-add=NET_RAW \
  -v /tmp/test-capture:/capture \
  fptuxaes/network-monitor:latest \
  tcpdump -i any -w /capture/network_capture.pcap "tcp port 8000"
```

**Expected**: Container starts successfully

#### Step 4: Start Test Server
```bash
docker run -d --name test-server \
  --network test-grading-net \
  alpine:latest \
  sh -c "apk add --no-cache netcat-openbsd && while true; do echo 'HTTP/1.1 200 OK\n\nHello' | nc -l -p 8000; done"
```

#### Step 5: Send Test Traffic
```bash
docker run --rm --name test-client \
  --network test-grading-net \
  alpine:latest \
  sh -c "apk add --no-cache netcat-openbsd && echo 'GET / HTTP/1.1' | nc test-server 8000"
```

#### Step 6: Stop Monitor and Check Results
```bash
# Stop monitor to flush pcap file
docker stop test-monitor

# Check pcap file
ls -lh /tmp/test-capture/network_capture.pcap

# If file size > 0 bytes, SUCCESS!
# Verify packet count
docker run --rm -v /tmp/test-capture:/data alpine:latest \
  sh -c "apk add --no-cache tcpdump && tcpdump -r /data/network_capture.pcap -n | wc -l"
```

**Expected Results:**
- ✓ File `/tmp/test-capture/network_capture.pcap` exists
- ✓ File size > 0 bytes (should be several hundred bytes minimum)
- ✓ Packet count > 0 (typically 3+ packets for TCP handshake)

#### Step 7: Cleanup
```bash
docker rm -f test-monitor test-server test-client
docker network rm test-grading-net
rm -rf /tmp/test-capture
```

### Automated Test Script

See `/tmp/test_network_monitor.sh` for automated testing (requires stable network connection).

## Verifying the Fix in Production

### Before Running Grading
1. Ensure network monitor image is built:
   ```bash
   docker images | grep network-monitor
   ```
   
   If not found, build it:
   ```bash
   cd /home/runner/work/auto-grading/auto-grading
   docker build -t fptuxaes/network-monitor:latest -f DockerImage/NetworkMonitor.Dockerfile DockerImage/
   ```

### After Running Grading
1. Check pcap file sizes:
   ```bash
   find Run_Log -name "network_capture.pcap" -ls
   ```
   
2. All files should be > 0 bytes

3. For detailed verification, read a pcap file:
   ```bash
   # Install tcpdump if needed
   docker run --rm -v $(pwd)/Run_Log/1/student/STUDENTCODE/NetworkCapture:/data \
     alpine:latest \
     sh -c "apk add --no-cache tcpdump && tcpdump -r /data/network_capture.pcap -n"
   ```

## Troubleshooting

### Issue: Empty PCAP Files (0 bytes)

**Symptoms:**
- All pcap files are 0 bytes
- No errors in grading logs

**Possible Causes:**
1. **Missing capabilities** (FIXED in this PR)
   - Solution: Ensure `--cap-add=NET_ADMIN --cap-add=NET_RAW` is in docker run command
   
2. **Monitor container failed to start**
   - Check: `docker logs ag-monitor-{studentCode}`
   - Look for: tcpdump errors, permission denied, interface not found
   
3. **Wrong network**
   - Ensure monitor container is on same network as student containers
   - Check: `docker network inspect auto-grading-network`
   
4. **Wrong port filter**
   - tcpdump filter must match the actual port being used
   - Example: If server listens on 8000, filter must be `tcp port 8000`

### Issue: Monitor Container Exits Immediately

**Check logs:**
```bash
docker logs ag-monitor-{studentCode}
```

**Common errors:**
- `tcpdump: no suitable device found` - Check network attachment
- `Permission denied` - Missing NET_ADMIN/NET_RAW capabilities
- `command not found` - Image not built correctly

### Issue: Network Monitor Image Not Found

**Solution:**
```bash
cd /home/runner/work/auto-grading/auto-grading
docker build -t fptuxaes/network-monitor:latest -f DockerImage/NetworkMonitor.Dockerfile DockerImage/
```

## Technical Details

### Why NET_ADMIN and NET_RAW are Required

Docker containers run in restricted security contexts by default. Packet capture operations require elevated privileges:

1. **NET_ADMIN**: Allows network interface configuration and monitoring
   - Required for tcpdump to open network interfaces
   - Allows setting promiscuous mode on interfaces
   
2. **NET_RAW**: Allows creation of raw sockets
   - Required for tcpdump to capture packets at the link layer
   - Allows reading raw packet data from the network

Without these capabilities:
- tcpdump runs but cannot open capture interfaces
- Silently fails or produces permission denied errors
- Result: Empty pcap files (0 bytes)

### libpcap Dependency

**Question**: Does the network monitor container need libpcap installed?

**Answer**: NO - When you install tcpdump via `apk add tcpdump`, Alpine's package manager automatically installs libpcap as a dependency. You do NOT need to explicitly add it to the Dockerfile.

```dockerfile
# This line installs BOTH tcpdump AND libpcap
RUN apk add --no-cache tcpdump
```

### Container vs Host Monitoring

**Container-based (Current Approach - Docker Internal Networking)**:
- ✓ Captures direct container-to-container traffic
- ✓ No Docker NAT proxy interference
- ✓ Accurate representation of student code
- ✗ Requires NET_ADMIN/NET_RAW capabilities
- ✗ One monitor container per student

**Host-based (SharpPcap on Windows)**:
- ✓ Single capture instance for all students
- ✓ No special container capabilities needed
- ✗ Only works with port mapping mode
- ✗ Captures Docker NAT proxy behavior (ghost SYN-ACK)
- ✗ Traffic must go through vEthernet adapter

## References

- Docker capabilities documentation: https://docs.docker.com/engine/reference/run/#runtime-privilege-and-linux-capabilities
- tcpdump man page: https://www.tcpdump.org/manpages/tcpdump.1.html
- Alpine Linux packages: https://pkgs.alpinelinux.org/packages
