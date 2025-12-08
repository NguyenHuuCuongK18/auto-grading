#!/bin/sh
# Unified Container Entrypoint
# This script runs BEFORE Supervisord to ensure the named pipe is ready

PIPE_PATH="/tmp/client_input"

echo "[Entrypoint] Phase 1: Creating the Named Pipe..."
rm -f "$PIPE_PATH"
mkfifo "$PIPE_PATH"
chmod 777 "$PIPE_PATH"

echo "[Entrypoint] Phase 2: Starting the Keeper Process..."
# CRITICAL FIX: 'sleep infinity' does NOT work on Alpine/BusyBox!
# BusyBox sleep requires an integer, not the word 'infinity'
# Use a large number (31536000 = 1 year in seconds) instead
# This holds the write-end of the pipe open, preventing EOF for clients
# We redirect stdout to the pipe, which counts as a "Writer"
# This keeper process SURVIVES when Supervisord takes over (unlike FD 3 which gets closed)
sleep 31536000 > "$PIPE_PATH" &
KEEPER_PID=$!

echo "[Entrypoint] Phase 3: Verifying Keeper (PID: $KEEPER_PID)..."
# Wait briefly to ensure the kernel registers the writer
sleep 1

if ! kill -0 "$KEEPER_PID" 2>/dev/null; then
    echo "[Entrypoint] CRITICAL ERROR: Keeper process died immediately!"
    echo "[Entrypoint] (Check if 'sleep infinity' is unsupported - use numeric value instead)"
    exit 1
fi

echo "[Entrypoint] Keeper is alive. Pipe is held open."
echo "[Entrypoint] Pipe verification: $(ls -l $PIPE_PATH)"
echo "[Entrypoint] Starting Supervisord..."

# exec replaces the shell, but the 'sleep infinity' process remains running in the background
# Supervisord closes FD 3 but does NOT kill background processes
exec /usr/bin/supervisord -c /etc/supervisor/conf.d/supervisord.conf
