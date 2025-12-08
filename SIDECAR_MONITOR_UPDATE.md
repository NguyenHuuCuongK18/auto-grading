# Network Monitor Sidecar Approach - Implementation Update

## Issue Identified
After implementing NET_ADMIN/NET_RAW capabilities, the user identified that:
1. Network monitor containers were not being cleaned up correctly after grading
2. The bridge network approach (using `--network {dockerNetwork}`) has limitations due to network switching isolation

## Solution: Sidecar Method (Option A)

### What Changed

**Before (Bridge Network Approach)**:
```bash
docker run -d --name ag-monitor-student123 \
  --network auto-grading-network \      # ← Attached to bridge network
  --cap-add=NET_ADMIN \
  --cap-add=NET_RAW \
  -v /path/to/output:/capture \
  fptuxaes/network-monitor:latest \
  tcpdump -i any -w /capture/network_capture.pcap "tcp port 8000"
```

**Problem**: Bridge network acts like a physical switch, sending packets only to destination ports. Monitor on a different "port" never receives packets destined for server or client.

**After (Sidecar Approach - Option A)**:
```bash
docker run -d --name ag-monitor-student123 \
  --net=container:ag-server-student123 \ # ← Shares server's network namespace
  --cap-add=NET_ADMIN \
  --cap-add=NET_RAW \
  -v /path/to/output:/capture \
  fptuxaes/network-monitor:latest \
  tcpdump -i any -w /capture/network_capture.pcap "tcp port 8000"
```

**Solution**: Monitor "parasites" on server container, sharing its exact network interface (eth0). Sees everything server sends/receives.

## Why Sidecar is Better

### 1. Full Traffic Visibility
- **Bridge Network**: Monitor is isolated by switching; only sees broadcast traffic
- **Sidecar**: Monitor shares server's `eth0`, sees ALL unicast traffic to/from server

### 2. Platform Independent
- **Bridge Network**: Requires complex interface detection, varies by Docker backend
- **Sidecar**: Works identically on Linux, Windows (WSL2), and Mac (Hyper-V)

### 3. Simplified Configuration
- **Bridge Network**: Must detect which bridge interface (br-xxxx, docker0, vethXXX)
- **Sidecar**: No interface detection needed - always uses server's interface

### 4. Guaranteed Packet Capture
- **Bridge Network**: Depends on bridge configuration, promiscuous mode, BPF filters
- **Sidecar**: Guaranteed visibility - monitor IS the server from network perspective

### 5. Automatic Lifecycle Management
- **Bridge Network**: Monitor independent, must be manually cleaned up
- **Sidecar**: When server stops, monitor automatically stops (shared namespace)

## Architecture Diagram

### Sidecar Monitoring (Current Implementation)

```
┌─────────────────────────────────────────────────────────────────┐
│ Docker Bridge Network (auto-grading-network)                    │
│                                                                  │
│  ┌──────────────────────────────────────────┐                  │
│  │  Server Container (ag-server-student123) │                  │
│  │  ┌────────────────────────────────────┐  │                  │
│  │  │ Network Namespace                  │  │                  │
│  │  │                                    │  │                  │
│  │  │  • Student Server App (Port 8000)  │  │                  │
│  │  │  • eth0 Interface                  │  │◄─────────────┐   │
│  │  │                                    │  │              │   │
│  │  │  • Monitor Sidecar (tcpdump)       │  │              │   │
│  │  │    Shares same eth0                │  │              │   │
│  │  │    Sees all traffic                │  │              │   │
│  │  └────────────────────────────────────┘  │              │   │
│  └──────────────────────────────────────────┘              │   │
│                      ▲                                      │   │
│                      │                                      │   │
│                      │ Direct connection                    │   │
│                      │                                      │   │
│  ┌───────────────────┴──────────────┐                      │   │
│  │  Client Container                │                      │   │
│  │  (ag-client-student123)          │──────────────────────┘   │
│  │                                   │  Connects to             │
│  │  Connects to:                     │  ag-server-student123:   │
│  │  ag-server-student123:8000        │  8000                    │
│  └───────────────────────────────────┘                          │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘

Monitor Output: /capture/network_capture.pcap (mounted from host)
```

**Key Points**:
1. Monitor runs in **same network namespace** as server (via `--net=container`)
2. Monitor and server share the **same eth0 interface**
3. Monitor sees **ALL packets** going to/from server's eth0
4. No switching isolation - monitor IS the server from network perspective
5. When server stops, monitor automatically stops (shared lifecycle)

## Implementation Details

### Code Changes

**File**: `Lib/SolutionGrader.Core/Services/DockerGradingService.cs`

**Method**: `StartNetworkMonitorContainerAsync`
- **Old**: `dockerNetwork` parameter
- **New**: `serverContainerName` parameter
- **Change**: Uses `--net=container:{serverContainerName}` instead of `--network {dockerNetwork}`

**Call Site** (line ~367):
```csharp
await StartNetworkMonitorContainerAsync(
    monitorContainerName,
    config.CodeContainerInternalPort,
    monitorOutputDir,
    serverContainer); // Pass server container name for sidecar attachment
```

### Cleanup Improvements

Enhanced `StopNetworkMonitorContainerAsync` with:
1. **Finally block**: Ensures monitor is ALWAYS removed, even if errors occur
2. **Better logging**: Shows sidecar-specific status messages
3. **Lifecycle awareness**: Notes that monitor may auto-stop when server is removed

## Benefits Summary

| Aspect | Bridge Network | Sidecar (Option A) |
|--------|---------------|-------------------|
| **Visibility** | Limited (switching isolation) | Complete (shared namespace) |
| **Platform** | Linux-specific | Cross-platform |
| **Setup** | Complex (interface detection) | Simple (container name only) |
| **Reliability** | Depends on bridge config | Guaranteed |
| **Cleanup** | Manual | Automatic (with server) |
| **Debugging** | Difficult (invisible packets) | Easy (sees everything) |

## Testing Verification

After implementing sidecar approach:

1. **Monitor starts attached to server**:
   ```bash
   docker inspect ag-monitor-student123 | grep NetworkMode
   # Should show: "NetworkMode": "container:ag-server-student123"
   ```

2. **Pcap files populated**:
   ```bash
   find Run_Log -name "network_capture.pcap" -exec ls -lh {} \;
   # All files should be > 0 bytes
   ```

3. **Monitor shares server's network stack**:
   ```bash
   # From inside monitor container
   docker exec ag-monitor-student123 ip addr
   # Should show same interfaces as server container
   ```

4. **Cleanup verification**:
   ```bash
   # After grading
   docker ps -a | grep ag-monitor
   # Should show no monitor containers (all cleaned up)
   ```

## Troubleshooting

### Issue: Monitor Container Fails to Start

**Symptoms**: Error message about network namespace
**Cause**: Server container not running when monitor tries to attach
**Solution**: Ensure server container is created BEFORE monitor (already in code)

### Issue: Empty Pcap Files Still

**Check**:
1. Monitor has NET_ADMIN/NET_RAW capabilities
2. Monitor is attached to correct server container
3. tcpdump filter matches actual port
4. Server container is actually receiving traffic

### Issue: Monitor Not Cleaned Up

**Check**:
```bash
docker ps -a | grep ag-monitor-
```

If orphaned monitors exist:
```bash
docker rm -f $(docker ps -a | grep ag-monitor- | awk '{print $1}')
```

**Root Cause**: Error in `StopNetworkMonitorContainerAsync` preventing cleanup
**Solution**: Now uses `finally` block to ensure cleanup even with errors

## Migration Notes

**No user action required**:
- Change is transparent to existing workflows
- Same commands to build and run grading
- Same pcap output location
- Same network flow analysis

**Benefits users will see**:
- More reliable packet capture
- Better cleanup (no orphaned monitors)
- Works across all Docker backends
- Clearer logging about monitor lifecycle

## References

- **Original Comment**: @bstHoang's explanation of Option A (Sidecar Method)
- **Docker Docs**: https://docs.docker.com/engine/reference/run/#network-settings
- **Sidecar Pattern**: https://kubernetes.io/docs/concepts/workloads/pods/#how-pods-manage-multiple-containers

## Commit

This change is committed as: "Implement sidecar network monitoring approach (Option A) for better traffic visibility and cleanup"
