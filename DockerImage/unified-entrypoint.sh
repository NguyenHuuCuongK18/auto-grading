#!/bin/sh
# Unified Container Entrypoint

PIPE_PATH="/tmp/client_input"

echo "[Entrypoint] Creating the Named Pipe..."
rm -f "$PIPE_PATH"
mkfifo "$PIPE_PATH"
chmod 777 "$PIPE_PATH"

echo "[Entrypoint] Pipe created: $PIPE_PATH"
echo "[Entrypoint] Supervisord will start the keeper process to hold pipe open"
echo "[Entrypoint] Starting Supervisord..."

# Supervisord manages the keeper process as a first-class citizen
# No background processes here - exec cleanly replaces this shell with Supervisord
exec /usr/bin/supervisord -c /etc/supervisor/conf.d/supervisord.conf
