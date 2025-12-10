#!/bin/sh
# Unified Container Entrypoint

PIPE_PATH="/tmp/client_input"

echo "[Entrypoint] Phase 1: Creating the Named Pipe..."
# Ensure clean state
rm -f "$PIPE_PATH"
mkfifo "$PIPE_PATH"
# Permissions for non-root users (if needed)
chmod 777 "$PIPE_PATH"

echo "[Entrypoint] Phase 2: Handing over to Supervisord..."
# We do NOT start the keeper here. Supervisord (PID 1) will start it immediately.
# The keeper is a first-class supervisord process with priority=1
exec /usr/bin/supervisord -c /etc/supervisor/conf.d/supervisord.conf
