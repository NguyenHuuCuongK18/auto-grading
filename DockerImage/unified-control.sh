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

# Helper function to update stage environment variable in supervisord
update_stage_env() {
    local program=$1
    local stage=$2
    
    # Update the STAGE environment variable for the program
    # This requires updating the supervisord config and reloading
    # For simplicity, we'll use a different approach: set environment before starting
    
    # Export for child processes
    export STAGE=$stage
}

case "$ACTION" in
    StartServer)
        echo "[Control] Starting server for stage $STAGE"
        
        # Stop if already running
        $SUPERVISORCTL stop server 2>/dev/null || true
        sleep 0.2
        
        # Update stage environment variable
        # We need to update the supervisord config to use the new STAGE value
        # Use sed to update the STAGE value in the environment line
        sed -i "s/environment=\(.*\),STAGE=[0-9]*/environment=\1,STAGE=$STAGE/" /etc/supervisor/conf.d/supervisord.conf
        
        # Reread and update supervisord config
        $SUPERVISORCTL reread 2>/dev/null || true
        $SUPERVISORCTL update 2>/dev/null || true
        
        # Start server with new stage
        $SUPERVISORCTL start server
        
        if wait_for_start server; then
            echo "[Control] Server started successfully for stage $STAGE (logging to /apps/server/server-stage-$STAGE.log)"
            # Wait for server to bind to port
            sleep 1
        else
            echo "[Control] WARNING: Server may not have started for stage $STAGE"
        fi
        ;;
    
    StartClient)
        echo "[Control] Starting client for stage $STAGE"
        
        # Named pipe is created by the entrypoint script before Supervisord starts
        # The pipe is held open by File Descriptor 3 in PID 1 (Supervisord)
        # No background process needed - the FD lock is permanent
        # Just verify the pipe exists
        if [ ! -p /tmp/client_input ]; then
            echo "[Control] ERROR: Named pipe /tmp/client_input does not exist!"
            echo "[Control] The entrypoint should have created it. This is a critical error."
            exit 1
        fi
        
        echo "[Control] Named pipe verified (held open by FD 3 in PID 1)"
        
        # Stop client if already running
        $SUPERVISORCTL stop client 2>/dev/null || true
        sleep 0.2
        
        # Update stage environment variable
        sed -i "s/environment=\(.*\),STAGE=[0-9]*/environment=\1,STAGE=$STAGE/" /etc/supervisor/conf.d/supervisord.conf
        
        # Reread and update supervisord config
        $SUPERVISORCTL reread 2>/dev/null || true
        $SUPERVISORCTL update 2>/dev/null || true
        
        # Start client with new stage
        $SUPERVISORCTL start client
        
        if wait_for_start client; then
            echo "[Control] Client started successfully for stage $STAGE (logging to /apps/client/client-stage-$STAGE.log)"
            echo "[Control] Client is reading from named pipe /tmp/client_input"
        else
            echo "[Control] WARNING: Client may not have started for stage $STAGE"
        fi
        ;;
    
    SendInput)
        # Send input to the client process via named pipe
        # Input is provided as third parameter
        INPUT="${3:-}"
        
        echo "[Control] Sending input to client via named pipe: '$INPUT'"
        
        # Ensure named pipe exists
        if [ ! -p /tmp/client_input ]; then
            echo "[Control] ERROR: Named pipe /tmp/client_input does not exist!"
            echo "[Control] Client must be started with StartClient first"
            exit 1
        fi
        
        # Write input to the named pipe
        # The client will immediately read this and process it
        echo -e "${INPUT}" > /tmp/client_input
        
        echo "[Control] Input sent successfully to named pipe"
        
        # Give the client time to process the input
        sleep 0.5
        ;;
    
    CloseServer)
        echo "[Control] Stopping server for stage $STAGE"
        $SUPERVISORCTL stop server
        sleep 0.2
        ;;
    
    CloseClient)
        echo "[Control] Stopping client for stage $STAGE"
        $SUPERVISORCTL stop client
        sleep 0.2
        ;;
    
    RestartServer)
        echo "[Control] Restarting server for stage $STAGE"
        
        # Update stage environment variable
        sed -i "s/environment=\(.*\),STAGE=[0-9]*/environment=\1,STAGE=$STAGE/" /etc/supervisor/conf.d/supervisord.conf
        $SUPERVISORCTL reread 2>/dev/null || true
        $SUPERVISORCTL update 2>/dev/null || true
        
        $SUPERVISORCTL restart server
        sleep 1
        ;;
    
    RestartClient)
        echo "[Control] Restarting client for stage $STAGE"
        
        # Update stage environment variable
        sed -i "s/environment=\(.*\),STAGE=[0-9]*/environment=\1,STAGE=$STAGE/" /etc/supervisor/conf.d/supervisord.conf
        $SUPERVISORCTL reread 2>/dev/null || true
        $SUPERVISORCTL update 2>/dev/null || true
        
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
        echo "Valid actions: StartServer, StartClient, SendInput, CloseServer, CloseClient, RestartServer, RestartClient, Status, StopAll"
        exit 1
        ;;
esac

exit 0
