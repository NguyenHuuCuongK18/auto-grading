#!/bin/bash
# Server wrapper script - reads STAGE and TESTCASE from files and redirects logs accordingly

# Read STAGE from file (default to 0 if not set)
STAGE=0
if [ -f /tmp/server_stage ]; then
    STAGE=$(cat /tmp/server_stage)
fi

# Read TESTCASE from file (default to "default" if not set)
TESTCASE="default"
if [ -f /tmp/server_testcase ]; then
    TESTCASE=$(cat /tmp/server_testcase)
fi

# Export for dotnet process
export STAGE
export TESTCASE
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

# Run dotnet with output redirected to test-case-specific and stage-specific log file
exec dotnet "$DLL" >> "/apps/server/server-${TESTCASE}-stage-${STAGE}.log" 2>&1
