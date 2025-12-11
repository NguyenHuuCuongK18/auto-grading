#!/bin/bash
# Server wrapper script - redirects all output to a unified log file
# The C# grading service will read this file incrementally to separate output by stage

# Export environment variables for dotnet process
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_RUNNING_IN_CONTAINER=true
export DOTNET_SYSTEM_CONSOLE_UNBUFFERED=1

# TCP 4-WAY HANDSHAKE ENFORCEMENT:
# LD_PRELOAD injects libdelay_close.so which intercepts close() syscall
# and adds 50ms delay before closing. This forces Linux to send the ACK
# for any received FIN before we send our own FIN, producing proper 4-way close:
#   FIN-ACK -> ACK -> FIN-ACK -> ACK (instead of 3-way: FIN-ACK -> FIN-ACK -> ACK)
export LD_PRELOAD=/usr/lib/libdelay_close.so

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
