#!/bin/bash
# Unified Container Control Script
# Manages server and client processes via supervisorctl
# Called from DockerGradingService to execute test case actions
#
# Usage: unified-control.sh <ACTION> [STAGE]
# Actions: StartServer, StartClient, CloseServer, CloseClient, Status

ACTION=$1
STAGE=${2:-0}

# Use Unix socket for supervisorctl
SUPERVISORCTL="supervisorctl -c /etc/supervisor/conf.d/supervisord.conf"

# Helper function to wait for process to actually start
wait_for_start() {
    local program=$1
    local max_wait=5
    local count=0
    
    while [ $count -lt $max_wait ]; do
        if $SUPERVISORCTL status $program | grep -q "RUNNING"; then
            return 0
        fi
        sleep 0.5
        ((count++))
    done
    return 1
}

# Helper function to mark stage in log
mark_stage() {
    local program=$1
    local stage=$2
    echo "=== STAGE $stage START $(date) ===" >> /logs/${program}.log
}

case "$ACTION" in
    StartServer)
        echo "[Control] Starting server for stage $STAGE"
        mark_stage "server" "$STAGE"
        
        # Stop if already running
        $SUPERVISORCTL stop server 2>/dev/null || true
        sleep 0.2
        
        # Start server
        $SUPERVISORCTL start server
        
        if wait_for_start server; then
            echo "[Control] Server started successfully for stage $STAGE"
            # Wait for server to bind to port
            sleep 1
        else
            echo "[Control] WARNING: Server may not have started for stage $STAGE"
        fi
        ;;
    
    StartClient)
        echo "[Control] Starting client for stage $STAGE"
        mark_stage "client" "$STAGE"
        
        # Stop if already running
        $SUPERVISORCTL stop client 2>/dev/null || true
        sleep 0.2
        
        # Start client
        $SUPERVISORCTL start client
        
        if wait_for_start client; then
            echo "[Control] Client started successfully for stage $STAGE"
        else
            echo "[Control] WARNING: Client may not have started for stage $STAGE"
        fi
        ;;
    
    CloseServer)
        echo "[Control] Stopping server for stage $STAGE"
        $SUPERVISORCTL stop server
        echo "=== STAGE $STAGE END $(date) ===" >> /logs/server.log
        sleep 0.2
        ;;
    
    CloseClient)
        echo "[Control] Stopping client for stage $STAGE"
        $SUPERVISORCTL stop client
        echo "=== STAGE $STAGE END $(date) ===" >> /logs/client.log
        sleep 0.2
        ;;
    
    RestartServer)
        echo "[Control] Restarting server for stage $STAGE"
        mark_stage "server" "$STAGE"
        $SUPERVISORCTL restart server
        sleep 1
        ;;
    
    RestartClient)
        echo "[Control] Restarting client for stage $STAGE"
        mark_stage "client" "$STAGE"
        $SUPERVISORCTL restart client
        ;;
    
    Status)
        $SUPERVISORCTL status
        ;;
    
    StopAll)
        echo "[Control] Stopping all processes"
        $SUPERVISORCTL stop all
        ;;
    
    *)
        echo "Unknown action: $ACTION"
        echo "Valid actions: StartServer, StartClient, CloseServer, CloseClient, RestartServer, RestartClient, Status, StopAll"
        exit 1
        ;;
esac

exit 0
