#!/bin/bash
# Client wrapper script - redirects all output to a unified log file
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

# Run dotnet with stdin from pipe and output redirected to unified log file
# The C# code will read this file incrementally after each action to separate by stage
exec dotnet "$DLL" < /tmp/client_input >> "/apps/client/client.log" 2>&1
