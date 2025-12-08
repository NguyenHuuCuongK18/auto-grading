#!/bin/sh
# Unified Container Entrypoint
# This script runs BEFORE Supervisord to ensure the named pipe is ready

PIPE_PATH="/tmp/client_input"

echo "[Entrypoint] Phase 1: Creating the Named Pipe..."
# 1. Force remove old pipe to ensure a clean state
rm -f "$PIPE_PATH"

# 2. Create the pipe
mkfifo "$PIPE_PATH"

# 3. Open permissions (crucial for non-root users)
chmod 777 "$PIPE_PATH"

echo "[Entrypoint] Phase 2: Starting the Keeper..."
# 4. Start the Keeper in the background
# tail -f /dev/null is reliable and explicitly holds the file descriptor open
tail -f /dev/null > "$PIPE_PATH" &
KEEPER_PID=$!

echo "[Entrypoint] Phase 3: Verifying the Keeper..."
# 5. THE BLOCKING CHECK - ensures the pipe is actually open by a writer
# We wait until the keeper is confirmed running before starting Supervisord
for i in $(seq 1 10); do
    # Check if the PID is still alive
    if ! kill -0 "$KEEPER_PID" 2>/dev/null; then
        echo "[Entrypoint] ERROR: Keeper died! Exiting."
        exit 1
    fi
    
    # Brief delay to ensure the keeper has opened the pipe
    # This prevents the race condition where client starts before writer is ready
    sleep 0.5
done

echo "[Entrypoint] Phase 4: Pipe is ready (Keeper PID: $KEEPER_PID)"
echo "[Entrypoint] Pipe verification: $(ls -l $PIPE_PATH)"
echo "[Entrypoint] Starting Supervisord..."

# 6. Start Supervisord
# exec replaces the shell process, so Supervisord becomes PID 1
# The keeper continues running as a background process
exec /usr/bin/supervisord -c /etc/supervisor/conf.d/supervisord.conf
