#!/bin/bash
# Server wrapper script - redirects all output to a unified log file
# The C# grading service will read this file incrementally to separate output by stage

# Export environment variables for dotnet process
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_RUNNING_IN_CONTAINER=true
export DOTNET_SYSTEM_CONSOLE_UNBUFFERED=1

# Change to server directory
cd /apps/server

# Find the DLL to run
DLL=$(find . -name '*.dll' -not -name 'System.*.dll' -not -name 'Microsoft.*.dll' | head -1)

if [ -z "$DLL" ]; then
    echo "ERROR: No server DLL found in /apps/server"
    exit 1
fi

# Run dotnet with output redirected to unified log file
# The C# code will read this file incrementally after each action to separate by stage
exec dotnet "$DLL" >> "/apps/server/server.log" 2>&1
