#!/bin/sh
# Unified Container Entrypoint
# This script runs BEFORE Supervisord to ensure the named pipe is ready
# Uses File Descriptor (FD) lock for robust pipe persistence

PIPE_PATH="/tmp/client_input"

echo "[Entrypoint] Setting up the Golden Pipe..."

# 1. Clean and Create
rm -f "$PIPE_PATH"
mkfifo "$PIPE_PATH"
chmod 777 "$PIPE_PATH"

# 2. THE FD LOCK: Open the pipe on File Descriptor 3
# This attaches the pipe to the Entrypoint shell itself (PID 1)
# '<>' opens it for both reading and writing, keeping it 'busy'
# No background process needed - the shell itself holds the lock
# This FD will be inherited by Supervisord when we exec
exec 3<> "$PIPE_PATH"

echo "[Entrypoint] Pipe is locked on FD 3. It will NEVER close."
echo "[Entrypoint] Pipe verification: $(ls -l $PIPE_PATH)"
echo "[Entrypoint] Starting Supervisord..."

# 3. Start Supervisord
# exec replaces the shell process, so Supervisord becomes PID 1
# Supervisord inherits the open FD 3, keeping the pipe alive
# No race conditions, no background process to manage
exec /usr/bin/supervisord -c /etc/supervisor/conf.d/supervisord.conf
