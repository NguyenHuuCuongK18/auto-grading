#!/bin/sh
# Unified Container Entrypoint

PIPE_PATH="/tmp/client_input"

echo "[Entrypoint] Phase 1: Creating the Named Pipe..."
# Ensure clean state
rm -f "$PIPE_PATH"
mkfifo "$PIPE_PATH"
# Permissions for non-root users (if needed)
chmod 777 "$PIPE_PATH"

echo "[Entrypoint] Phase 2: Forcing 4-way TCP handshake (quickack)..."
# CRITICAL: Force Linux to use standard 4-way TCP close instead of 3-way close
#
# PROBLEM:
# Linux TCP stack combines ACK with FIN into a single FIN-ACK packet (3-way close)
# Windows TCP stack sends them separately: ACK then FIN-ACK (4-way close)
# When testkit is generated on Windows but grading runs on Linux, packet counts differ
#
# SOLUTION:
# Set 'quickack 1' on the loopback route to disable TCP Delayed ACK
# This forces the kernel to send ACK immediately instead of waiting to piggyback it
#
# HOW IT WORKS:
# Normal Linux:   Receive FIN -> Wait 40ms (Timer) -> App Closes -> Send FIN+ACK (Merged)
# With QuickAck:  Receive FIN -> Send ACK Instantly -> App Closes -> Send FIN
# Result: Clean FIN, ACK, FIN, ACK sequence (4-way handshake)
#
# Reference: https://man7.org/linux/man-pages/man7/ip.7.html (quickack option)
#
# Apply quickack to loopback interface (where client/server communicate via 127.0.0.1)
# The container must be run with --cap-add=NET_ADMIN for this to work
if command -v ip >/dev/null 2>&1; then
    # Check current loopback route
    LOOPBACK_ROUTE=$(ip route show table local | grep "127.0.0.0/8" | head -1)
    if [ -n "$LOOPBACK_ROUTE" ]; then
        echo "[Entrypoint] Current loopback route: $LOOPBACK_ROUTE"
        # Modify loopback route to enable quickack
        # Format: local 127.0.0.0/8 dev lo proto kernel scope host src 127.0.0.1
        ip route change local 127.0.0.0/8 dev lo scope host quickack 1 2>/dev/null && \
            echo "[Entrypoint] SUCCESS: Enabled quickack on loopback for proper 4-way TCP close" || \
            echo "[Entrypoint] Warning: Could not modify loopback route, trying default route..."
    fi
    
    # Also try to set quickack on default route if it exists (for external connections)
    DEFAULT_GW=$(ip route show default | awk '{print $3}')
    DEFAULT_DEV=$(ip route show default | awk '{print $5}')
    if [ -n "$DEFAULT_GW" ] && [ -n "$DEFAULT_DEV" ]; then
        ip route change default via "$DEFAULT_GW" dev "$DEFAULT_DEV" quickack 1 2>/dev/null && \
            echo "[Entrypoint] Enabled quickack on default route ($DEFAULT_DEV via $DEFAULT_GW)" || \
            echo "[Entrypoint] Note: Could not modify default route (this is OK for localhost-only tests)"
    fi
else
    echo "[Entrypoint] Warning: 'ip' command not found, cannot enable quickack"
fi

echo "[Entrypoint] Phase 3: Handing over to Supervisord..."
# We do NOT start the keeper here. Supervisord (PID 1) will start it immediately.
# The keeper is a first-class supervisord process with priority=1
exec /usr/bin/supervisord -c /etc/supervisor/conf.d/supervisord.conf
