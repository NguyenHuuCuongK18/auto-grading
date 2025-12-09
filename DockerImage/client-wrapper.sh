#!/bin/bash
# Client wrapper script - reads STAGE from file and redirects logs accordingly

# Read STAGE from file (default to 0 if not set)
STAGE=0
if [ -f /tmp/client_stage ]; then
    STAGE=$(cat /tmp/client_stage)
fi

# Export for dotnet process
export STAGE
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_RUNNING_IN_CONTAINER=true
export DOTNET_SYSTEM_CONSOLE_UNBUFFERED=1

# Change to client directory
cd /apps/client

# Find the DLL to run
# CRITICAL: Find client DLL by excluding system DLLs and any DLL that exists in /apps/server
# This handles Grade_Content scenarios where golden client is in /apps/client
DLL=$(find . -maxdepth 1 -name '*.dll' -not -name 'System.*.dll' -not -name 'Microsoft.*.dll' | while read dll; do
    basename_dll=$(basename "$dll")
    # Skip this DLL if it exists in /apps/server (it's the server DLL, not client)
    if [ ! -f "/apps/server/$basename_dll" ]; then
        echo "$dll"
        break
    fi
done | head -1)

if [ -z "$DLL" ]; then
    echo "ERROR: No client DLL found in /apps/client"
    exit 1
fi

# Run dotnet with stdin from pipe and output redirected to stage-specific log file
exec dotnet "$DLL" < /tmp/client_input >> "/apps/client/client-stage-${STAGE}.log" 2>&1
