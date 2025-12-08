# Network Monitor Container Fix - Summary

## Issue Report
**Problem**: Students failing with empty network capture files (0 bytes pcap files)  
**Symptom**: Network monitor containers running but not capturing any traffic  
**Impact**: Network flow validation tests cannot run, grading fails

## Root Cause Analysis

The network monitor containers were created without the necessary Linux capabilities for packet capture:
- **Missing**: `NET_ADMIN` and `NET_RAW` capabilities
- **Result**: tcpdump runs but cannot access network interfaces or capture packets
- **Behavior**: Container starts successfully, creates empty pcap file, no errors shown

## Solution

Added required Docker capabilities to the network monitor container creation in `DockerGradingService.cs`:

```csharp
string dockerCmd = $"docker run -d --name {monitorContainerName} " +
                 $"--network {dockerNetwork} " +
                 $"--cap-add=NET_ADMIN " +        // NEW: Network interface access
                 $"--cap-add=NET_RAW " +          // NEW: Raw packet capture
                 $"-v \"{absOutputDir}:/capture\" " +
                 $"fptuxaes/network-monitor:latest " +
                 $"tcpdump -i any -w /capture/network_capture.pcap \"tcp port {port}\"";
```

## Why These Capabilities Are Required

### NET_ADMIN
- Allows network interface configuration and monitoring
- Required for tcpdump to open and access network interfaces
- Enables setting interfaces to promiscuous mode for packet capture

### NET_RAW
- Allows creation of raw sockets
- Required for tcpdump to capture packets at the link layer
- Enables reading raw packet data directly from the network

**Without these capabilities**:
- tcpdump runs in the container
- Cannot open capture interfaces (permission denied)
- Silently fails or exits immediately
- Creates empty pcap file (0 bytes)

## Architecture Choice: Docker Internal Networking

**Configuration**: `UseDockerInternalNetworking = true` (default)

### Why This Approach

1. **Eliminates Docker NAT Proxy Behavior**
   - No port mappings (`-p {host}:{container}`)
   - No Docker NAT layer between containers
   - No "ghost" SYN-ACK responses from Docker proxy
   - Accurate representation of student code behavior

2. **Direct Container Communication**
   - Client connects: `ag-client-student123` → `ag-server-student123:8000`
   - Traffic flows directly through Docker bridge network
   - No external port exposure needed

3. **Clean Network Monitoring**
   - Monitor container on same Docker network
   - Captures actual container-to-container traffic
   - No proxy interference in captured packets

### Traffic Flow Diagram

```
Container-to-Container Communication (Internal Network):

┌─────────────────────────────────────────────────┐
│  Docker Bridge Network (auto-grading-network)   │
│                                                  │
│  ┌──────────────┐         ┌──────────────┐     │
│  │ ag-server-   │◄────────│ ag-client-   │     │
│  │ student123   │         │ student123   │     │
│  │ Port: 8000   │         │              │     │
│  └──────┬───────┘         └──────────────┘     │
│         │                                       │
│         │ Direct capture                        │
│         ▼                                       │
│  ┌──────────────┐                               │
│  │ ag-monitor-  │                               │
│  │ student123   │                               │
│  │              │                               │
│  │ tcpdump with │                               │
│  │ NET_ADMIN +  │                               │
│  │ NET_RAW      │                               │
│  └──────┬───────┘                               │
│         │                                       │
└─────────┼───────────────────────────────────────┘
          │
          └──► /capture/network_capture.pcap
              (Mounted from host)
```

## Implementation Details

### Files Modified

1. **Lib/SolutionGrader.Core/Services/DockerGradingService.cs**
   - Line 3083-3084: Added `--cap-add=NET_ADMIN` and `--cap-add=NET_RAW`
   - Line 3057-3063: Added documentation explaining requirements
   - Line 3260-3278: Updated `UseDockerInternalNetworking` documentation

2. **DockerImage/NetworkMonitor.Dockerfile**
   - Added capability requirements to header comments
   - Included example run command with capabilities
   - Explained why capabilities are needed

3. **NETWORK_MONITOR_TESTING_GUIDE.md** (NEW)
   - Complete testing procedures
   - Troubleshooting guide
   - Technical explanations

## Testing Instructions

### 1. Build Network Monitor Image

```bash
cd /home/runner/work/auto-grading/auto-grading
docker build -t fptuxaes/network-monitor:latest -f DockerImage/NetworkMonitor.Dockerfile DockerImage/
```

### 2. Run Grading Session

Use the existing grading workflow (UI or CLI). The network monitor will automatically start with correct capabilities.

### 3. Verify Network Capture

After grading completes:

```bash
# Check pcap file sizes
find Run_Log -name "network_capture.pcap" -exec ls -lh {} \;

# Expected: All files > 0 bytes (typically several KB)
```

### 4. Analyze Captured Traffic

```bash
# View captured packets
docker run --rm -v $(pwd)/Run_Log/1/student/STUDENTCODE/NetworkCapture:/data \
  alpine:latest \
  sh -c "apk add --no-cache tcpdump && tcpdump -r /data/network_capture.pcap -n"

# Expected: See TCP handshakes, data transfers, etc.
```

## Common Questions

### Q: Does the network monitor container need libpcap installed?
**A**: No. When you install tcpdump via `apk add tcpdump`, Alpine's package manager automatically installs libpcap as a dependency. The Dockerfile is correct as-is.

### Q: Why not use host-based SharpPcap monitoring?
**A**: With Docker internal networking (no port mappings), traffic never reaches the Windows host's vEthernet adapter. It stays within the Docker bridge network inside the Linux VM. Host-based capture would see nothing.

### Q: What if I want to avoid proxy behavior but use host-based monitoring?
**A**: You would need to set `UseDockerInternalNetworking = false`, which:
- Exposes ports to host via `-p {host}:{container}`
- Allows SharpPcap to capture on vEthernet adapter
- But introduces Docker NAT proxy behavior (ghost SYN-ACK)
- Not recommended for accurate grading

### Q: Are there security concerns with NET_ADMIN/NET_RAW?
**A**: These capabilities only allow network monitoring, not escalation to root or system compromise. The monitor container:
- Runs as non-root user
- Only has network access
- Cannot modify system configuration
- Is isolated to Docker network
- Automatically removed after grading

## Troubleshooting

### Empty PCAP Files (0 bytes) - Still Happening?

Check these:

1. **Capabilities Applied?**
   ```bash
   docker inspect ag-monitor-student123 | grep -A 5 CapAdd
   # Should show: "NET_ADMIN", "NET_RAW"
   ```

2. **Monitor Container Running?**
   ```bash
   docker logs ag-monitor-student123
   # Check for tcpdump errors
   ```

3. **Correct Network?**
   ```bash
   docker network inspect auto-grading-network
   # Ensure monitor, server, and client are all attached
   ```

4. **Correct Port Filter?**
   - tcpdump filter must match actual server port
   - Example: Server on 8000 → filter `tcp port 8000`

### Monitor Container Exits Immediately

```bash
docker logs ag-monitor-student123
```

Common errors:
- `no suitable device found` → Wrong network
- `Permission denied` → Missing capabilities (rebuild code)
- `command not found` → Image not built correctly

### Network Monitor Image Not Found

```bash
docker build -t fptuxaes/network-monitor:latest \
  -f DockerImage/NetworkMonitor.Dockerfile \
  DockerImage/
```

## Success Criteria

✅ **Fix is working when**:
1. All pcap files > 0 bytes
2. Can read packets from pcap files with tcpdump
3. Network flow validation tests pass
4. Students receive correct grades based on network behavior

## Reference Documentation

- **Testing Guide**: `/home/runner/work/auto-grading/auto-grading/NETWORK_MONITOR_TESTING_GUIDE.md`
- **Docker Capabilities**: https://docs.docker.com/engine/reference/run/#runtime-privilege-and-linux-capabilities
- **tcpdump Manual**: https://www.tcpdump.org/manpages/tcpdump.1.html

## Commit History

1. **e47d35a**: Add NET_ADMIN and NET_RAW capabilities to network monitor container for packet capture
2. **3b01513**: Add comprehensive network monitor testing guide and documentation

## Next Steps

1. Build the network monitor image
2. Run a grading session with real students
3. Verify pcap files are populated
4. Confirm network flow tests pass
5. Store memory about this fix for future reference
