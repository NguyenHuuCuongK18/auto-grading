#!/bin/bash
# Unified Container Control Script
# Manages server and client processes via supervisorctl
# Called from DockerGradingService to execute test case actions

ACTION=$1
STAGE=$2
SERVER_DLL=${3:-"Server.dll"}
CLIENT_DLL=${4:-"Client.dll"}

# Export environment variables for supervisord
export STAGE=$STAGE
export SERVER_DLL=$SERVER_DLL
export CLIENT_DLL=$CLIENT_DLL

case "$ACTION" in
    StartServer)
        echo "[$(date)] Starting server for stage $STAGE"
        supervisorctl -c /etc/supervisor/supervisord.conf start server
        # Wait for server to bind to port
        sleep 1
        ;;
    
    StartClient)
        echo "[$(date)] Starting client for stage $STAGE"
        supervisorctl -c /etc/supervisor/supervisord.conf start client
        ;;
    
    CloseServer)
        echo "[$(date)] Stopping server for stage $STAGE"
        supervisorctl -c /etc/supervisor/supervisord.conf stop server
        ;;
    
    CloseClient)
        echo "[$(date)] Stopping client for stage $STAGE"
        supervisorctl -c /etc/supervisor/supervisord.conf stop client
        ;;
    
    RestartServer)
        echo "[$(date)] Restarting server for stage $STAGE"
        supervisorctl -c /etc/supervisor/supervisord.conf restart server
        sleep 1
        ;;
    
    RestartClient)
        echo "[$(date)] Restarting client for stage $STAGE"
        supervisorctl -c /etc/supervisor/supervisord.conf restart client
        ;;
    
    Status)
        supervisorctl -c /etc/supervisor/supervisord.conf status
        ;;
    
    *)
        echo "Unknown action: $ACTION"
        echo "Valid actions: StartServer, StartClient, CloseServer, CloseClient, RestartServer, RestartClient, Status"
        exit 1
        ;;
esac
