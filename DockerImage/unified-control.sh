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
        
        # Stop if already running
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
        else
            echo "[Control] WARNING: Client may not have started for stage $STAGE"
        fi
        ;;
    
    SendInput)
        # Send input to the client process by restarting it with input piped
        # Input is provided as third parameter
        INPUT="${3:-}"
        
        echo "[Control] Providing input to client: '$INPUT'"
        
        # Stop the current client if running
        $SUPERVISORCTL stop client 2>/dev/null || true
        sleep 0.5
        
        # Write input to a file
        INPUT_FILE="/tmp/client-input-${STAGE}.txt"
        echo -e "${INPUT}" > "$INPUT_FILE"
        
        # Update supervisord config to pipe input file to client
        # We need to find and replace the entire command line
        # First, backup the original config
        cp /etc/supervisor/conf.d/supervisord.conf /etc/supervisor/conf.d/supervisord.conf.bak
        
        # Use awk to replace the client command line properly
        # CRITICAL: Input redirection must come AFTER the entire find|head|xargs pipeline
        # Use sh -c to ensure proper parsing: sh -c 'dotnet file.dll < input.txt'
        awk -v input_file="$INPUT_FILE" -v stage="$STAGE" '
        /^\[program:client\]/ { in_client=1; print; next }
        /^\[program:/ && in_client { in_client=0 }
        in_client && /^command=/ { 
            print "command=/bin/bash -c \"cd /apps/client && DLL=\\$(find . -maxdepth 1 -name '\\''*.dll'\\'' -not -name '\\''System.*.dll'\\'' -not -name '\\''Microsoft.*.dll'\\'' | head -1) && dotnet \\$DLL < " input_file "\""
            next 
        }
        in_client && /^environment=/ {
            sub(/STAGE=[0-9]*/, "STAGE=" stage)
        }
        { print }
        ' /etc/supervisor/conf.d/supervisord.conf.bak > /etc/supervisor/conf.d/supervisord.conf
        
        # Reread and update
        $SUPERVISORCTL reread 2>/dev/null || true
        $SUPERVISORCTL update 2>/dev/null || true
        
        # Start client with input
        $SUPERVISORCTL start client
        
        echo "[Control] Client restarted with input from ${INPUT_FILE}"
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
