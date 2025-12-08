# Docker Port Mapping Proxy Behavior - Root Cause Analysis

## Problem Statement

Network flow monitoring shows SYN-ACK and PSH-ACK responses on port 4000 even when the student's server exits immediately (e.g., "Hello World" program). This appears as "proxy behavior" or "ghost server" responding to client connections.

## Root Cause: Docker's Port Publishing NAT Proxy

When containers are created with port mapping `-p {host_port}:{container_port}`, Docker creates a **NAT proxy layer** on the host that:

1. **Listens on the host port** (e.g., 4000)
2. **Automatically accepts TCP connections** (responds with SYN-ACK)
3. **Attempts to forward** to the container's internal port
4. **Immediately closes** the connection if nothing is listening inside container (FIN-ACK)

This is **Docker's intended behavior** for published ports, not a bug.

## Reproduction Test

```bash
# Create container with port mapping but NO listener inside
docker run -d --name test-proxy -p 5555:5555 fptuxaes/aes-dotnet8-console:latest

# Try to connect from host
timeout 2 bash -c 'cat < /dev/null > /dev/tcp/localhost/5555'
# Result: CONNECTION ACCEPTED (Docker's NAT proxy responds)

# Check inside container
docker exec test-proxy netstat -tln | grep ':5555'
# Result: Nothing listening on 5555
```

**Outcome**: Connection is accepted by Docker's proxy even though no process is listening inside the container.

## Network Flow Analysis for Student dungtdhe186461

### Server Behavior
```
Hello, World!  × 6  (server runs and exits immediately, 6 test cases)
```

### Network Flow Pattern
```
Line 1-3:   SYN → SYN-ACK → ACK        (Docker proxy accepts connection)
Line 4-5:   FIN-ACK from server        (Docker proxy closes - no backend)
Line 6:     RST-ACK                    (Connection reset)

Line 7-9:   SYN → SYN-ACK → ACK        (Next test case, same pattern)
Line 10:    FIN-ACK from server        
Line 12:    PSH-ACK with data "S001"   (Client sends request)
Line 13:    ACK                        (Docker proxy ACKs data)
Line 14:    FIN-ACK                    (Client closes)
```

### What's Happening

1. **Student's server starts** → prints "Hello World" → **exits immediately**
2. **Container stays alive** (ENTRYPOINT keeps it running)
3. **Port mapping remains active** (`-p 4000:4000`)
4. **Golden client connects** → `host.docker.internal:4000`
5. **Docker's NAT proxy responds** with SYN-ACK (accepts connection)
6. **Proxy detects no listener** → sends FIN-ACK immediately
7. **Client sends data anyway** (PSH-ACK with "S001", "S123", etc.)
8. **Proxy ACKs the data** but has nowhere to forward it
9. **No response data** → Client gets empty response
10. **Test fails** with "Failed to parse server response"

## Why This Happens

### Docker Port Publishing Architecture

When you use `-p 4000:4000`:
```
Host Network Stack
       ↓
Docker NAT Proxy (userland proxy OR iptables NAT)
       ↓
Container Network Namespace
       ↓
Application (if listening)
```

The NAT proxy operates at the host level, independently of what's running inside the container.

### Relevant Docker Code

In `Lib/EnvironmentBuilder/dockercommand/DockerCommandExecutor.cs`:

```csharp
// Lines 359-361 and 407-409
string portMapping = dockerBase.HostPort > 0 && dockerBase.ContainerPort > 0
    ? $"-p {dockerBase.HostPort}:{dockerBase.ContainerPort} "
    : "";
```

This creates the NAT proxy that causes the "ghost server" behavior.

## Why NGINX Disabling Didn't Fix It

The Dockerfile correctly disables NGINX:
```dockerfile
RUN systemctl stop nginx 2>/dev/null || service nginx stop 2>/dev/null || true
RUN pkill -9 nginx 2>/dev/null || true
```

Tests confirm NGINX is NOT running:
```bash
docker exec container ps aux
# Only shows: tail -f /dev/null (PID 1)
```

The proxy behavior was NEVER from NGINX - it was always Docker's port publishing mechanism.

## Solution Options

### Option 1: Remove Port Mappings (Use Docker Internal Networking)

**Change client configuration** to connect via Docker's internal network:
```
Before: host.docker.internal:4000
After:  ag-server-{studentCode}:4000
```

**Pros:**
- No Docker proxy interference
- True container-to-container communication
- Network flow shows actual student code behavior

**Cons:**
- Requires changing golden client configuration
- May not match real-world deployment scenarios
- Network monitor must capture bridge network traffic

### Option 2: Accept Docker's Proxy Behavior

**Keep port mappings** but understand that SYN-ACK is expected Docker behavior.

**Adjust grading logic**:
- SYN-ACK from Docker proxy is normal
- Focus on checking for response data, not just connection
- Test fails if server sends no data (empty response)

**Pros:**
- No architecture changes needed
- Matches real-world port forwarding scenarios
- Simpler implementation

**Cons:**
- Network flow will always show SYN-ACK (even for broken code)
- Cannot distinguish "server exited" from "server silent" via network alone

### Option 3: Add Process Validation

**Check if application is actually listening** inside container:
```bash
docker exec container sh -c "ss -ltn | grep ':4000'"
# Returns empty if nothing listening
```

**Use this check**:
- Before declaring test passed
- To differentiate Docker proxy from real server
- For detailed diagnostics

**Pros:**
- Accurate detection of server state
- Can fail tests for non-listening servers
- Works with port mappings

**Cons:**
- Adds complexity
- Requires ss/netstat in container
- Timing issues (server might exit between checks)

## Recommended Solution

**Hybrid approach:**

1. **Keep port mappings** (needed for `host.docker.internal` access)
2. **Remove the 500ms wait** in `WaitForPublishConsoleFileDeployment` (already done)
3. **Accept that Docker responds with SYN-ACK** (this is normal)
4. **Tests fail naturally** when server provides no data response
5. **Docker logs show** "Hello World" for diagnosis

**Why this works:**
- Client connects successfully (Docker proxy allows this)
- Client sends request data (proxy forwards attempt)
- Server sends no response (it exited)
- Client gets empty response → test fails
- This is the **correct grading outcome**

The "proxy" is not preventing correct grading - it's just Docker's standard port publishing behavior. The tests are still failing correctly for broken student code.

## Key Insight

**The "ghost server" was never a bug to fix.** It's Docker's port publishing working as designed. The grading system is already handling this correctly:

1. Student's broken server exits
2. Client connects (Docker proxy allows)
3. Client gets no data response
4. Test fails (correct outcome!)
5. Docker logs preserved for debugging

The network flow showing SYN-ACK is a red herring - it doesn't affect the correctness of grading.

## Testing Verification

To verify Docker's behavior:

```bash
# Test 1: Port mapped, nothing listening
docker run -d --name test1 -p 7000:7000 fptuxaes/aes-dotnet8-console:latest
nc -zv localhost 7000
# Result: Connection succeeded (Docker proxy)

# Test 2: No port mapping
docker run -d --name test2 fptuxaes/aes-dotnet8-console:latest
nc -zv localhost 7001
# Result: Connection refused (no Docker proxy)

docker rm -f test1 test2
```

## Conclusion

The "proxy behavior" is **Docker's port publishing mechanism**, not NGINX, not a bug, and not something that needs fixing. The grading system correctly fails students whose servers don't respond with data, regardless of Docker's SYN-ACK behavior at the connection level.

**No code changes needed** - the system works correctly as is. The network flow showing SYN-ACK simply reflects how Docker's port publishing works.
